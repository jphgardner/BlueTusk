using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Threading.Tasks.Sources;
using BlueTusk.Client;

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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        Interlocked.Increment(ref _queued);
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
            Interlocked.Decrement(ref _queued);
            request.Dispose();
            throw;
        }
        catch (ChannelClosedException exception)
        {
            Interlocked.Decrement(ref _queued);
            request.Dispose();
            throw new ObjectDisposedException(nameof(BlueTuskDataSource), exception);
        }

        RecordAccepted();
        return await request.AsValueTask().ConfigureAwait(false);
    }

    private void RecordAccepted()
    {
        Interlocked.Increment(ref _accepted);
    }

    private async Task WorkerAsync(int workerIndex)
    {
        BlueTuskConnection? connection = null;
        var processedOnLease = 0;
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_shutdown.Token).ConfigureAwait(false))
            {
                var result = await ExecuteLeaseAsync(
                    connection,
                    workerIndex,
                    _options.MaxCommandsPerLease - processedOnLease).ConfigureAwait(false);
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
        int commandLimit)
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

                    Interlocked.Decrement(ref _queued);
                    if (!request.TryBeginExecution())
                    {
                        Interlocked.Increment(ref _canceled);
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
                        processed++;
                        continue;
                    }
                    catch (Exception exception)
                    {
                        request.TrySetException(exception);
                        Interlocked.Increment(ref _faulted);
                        processed++;
                        continue;
                    }
                }

                if (request.Command.CanUseMultiplexedPipeline)
                {
                    var batch = new List<IMultiplexedCommandRequest>(
                        _options.MaxPipelineCommands)
                    {
                        request,
                    };
                    while (batch.Count < _options.MaxPipelineCommands &&
                           processed + batch.Count < commandLimit &&
                           _queue.Reader.TryRead(out var candidate))
                    {
                        Interlocked.Decrement(ref _queued);
                        if (!candidate.TryBeginExecution())
                        {
                            Interlocked.Increment(ref _canceled);
                            processed++;
                            continue;
                        }

                        if (!candidate.Command.CanUseMultiplexedPipeline)
                        {
                            pending = candidate;
                            break;
                        }

                        batch.Add(candidate);
                    }

                    await ExecutePipelineAsync(connection, batch).ConfigureAwait(false);
                    processed += batch.Count;
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
            }

        }

        return new LeaseExecutionResult(connection, processed);
    }

    private async Task ExecuteSingleAsync(
        BlueTuskConnection connection,
        IMultiplexedCommandRequest request)
    {
        Interlocked.Increment(ref _executing);
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
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
            request.TrySetCanceled(request.CancellationToken);
            Interlocked.Increment(ref _canceled);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            request.TrySetException(
                new ObjectDisposedException(nameof(BlueTuskDataSource)));
            Interlocked.Increment(ref _canceled);
        }
        catch (Exception exception)
        {
            request.TrySetException(exception);
            Interlocked.Increment(ref _faulted);
        }
        finally
        {
            Interlocked.Decrement(ref _executing);
        }
    }

    private async Task ExecutePipelineAsync(
        BlueTuskConnection connection,
        IReadOnlyList<IMultiplexedCommandRequest> requests)
    {
        var activeBuffer = ArrayPool<IMultiplexedCommandRequest>.Shared.Rent(requests.Count);
        var commandBuffer = ArrayPool<BlueTuskMultiplexedPipelineCommand>.Shared.Rent(requests.Count);
        PipelineCancellationScope?[]? scopeBuffer = null;
        CancellationToken[]? tokenBuffer = null;
        var activeCount = 0;
        try
        {
            foreach (var request in requests)
            {
                try
                {
                    request.StartTelemetry(connection);
                    commandBuffer[activeCount] = request.Command.CreateMultiplexedPipelineCommand(
                        connection,
                        request.Scalar);
                    if (request.Command.CommandTimeout > 0 ||
                        request.CancellationToken.CanBeCanceled)
                    {
                        if (scopeBuffer is null)
                        {
                            scopeBuffer = ArrayPool<PipelineCancellationScope?>.Shared.Rent(
                                requests.Count);
                            tokenBuffer = ArrayPool<CancellationToken>.Shared.Rent(requests.Count);
                            Array.Clear(scopeBuffer, 0, activeCount);
                            Array.Clear(tokenBuffer, 0, activeCount);
                        }

                        var scope = new PipelineCancellationScope(
                            request.Command.CommandTimeout,
                            request.CancellationToken,
                            _shutdown.Token);
                        scopeBuffer[activeCount] = scope;
                        tokenBuffer![activeCount] = scope.Token;
                    }
                    else if (tokenBuffer is not null)
                    {
                        scopeBuffer![activeCount] = null;
                        tokenBuffer[activeCount] = CancellationToken.None;
                    }

                    activeBuffer[activeCount] = request;
                    activeCount++;
                    Interlocked.Increment(ref _executing);
                }
                catch (Exception exception)
                {
                    request.TrySetException(exception);
                    Interlocked.Increment(ref _faulted);
                }
            }

            if (activeCount == 0)
            {
                return;
            }

            var activeRequests = new ArraySegment<IMultiplexedCommandRequest>(
                activeBuffer,
                0,
                activeCount);
            var commands = new ArraySegment<BlueTuskMultiplexedPipelineCommand>(
                commandBuffer,
                0,
                activeCount);
            IReadOnlyList<CancellationToken>? groupCancellationTokens =
                tokenBuffer is null
                    ? (IReadOnlyList<CancellationToken>?)null
                    : new ArraySegment<CancellationToken>(tokenBuffer, 0, activeCount);
            try
            {
                Interlocked.Increment(ref _pipelineFlushes);
                Interlocked.Add(ref _pipelinedCommands, activeCount);
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
                    var scope = scopeBuffer?[index];
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
                        return;
                    }

                    if (outcome.Error is not null)
                    {
                        request.TrySetException(
                            request.Command.TranslateMultiplexedPipelineError(outcome.Error));
                        Interlocked.Increment(ref _faulted);
                    }
                    else
                    {
                        request.TrySetResult(
                            new MultiplexedCommandResult(
                                outcome.Result,
                                outcome.ScalarResult));
                        Interlocked.Increment(ref _completed);
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
                    }
                }
            }
            finally
            {
                for (var index = 0; index < activeCount; index++)
                {
                    activeRequests[index].Command.CompleteMultiplexedPipelineExecution();
                    scopeBuffer?[index]?.Dispose();
                    Interlocked.Decrement(ref _executing);
                }
            }
        }
        finally
        {
            Array.Clear(activeBuffer, 0, activeCount);
            ArrayPool<IMultiplexedCommandRequest>.Shared.Return(activeBuffer);
            ArrayPool<BlueTuskMultiplexedPipelineCommand>.Shared.Return(
                commandBuffer,
                clearArray: true);
            if (scopeBuffer is not null)
            {
                Array.Clear(scopeBuffer, 0, activeCount);
                ArrayPool<PipelineCancellationScope?>.Shared.Return(scopeBuffer);
                Array.Clear(tokenBuffer!, 0, activeCount);
                ArrayPool<CancellationToken>.Shared.Return(tokenBuffer!);
            }
        }
    }

    private void CancelQueued()
    {
        while (_queue.Reader.TryRead(out var request))
        {
            Interlocked.Decrement(ref _queued);
            request.Dispose();
            request.TrySetException(
                new ObjectDisposedException(nameof(BlueTuskDataSource)));
            Interlocked.Increment(ref _canceled);
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
    }

    private sealed class MultiplexedCommandRequest<T> :
        IMultiplexedCommandRequest,
        IDisposable,
        IValueTaskSource<T>
    {
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private ManualResetValueTaskSourceCore<T> _completion;
        private int _completionState;
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
                Dispose();
                return false;
            }

            Dispose();
            return true;
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
    private static readonly HashSet<string> SessionStateTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "BEGIN",
        "COMMIT",
        "COPY",
        "DECLARE",
        "DEALLOCATE",
        "DISCARD",
        "EXECUTE",
        "FETCH",
        "LISTEN",
        "LOAD",
        "LOCK",
        "MOVE",
        "PREPARE",
        "RESET",
        "ROLLBACK",
        "SAVEPOINT",
        "SET",
        "START",
        "UNLISTEN",
    };

    private static readonly HashSet<string> SessionStateRoutines = new(StringComparer.OrdinalIgnoreCase)
    {
        "PG_ADVISORY_LOCK",
        "PG_ADVISORY_LOCK_SHARED",
        "PG_ADVISORY_UNLOCK",
        "PG_ADVISORY_UNLOCK_ALL",
        "PG_ADVISORY_UNLOCK_SHARED",
        "PG_TRY_ADVISORY_LOCK",
        "PG_TRY_ADVISORY_LOCK_SHARED",
        "SET_CONFIG",
    };

    internal static bool IsSessionNeutral(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var tokens = Tokenize(sql);
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (SessionStateTokens.Contains(token) || SessionStateRoutines.Contains(token))
            {
                return false;
            }

            if (token.Equals("CREATE", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < tokens.Count &&
                (tokens[index + 1].Equals("TEMP", StringComparison.OrdinalIgnoreCase) ||
                 tokens[index + 1].Equals("TEMPORARY", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return tokens.Count != 0;
    }

    private static List<string> Tokenize(string sql)
    {
        var tokens = new List<string>();
        var index = 0;
        while (index < sql.Length)
        {
            var current = sql[index];
            if (char.IsWhiteSpace(current) || current is ';' or ',' or '(' or ')' or '.')
            {
                index++;
                continue;
            }

            if (current == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                index += 2;
                while (index < sql.Length && sql[index] is not ('\r' or '\n'))
                {
                    index++;
                }

                continue;
            }

            if (current == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                index = SkipBlockComment(sql, index + 2);
                continue;
            }

            if (current == '\'')
            {
                index = SkipQuoted(sql, index + 1, '\'', doubledEscape: true);
                continue;
            }

            if (current == '$' && TryReadDollarQuote(sql, index, out var delimiter))
            {
                var end = sql.IndexOf(delimiter, index + delimiter.Length, StringComparison.Ordinal);
                index = end < 0 ? sql.Length : end + delimiter.Length;
                continue;
            }

            if (current == '"')
            {
                var start = ++index;
                var identifier = new System.Text.StringBuilder();
                while (index < sql.Length)
                {
                    if (sql[index] == '"')
                    {
                        if (index + 1 < sql.Length && sql[index + 1] == '"')
                        {
                            identifier.Append(sql, start, index - start).Append('"');
                            index += 2;
                            start = index;
                            continue;
                        }

                        identifier.Append(sql, start, index - start);
                        index++;
                        break;
                    }

                    index++;
                }

                if (identifier.Length != 0)
                {
                    tokens.Add(identifier.ToString());
                }

                continue;
            }

            if (char.IsLetter(current) || current == '_')
            {
                var start = index++;
                while (index < sql.Length &&
                       (char.IsLetterOrDigit(sql[index]) || sql[index] is '_' or '$'))
                {
                    index++;
                }

                tokens.Add(sql[start..index]);
                continue;
            }

            index++;
        }

        return tokens;
    }

    private static int SkipBlockComment(string sql, int index)
    {
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

        return index;
    }

    private static int SkipQuoted(string sql, int index, char quote, bool doubledEscape)
    {
        while (index < sql.Length)
        {
            if (sql[index] != quote)
            {
                index++;
                continue;
            }

            if (doubledEscape && index + 1 < sql.Length && sql[index + 1] == quote)
            {
                index += 2;
                continue;
            }

            return index + 1;
        }

        return index;
    }

    private static bool TryReadDollarQuote(string sql, int index, out string delimiter)
    {
        var end = index + 1;
        while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_'))
        {
            end++;
        }

        if (end < sql.Length && sql[end] == '$')
        {
            delimiter = sql[index..(end + 1)];
            return true;
        }

        delimiter = string.Empty;
        return false;
    }
}
