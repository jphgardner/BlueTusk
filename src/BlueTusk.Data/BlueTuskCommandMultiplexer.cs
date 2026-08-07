using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Threading.Tasks.Sources;
using BlueTusk.Client;
using BlueTusk.Diagnostics;

namespace BlueTusk.Data;

internal sealed class BlueTuskCommandMultiplexer : IDisposable, IAsyncDisposable
{
    private readonly BlueTuskDataSource _dataSource;
    private readonly ResolvedMultiplexingOptions _options;
    private readonly Channel<IMultiplexedCommandRequest> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;
    private readonly BlueTuskConnection?[] _workerConnections;
    private int _disposed;
    private int _queued;
    private int _executing;
    private long _accepted;
    private long _completed;
    private long _canceled;
    private long _faulted;
    private long _pipelineFlushes;
    private long _pipelinedCommands;

    internal BlueTuskCommandMultiplexer(
        BlueTuskDataSource dataSource,
        ResolvedMultiplexingOptions options)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _options = options;
        _queue = Channel.CreateBounded<IMultiplexedCommandRequest>(
            new BoundedChannelOptions(options.QueueCapacity)
            {
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = options.WorkerCount == 1,
                SingleWriter = false,
            });
        _workerConnections = new BlueTuskConnection?[options.WorkerCount];
        _workers = Enumerable.Range(0, options.WorkerCount)
            .Select(index => Task.Run(() => WorkerAsync(index)))
            .ToArray();
    }

    internal BlueTuskMultiplexingStatistics Statistics => new(
        Enabled: true,
        Workers: _workers.Length,
        Queued: Volatile.Read(ref _queued),
        Executing: Volatile.Read(ref _executing),
        Accepted: Interlocked.Read(ref _accepted),
        Completed: Interlocked.Read(ref _completed),
        Canceled: Interlocked.Read(ref _canceled),
        Faulted: Interlocked.Read(ref _faulted),
        PipelineFlushes: Interlocked.Read(ref _pipelineFlushes),
        PipelinedCommands: Interlocked.Read(ref _pipelinedCommands));

    internal ValueTask<BlueTuskQueryResult> ExecuteAsync(
        BlueTuskCommand command,
        Action<BlueTuskConnection> startTelemetry,
        CancellationToken cancellationToken)
        => EnqueueAsync(new MultiplexedCommandRequest<BlueTuskQueryResult>(
            command,
            startTelemetry,
            scalar: false,
            cancellationToken), cancellationToken);

    internal ValueTask<BlueTuskScalarQueryResult> ExecuteScalarAsync(
        BlueTuskCommand command,
        Action<BlueTuskConnection> startTelemetry,
        CancellationToken cancellationToken)
        => EnqueueAsync(new MultiplexedCommandRequest<BlueTuskScalarQueryResult>(
            command,
            startTelemetry,
            scalar: true,
            cancellationToken), cancellationToken);

    private ValueTask<T> EnqueueAsync<T>(
        MultiplexedCommandRequest<T> request,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            request.RecordQueueWait();
            request.Dispose();
            BlueTuskDiagnostics.RecordMultiplexingAdmission("closed");
            throw new ObjectDisposedException(nameof(BlueTuskDataSource));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            request.RecordQueueWait();
            request.Dispose();
            BlueTuskDiagnostics.RecordMultiplexingAdmission("canceled");
            cancellationToken.ThrowIfCancellationRequested();
        }

        Interlocked.Increment(ref _queued);
        BlueTuskDiagnostics.RecordMultiplexingPendingDelta(1);
        var writing = _queue.Writer.WriteAsync(request, cancellationToken);
        if (writing.IsCompletedSuccessfully)
        {
            RecordAccepted();
            return request.AsValueTask();
        }

        return CompleteEnqueueAsync(writing, request);
    }

    private async ValueTask<T> CompleteEnqueueAsync<T>(
        ValueTask writing,
        MultiplexedCommandRequest<T> request)
    {
        try
        {
            await writing.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            DecrementQueued();
            request.RecordQueueWait();
            request.Dispose();
            BlueTuskDiagnostics.RecordMultiplexingAdmission("canceled");
            throw;
        }
        catch (ChannelClosedException exception)
        {
            DecrementQueued();
            request.RecordQueueWait();
            request.Dispose();
            BlueTuskDiagnostics.RecordMultiplexingAdmission("closed");
            throw new ObjectDisposedException(nameof(BlueTuskDataSource), exception);
        }

        RecordAccepted();
        return await request.AsValueTask().ConfigureAwait(false);
    }

    private void RecordAccepted()
    {
        Interlocked.Increment(ref _accepted);
        BlueTuskDiagnostics.RecordMultiplexingAdmission("accepted");
    }

    private void DecrementQueued()
    {
        Interlocked.Decrement(ref _queued);
        BlueTuskDiagnostics.RecordMultiplexingPendingDelta(-1);
    }

    private static void RecordOutcome(string outcome) =>
        BlueTuskDiagnostics.RecordMultiplexingCommandOutcome(outcome);

    private void BeginExecuting()
    {
        Interlocked.Increment(ref _executing);
        BlueTuskDiagnostics.RecordMultiplexingExecutingDelta(1);
    }

    private void EndExecuting()
    {
        Interlocked.Decrement(ref _executing);
        BlueTuskDiagnostics.RecordMultiplexingExecutingDelta(-1);
    }

    private async Task WorkerAsync(int workerIndex)
    {
        BlueTuskConnection? connection = null;
        var buffers = new PipelineWorkerBuffers(_options.MaxPipelineCommands);
        var processedOnLease = 0;
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_shutdown.Token).ConfigureAwait(false))
            {
                var result = await ExecuteLeaseAsync(
                    connection,
                    workerIndex,
                    _options.MaxCommandsPerLease - processedOnLease,
                    buffers).ConfigureAwait(false);
                connection = result.Connection;
                Volatile.Write(ref _workerConnections[workerIndex], connection);
                processedOnLease += result.Processed;
                if (processedOnLease >= _options.MaxCommandsPerLease)
                {
                    if (connection is not null)
                    {
                        Volatile.Write(ref _workerConnections[workerIndex], null);
                        await connection.DisposeAsync().ConfigureAwait(false);
                        connection = null;
                    }

                    processedOnLease = 0;
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            CancelQueued();
        }
        finally
        {
            Volatile.Write(ref _workerConnections[workerIndex], null);
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<LeaseExecutionResult> ExecuteLeaseAsync(
        BlueTuskConnection? connection,
        int workerIndex,
        int commandLimit,
        PipelineWorkerBuffers buffers)
    {
        IMultiplexedCommandRequest? pending = null;
        var processed = 0;
        try
        {
            while (processed < commandLimit)
            {
                var request = pending;
                pending = null;
                if (request is null)
                {
                    if (!_queue.Reader.TryRead(out request))
                    {
                        return new LeaseExecutionResult(connection, processed);
                    }

                    DecrementQueued();
                    if (!request.TryBeginExecution())
                    {
                        Interlocked.Increment(ref _canceled);
                        RecordOutcome("canceled");
                        processed++;
                        continue;
                    }
                }

                if (connection is null)
                {
                    try
                    {
                        connection = await _dataSource
                            .OpenMultiplexingConnectionAsync(_shutdown.Token)
                            .ConfigureAwait(false);
                        Volatile.Write(ref _workerConnections[workerIndex], connection);
                    }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                    {
                        request.TrySetException(
                            new ObjectDisposedException(nameof(BlueTuskDataSource)));
                        Interlocked.Increment(ref _canceled);
                        RecordOutcome("canceled");
                        processed++;
                        continue;
                    }
                    catch (Exception exception)
                    {
                        request.TrySetException(exception);
                        Interlocked.Increment(ref _faulted);
                        RecordOutcome("faulted");
                        processed++;
                        continue;
                    }
                }

                if (request.Command.CanUseMultiplexedPipeline)
                {
                    var batchCount = 1;
                    buffers.Requests[0] = request;
                    while (batchCount < _options.MaxPipelineCommands &&
                           processed + batchCount < commandLimit &&
                           _queue.Reader.TryRead(out var candidate))
                    {
                        DecrementQueued();
                        if (!candidate.TryBeginExecution())
                        {
                            Interlocked.Increment(ref _canceled);
                            RecordOutcome("canceled");
                            processed++;
                            continue;
                        }

                        if (!candidate.Command.CanUseMultiplexedPipeline)
                        {
                            pending = candidate;
                            break;
                        }

                        buffers.Requests[batchCount++] = candidate;
                    }

                    await ExecutePipelineAsync(connection, buffers, batchCount).ConfigureAwait(false);
                    processed += batchCount;
                }
                else
                {
                    await ExecuteSingleAsync(connection, request).ConfigureAwait(false);
                    processed++;
                }

                if (!connection.HasOpenSession)
                {
                    Volatile.Write(ref _workerConnections[workerIndex], null);
                    await connection.DisposeAsync().ConfigureAwait(false);
                    connection = null;
                }
            }
        }
        finally
        {
            if (pending is not null)
            {
                pending.TrySetException(
                    new ObjectDisposedException(nameof(BlueTuskDataSource)));
                Interlocked.Increment(ref _canceled);
                RecordOutcome("canceled");
            }

        }

        return new LeaseExecutionResult(connection, processed);
    }

    private async Task ExecuteSingleAsync(
        BlueTuskConnection connection,
        IMultiplexedCommandRequest request)
    {
        BeginExecuting();
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(
            request.CancellationToken,
            _shutdown.Token);
        try
        {
            var result = await request.Command.ExecuteDispatchedAsync(
                        connection,
                        request.StartTelemetry,
                        execution.Token).ConfigureAwait(false);
            request.TrySetResult(
                request.Scalar
                    ? new MultiplexedCommandResult(
                        QueryResult: null,
                        BlueTuskScalarQueryResult.FromQueryResult(result))
                    : new MultiplexedCommandResult(
                        result,
                        ScalarResult: default));
            Interlocked.Increment(ref _completed);
            RecordOutcome("completed");
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
            request.TrySetCanceled(request.CancellationToken);
            Interlocked.Increment(ref _canceled);
            RecordOutcome("canceled");
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            request.TrySetException(
                new ObjectDisposedException(nameof(BlueTuskDataSource)));
            Interlocked.Increment(ref _canceled);
            RecordOutcome("canceled");
        }
        catch (Exception exception)
        {
            request.TrySetException(exception);
            Interlocked.Increment(ref _faulted);
            RecordOutcome("faulted");
        }
        finally
        {
            EndExecuting();
        }
    }

    private async Task ExecutePipelineAsync(
        BlueTuskConnection connection,
        PipelineWorkerBuffers buffers,
        int requestCount)
    {
        var activeCount = 0;
        var hasCancellationTokens = false;
        try
        {
            for (var requestIndex = 0; requestIndex < requestCount; requestIndex++)
            {
                var request = buffers.Requests[requestIndex];
                try
                {
                    request.StartTelemetry(connection);
                    buffers.Commands[activeCount] = request.Command.CreateMultiplexedPipelineCommand(
                        connection,
                        request.Scalar);
                    if (request.Command.CommandTimeout > 0 ||
                        request.CancellationToken.CanBeCanceled)
                    {
                        var scope = new PipelineCancellationScope(
                            request.Command.CommandTimeout,
                            request.CancellationToken,
                            _shutdown.Token);
                        buffers.Scopes[activeCount] = scope;
                        buffers.CancellationTokens[activeCount] = scope.Token;
                        hasCancellationTokens = true;
                    }
                    else
                    {
                        buffers.Scopes[activeCount] = null;
                        buffers.CancellationTokens[activeCount] = CancellationToken.None;
                    }

                    buffers.Requests[activeCount] = request;
                    activeCount++;
                    BeginExecuting();
                }
                catch (Exception exception)
                {
                    request.TrySetException(exception);
                    Interlocked.Increment(ref _faulted);
                    RecordOutcome("faulted");
                }
            }

            if (activeCount == 0)
            {
                return;
            }

            var activeRequests = new ArraySegment<IMultiplexedCommandRequest>(
                buffers.Requests,
                0,
                activeCount);
            var commands = new ArraySegment<BlueTuskMultiplexedPipelineCommand>(
                buffers.Commands,
                0,
                activeCount);
            IReadOnlyList<CancellationToken>? groupCancellationTokens =
                !hasCancellationTokens
                    ? (IReadOnlyList<CancellationToken>?)null
                    : new ArraySegment<CancellationToken>(
                        buffers.CancellationTokens,
                        0,
                        activeCount);
            try
            {
                Interlocked.Increment(ref _pipelineFlushes);
                Interlocked.Add(ref _pipelinedCommands, activeCount);
                BlueTuskDiagnostics.RecordMultiplexingPipelineSize(activeCount);
                await connection.Session.ExecuteMultiplexedPipelineAsync(
                    commands,
                    groupCancellationTokens,
                    index => activeRequests[index].Command
                        .SetMultiplexedPipelineActiveConnection(connection),
                    index => activeRequests[index].Command
                        .CompleteMultiplexedPipelineExecution(),
                    CompleteOutcome,
                    _shutdown.Token).ConfigureAwait(false);

                void CompleteOutcome(
                    int index,
                    BlueTuskMultiplexedPipelineOutcome outcome)
                {
                    var request = activeRequests[index];
                    var scope = buffers.Scopes[index];
                    if (outcome.Cancellation is not null)
                    {
                        if (request.CancellationToken.IsCancellationRequested)
                        {
                            request.TrySetCanceled(request.CancellationToken);
                        }
                        else if (scope?.TimedOut == true)
                        {
                            request.TrySetException(
                                new TimeoutException(
                                    $"The command exceeded its {request.Command.CommandTimeout}-second timeout.",
                                    outcome.Cancellation));
                        }
                        else
                        {
                            request.TrySetException(
                                new ObjectDisposedException(nameof(BlueTuskDataSource)));
                        }

                        Interlocked.Increment(ref _canceled);
                        RecordOutcome("canceled");
                        return;
                    }

                    if (outcome.Error is not null)
                    {
                        request.TrySetException(
                            request.Command.TranslateMultiplexedPipelineError(outcome.Error));
                        Interlocked.Increment(ref _faulted);
                        RecordOutcome("faulted");
                    }
                    else
                    {
                        request.TrySetResult(
                            new MultiplexedCommandResult(
                                outcome.Result,
                                outcome.ScalarResult));
                        Interlocked.Increment(ref _completed);
                        RecordOutcome("completed");
                    }
                }
            }
            catch (Exception exception)
            {
                foreach (var request in activeRequests)
                {
                    if (request.TrySetException(exception))
                    {
                        Interlocked.Increment(ref _faulted);
                        RecordOutcome("faulted");
                    }
                }
            }
            finally
            {
                for (var index = 0; index < activeCount; index++)
                {
                    activeRequests[index].Command.CompleteMultiplexedPipelineExecution();
                    buffers.Scopes[index]?.Dispose();
                    EndExecuting();
                }
            }
        }
        finally
        {
            for (var index = 0; index < requestCount; index++)
            {
                buffers.Requests[index] = null!;
                buffers.Commands[index] = default;
                buffers.Scopes[index] = null;
                buffers.CancellationTokens[index] = default;
            }
        }
    }

    private void CancelQueued()
    {
        while (_queue.Reader.TryRead(out var request))
        {
            DecrementQueued();
            request.RecordQueueWait();
            request.Dispose();
            request.TrySetException(
                new ObjectDisposedException(nameof(BlueTuskDataSource)));
            Interlocked.Increment(ref _canceled);
            RecordOutcome("canceled");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        try
        {
            Task.WhenAll(_workers).Wait(_options.ShutdownTimeout);
        }
        catch (AggregateException exception) when (
            exception.InnerExceptions.All(static item => item is OperationCanceledException))
        {
        }

        if (_workers.Any(static worker => !worker.IsCompleted))
        {
            BlueTuskDiagnostics.RecordMultiplexingForcedShutdown();
            _shutdown.Cancel();
            AbortWorkerConnections();
            CancelQueued();
            Task.WhenAll(_workers).GetAwaiter().GetResult();
        }

        _shutdown.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        try
        {
            await Task.WhenAll(_workers)
                .WaitAsync(_options.ShutdownTimeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            BlueTuskDiagnostics.RecordMultiplexingForcedShutdown();
            _shutdown.Cancel();
            AbortWorkerConnections();
            CancelQueued();
            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private void AbortWorkerConnections()
    {
        foreach (var connection in _workerConnections)
        {
            try
            {
                connection?.AbortPhysicalSession();
            }
            catch
            {
                // Forced shutdown is best effort; worker completion observes transport failure.
            }
        }
    }

    private interface IMultiplexedCommandRequest : IDisposable
    {
        BlueTuskCommand Command { get; }

        Action<BlueTuskConnection> StartTelemetry { get; }

        bool Scalar { get; }

        CancellationToken CancellationToken { get; }

        bool TrySetResult(MultiplexedCommandResult result);

        bool TrySetException(Exception exception);

        bool TrySetCanceled(CancellationToken cancellationToken);

        bool TryBeginExecution();

        void RecordQueueWait();
    }

    private sealed class MultiplexedCommandRequest<T> :
        IMultiplexedCommandRequest,
        IDisposable,
        IValueTaskSource<T>
    {
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private ManualResetValueTaskSourceCore<T> _completion;
        private int _completionState;
        private long _queuedAt = BlueTuskDiagnostics.GetMultiplexingQueueTimestamp();
        private int _state;

        internal MultiplexedCommandRequest(
            BlueTuskCommand command,
            Action<BlueTuskConnection> startTelemetry,
            bool scalar,
            CancellationToken cancellationToken)
        {
            Command = command;
            StartTelemetry = startTelemetry;
            Scalar = scalar;
            CancellationToken = cancellationToken;
            _completion.RunContinuationsAsynchronously = true;
            if (cancellationToken.CanBeCanceled)
            {
                _cancellationRegistration = cancellationToken.UnsafeRegister(
                    static state => ((MultiplexedCommandRequest<T>)state!).CancelQueued(),
                    this);
            }
        }

        public BlueTuskCommand Command { get; }

        public Action<BlueTuskConnection> StartTelemetry { get; }

        public bool Scalar { get; }

        public CancellationToken CancellationToken { get; }

        internal ValueTask<T> AsValueTask() =>
            new(this, _completion.Version);

        public bool TrySetResult(MultiplexedCommandResult result)
        {
            if (Interlocked.Exchange(ref _completionState, 1) != 0)
            {
                return false;
            }

            if (typeof(T) == typeof(BlueTuskQueryResult))
            {
                var queryResult = result.QueryResult
                    ?? throw new InvalidOperationException(
                        "The multiplexed command did not produce a query result.");
                _completion.SetResult(Unsafe.As<BlueTuskQueryResult, T>(ref queryResult));
            }
            else
            {
                var scalarResult = result.ScalarResult;
                _completion.SetResult(Unsafe.As<BlueTuskScalarQueryResult, T>(ref scalarResult));
            }

            return true;
        }

        public bool TrySetException(Exception exception)
        {
            if (Interlocked.Exchange(ref _completionState, 1) != 0)
            {
                return false;
            }

            _completion.SetException(exception);
            return true;
        }

        public bool TrySetCanceled(CancellationToken cancellationToken) =>
            TrySetException(new OperationCanceledException(cancellationToken));

        public bool TryBeginExecution()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                RecordQueueWait();
                Dispose();
                return false;
            }

            RecordQueueWait();
            Dispose();
            return true;
        }

        public void RecordQueueWait()
        {
            var queuedAt = Interlocked.Exchange(ref _queuedAt, 0);
            BlueTuskDiagnostics.RecordMultiplexingQueueWait(queuedAt);
        }

        private void CancelQueued()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
            {
                TrySetCanceled(CancellationToken);
            }
        }

        public void Dispose() => _cancellationRegistration.Dispose();

        public T GetResult(short token) =>
            _completion.GetResult(token);

        public ValueTaskSourceStatus GetStatus(short token) =>
            _completion.GetStatus(token);

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags) =>
            _completion.OnCompleted(continuation, state, token, flags);
    }

    private readonly record struct MultiplexedCommandResult(
        BlueTuskQueryResult? QueryResult,
        BlueTuskScalarQueryResult ScalarResult);

    private readonly record struct LeaseExecutionResult(
        BlueTuskConnection? Connection,
        int Processed);

    private sealed class PipelineWorkerBuffers
    {
        internal PipelineWorkerBuffers(int capacity)
        {
            Requests = new IMultiplexedCommandRequest[capacity];
            Commands = new BlueTuskMultiplexedPipelineCommand[capacity];
            Scopes = new PipelineCancellationScope?[capacity];
            CancellationTokens = new CancellationToken[capacity];
        }

        internal IMultiplexedCommandRequest[] Requests { get; }

        internal BlueTuskMultiplexedPipelineCommand[] Commands { get; }

        internal PipelineCancellationScope?[] Scopes { get; }

        internal CancellationToken[] CancellationTokens { get; }
    }

    private sealed class PipelineCancellationScope : IDisposable
    {
        private readonly CancellationTokenSource? _timeout;
        private readonly CancellationTokenSource? _linked;

        internal PipelineCancellationScope(
            int commandTimeout,
            CancellationToken requestCancellationToken,
            CancellationToken shutdownToken)
        {
            if (commandTimeout > 0)
            {
                _timeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(commandTimeout));
                _linked = CancellationTokenSource.CreateLinkedTokenSource(
                    requestCancellationToken,
                    shutdownToken,
                    _timeout.Token);
            }
            else if (requestCancellationToken.CanBeCanceled)
            {
                _linked = CancellationTokenSource.CreateLinkedTokenSource(
                    requestCancellationToken,
                    shutdownToken);
            }
        }

        internal CancellationToken Token => _linked?.Token ?? CancellationToken.None;

        internal bool TimedOut => _timeout?.IsCancellationRequested == true;

        public void Dispose()
        {
            _linked?.Dispose();
            _timeout?.Dispose();
        }
    }
}

internal static class BlueTuskMultiplexingClassifier
{
    private static readonly string[] SessionStateTokens =
    [
        "ABORT",
        "BEGIN",
        "CALL",
        "CLOSE",
        "COMMIT",
        "COPY",
        "DECLARE",
        "DEALLOCATE",
        "DISCARD",
        "DO",
        "EXECUTE",
        "FETCH",
        "LISTEN",
        "LOAD",
        "LOCK",
        "MOVE",
        "NOTIFY",
        "PG_TEMP",
        "PREPARE",
        "RELEASE",
        "RESET",
        "ROLLBACK",
        "SAVEPOINT",
        "SET",
        "SHOW",
        "START",
        "TEMP",
        "TEMPORARY",
        "UNLISTEN",
    ];

    private static readonly string[] SessionStateRoutines =
    [
        "CURRENT_SETTING",
        "CURRVAL",
        "LASTVAL",
        "LO_CLOSE",
        "LO_CREAT",
        "LO_CREATE",
        "LO_EXPORT",
        "LO_GET",
        "LO_IMPORT",
        "LO_LSEEK",
        "LO_LSEEK64",
        "LO_OPEN",
        "LO_PUT",
        "LO_READ",
        "LO_TELL",
        "LO_TELL64",
        "LO_TRUNCATE",
        "LO_TRUNCATE64",
        "LO_UNLINK",
        "LO_WRITE",
        "LOREAD",
        "LOWRITE",
        "PG_ADVISORY_LOCK",
        "PG_ADVISORY_LOCK_SHARED",
        "PG_ADVISORY_XACT_LOCK",
        "PG_ADVISORY_XACT_LOCK_SHARED",
        "PG_ADVISORY_UNLOCK",
        "PG_ADVISORY_UNLOCK_ALL",
        "PG_ADVISORY_UNLOCK_SHARED",
        "PG_LISTENING_CHANNELS",
        "PG_MY_TEMP_SCHEMA",
        "PG_TRY_ADVISORY_LOCK",
        "PG_TRY_ADVISORY_LOCK_SHARED",
        "PG_TRY_ADVISORY_XACT_LOCK",
        "PG_TRY_ADVISORY_XACT_LOCK_SHARED",
        "SET_CONFIG",
    ];

    internal static bool IsSessionNeutral(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var text = sql.AsSpan();
        var index = 0;
        var hasToken = false;
        var atStatementStart = true;
        while (index < text.Length)
        {
            var current = text[index];
            if (char.IsWhiteSpace(current) || current is ',' or '(' or ')' or '.')
            {
                index++;
                continue;
            }

            if (current == ';')
            {
                atStatementStart = true;
                index++;
                continue;
            }

            if (current == '-' && index + 1 < text.Length && text[index + 1] == '-')
            {
                index += 2;
                while (index < text.Length && text[index] is not ('\r' or '\n'))
                {
                    index++;
                }

                continue;
            }

            if (current == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                if (!TrySkipBlockComment(text, ref index))
                {
                    return false;
                }

                continue;
            }

            if (current == '\'')
            {
                if (!TrySkipQuoted(text, ref index, '\'', allowBackslashEscape: false))
                {
                    return false;
                }

                continue;
            }

            if (current == '$' && TrySkipDollarQuote(text, ref index, out var closed))
            {
                if (!closed)
                {
                    return false;
                }

                continue;
            }

            if (current == '"')
            {
                var start = ++index;
                var containsEscape = false;
                while (index < text.Length)
                {
                    if (text[index] == '"')
                    {
                        if (index + 1 < text.Length && text[index + 1] == '"')
                        {
                            containsEscape = true;
                            index += 2;
                            continue;
                        }

                        var token = text[start..index];
                        index++;
                        hasToken = true;
                        if (!containsEscape && IsSessionStateToken(token))
                        {
                            return false;
                        }

                        atStatementStart = false;
                        goto ContinueScanning;
                    }

                    index++;
                }

                return false;
            }

            if (char.IsLetter(current) || current == '_')
            {
                var start = index++;
                while (index < text.Length &&
                       (char.IsLetterOrDigit(text[index]) || text[index] is '_' or '$'))
                {
                    index++;
                }

                var token = text[start..index];
                hasToken = true;
                if (IsSessionStateToken(token) ||
                    atStatementStart && token.Equals("END", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                atStatementStart = false;
                if (token.Length == 1 &&
                    (token[0] is 'E' or 'e') &&
                    index < text.Length &&
                    text[index] == '\'' &&
                    !TrySkipQuoted(text, ref index, '\'', allowBackslashEscape: true))
                {
                    return false;
                }

                continue;
            }

            index++;
            continue;

        ContinueScanning:
            continue;
        }

        return hasToken;
    }

    private static bool IsSessionStateToken(ReadOnlySpan<char> token) =>
        Contains(SessionStateTokens, token) ||
        Contains(SessionStateRoutines, token);

    private static bool Contains(string[] candidates, ReadOnlySpan<char> token)
    {
        foreach (var candidate in candidates)
        {
            if (token.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TrySkipBlockComment(ReadOnlySpan<char> sql, ref int index)
    {
        index += 2;
        var depth = 1;
        while (index < sql.Length && depth != 0)
        {
            if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*')
            {
                depth++;
                index += 2;
            }
            else if (index + 1 < sql.Length && sql[index] == '*' && sql[index + 1] == '/')
            {
                depth--;
                index += 2;
            }
            else
            {
                index++;
            }
        }

        return depth == 0;
    }

    private static bool TrySkipQuoted(
        ReadOnlySpan<char> sql,
        ref int index,
        char quote,
        bool allowBackslashEscape)
    {
        index++;
        while (index < sql.Length)
        {
            if (allowBackslashEscape && sql[index] == '\\' && index + 1 < sql.Length)
            {
                index += 2;
                continue;
            }

            if (sql[index] != quote)
            {
                index++;
                continue;
            }

            if (index + 1 < sql.Length && sql[index + 1] == quote)
            {
                index += 2;
                continue;
            }

            index++;
            return true;
        }

        return false;
    }

    private static bool TrySkipDollarQuote(
        ReadOnlySpan<char> sql,
        ref int index,
        out bool closed)
    {
        var end = index + 1;
        while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_'))
        {
            end++;
        }

        if (end < sql.Length && sql[end] == '$')
        {
            var delimiter = sql[index..(end + 1)];
            var contentStart = end + 1;
            var closingOffset = sql[contentStart..].IndexOf(delimiter);
            closed = closingOffset >= 0;
            index = closed
                ? contentStart + closingOffset + delimiter.Length
                : sql.Length;
            return true;
        }

        closed = false;
        return false;
    }
}
