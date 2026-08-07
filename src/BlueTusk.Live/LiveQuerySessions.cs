namespace BlueTusk.Live;

public readonly record struct LiveInvalidationCursor(long Value) : IComparable<LiveInvalidationCursor>
{
    public int CompareTo(LiveInvalidationCursor other) => Value.CompareTo(other.Value);

    public static bool operator <(LiveInvalidationCursor left, LiveInvalidationCursor right) =>
        left.Value < right.Value;

    public static bool operator <=(LiveInvalidationCursor left, LiveInvalidationCursor right) =>
        left.Value <= right.Value;

    public static bool operator >(LiveInvalidationCursor left, LiveInvalidationCursor right) =>
        left.Value > right.Value;

    public static bool operator >=(LiveInvalidationCursor left, LiveInvalidationCursor right) =>
        left.Value >= right.Value;
}

public interface ILiveInvalidationLog
{
    ValueTask<LiveInvalidationCursor> GetCurrentCursorAsync(
        string databaseIdentity,
        CancellationToken cancellationToken = default);

    ValueTask<bool> HasChangesAsync(
        string databaseIdentity,
        IReadOnlyCollection<LiveTableDependency> dependencies,
        LiveInvalidationCursor afterExclusive,
        LiveInvalidationCursor throughInclusive,
        CancellationToken cancellationToken = default);
}

public interface ILiveInvalidationSink
{
    ValueTask<LiveInvalidationCursor> AppendAsync(
        string databaseIdentity,
        BlueTusk.Streams.ChangeTransaction transaction,
        CancellationToken cancellationToken = default);
}

public sealed record LiveQuerySessionOptions
{
    public int MaximumInitialCatchUpPasses { get; init; } = 32;

    public LiveDiffOptions Diff { get; init; } = new();

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumInitialCatchUpPasses);
        ArgumentNullException.ThrowIfNull(Diff);
        Diff.Validate();
    }
}

public sealed record LiveQuerySessionStatus(
    LiveSubscriptionIdentity Identity,
    LiveInvalidationCursor Cursor,
    long LastSequence,
    long AuthoritativeQueryCount,
    long CoalescedInvalidationCount,
    int ResultCount,
    bool IsStarted);

public sealed class LiveQuerySession<T, TKey> : IAsyncDisposable
    where TKey : notnull
{
    private readonly LiveQueryPlan<T, TKey> _plan;
    private readonly LiveQueryExecutionContext _context;
    private readonly ILiveInvalidationLog _invalidationLog;
    private readonly LiveQuerySessionOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LiveResultSnapshot<T, TKey>? _snapshot;
    private LiveInvalidationCursor _cursor;
    private long _lastSequence;
    private long _authoritativeQueryCount;
    private long _coalescedInvalidationCount;
    private int _started;
    private int _disposed;

    public LiveQuerySession(
        LiveQueryPlan<T, TKey> plan,
        LiveQueryArguments arguments,
        LiveSecurityScope securityScope,
        ILiveInvalidationLog invalidationLog,
        int? resultLimit = null,
        LiveQuerySessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(securityScope);
        ArgumentNullException.ThrowIfNull(invalidationLog);
        options ??= new LiveQuerySessionOptions();
        options.Validate();
        _plan = plan;
        _context = new LiveQueryExecutionContext(arguments, securityScope);
        _invalidationLog = invalidationLog;
        _options = options;
        Identity = LiveSubscriptionIdentity.Create(plan, arguments, securityScope, resultLimit);
    }

    public LiveSubscriptionIdentity Identity { get; }

    public LiveQuerySessionStatus Status => new(
        Identity,
        _cursor,
        Interlocked.Read(ref _lastSequence),
        Interlocked.Read(ref _authoritativeQueryCount),
        Interlocked.Read(ref _coalescedInvalidationCount),
        Volatile.Read(ref _snapshot)?.Rows.Count ?? 0,
        Volatile.Read(ref _started) != 0);

    public async ValueTask<LiveDiffBatch<T, TKey>> StartAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _started) != 0)
            {
                throw new InvalidOperationException("A live query session can be started only once.");
            }

            var cursor = await _invalidationLog.GetCurrentCursorAsync(
                _plan.DatabaseIdentity,
                cancellationToken).ConfigureAwait(false);
            IReadOnlyList<T>? rows = null;
            for (var pass = 0; pass < _options.MaximumInitialCatchUpPasses; pass++)
            {
                rows = await ExecuteAuthoritativeQueryAsync(cancellationToken).ConfigureAwait(false);
                var through = await _invalidationLog.GetCurrentCursorAsync(
                    _plan.DatabaseIdentity,
                    cancellationToken).ConfigureAwait(false);
                if (through < cursor)
                {
                    throw new LiveInvalidationCursorException(
                        $"The invalidation cursor moved backward from {cursor.Value} to {through.Value}.");
                }

                if (through == cursor ||
                    !await _invalidationLog.HasChangesAsync(
                        _plan.DatabaseIdentity,
                        _plan.Dependencies,
                        cursor,
                        through,
                        cancellationToken).ConfigureAwait(false))
                {
                    _cursor = through;
                    var initial = LiveResultDiffer.Initial(
                        rows,
                        _plan.KeySelector,
                        _plan.KeyComparer,
                        sequence: 1);
                    Volatile.Write(ref _snapshot, initial.Snapshot);
                    Interlocked.Exchange(ref _lastSequence, 1);
                    Volatile.Write(ref _started, 1);
                    return initial;
                }

                Interlocked.Increment(ref _coalescedInvalidationCount);
                cursor = through;
            }

            throw new LiveInitialCatchUpException(
                $"Live query '{_plan.Name}' could not reach a quiet invalidation boundary after " +
                $"{_options.MaximumInitialCatchUpPasses} authoritative query passes.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<LiveDiffBatch<T, TKey>?> RefreshToCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var telemetryStarted = LiveDiagnostics.GetTimestamp();
        var telemetryOutcome = "success";
        var telemetryEvents = 0;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureStarted();
            var through = await _invalidationLog.GetCurrentCursorAsync(
                _plan.DatabaseIdentity,
                cancellationToken).ConfigureAwait(false);
            if (through < _cursor)
            {
                throw new LiveInvalidationCursorException(
                    $"The invalidation cursor moved backward from {_cursor.Value} to {through.Value}.");
            }

            if (through == _cursor)
            {
                return null;
            }

            var affected = await _invalidationLog.HasChangesAsync(
                _plan.DatabaseIdentity,
                _plan.Dependencies,
                _cursor,
                through,
                cancellationToken).ConfigureAwait(false);
            _cursor = through;
            if (!affected)
            {
                return null;
            }

            Interlocked.Increment(ref _coalescedInvalidationCount);
            var rows = await ExecuteAuthoritativeQueryAsync(cancellationToken).ConfigureAwait(false);
            var nextSequence = checked(Interlocked.Read(ref _lastSequence) + 1);
            var batch = LiveResultDiffer.Diff(
                Volatile.Read(ref _snapshot)!,
                rows,
                _plan.KeySelector,
                _plan.RowComparer,
                _plan.KeyComparer,
                _options.Diff,
                nextSequence);
            Volatile.Write(ref _snapshot, batch.Snapshot);
            if (batch.Events.Count != 0)
            {
                Interlocked.Exchange(ref _lastSequence, batch.Events[^1].Sequence);
            }

            telemetryEvents = batch.Events.Count;
            return batch;
        }
        catch (OperationCanceledException)
        {
            telemetryOutcome = "canceled";
            throw;
        }
        catch
        {
            telemetryOutcome = "error";
            throw;
        }
        finally
        {
            LiveDiagnostics.RecordRefresh(
                _plan.Name,
                telemetryOutcome,
                telemetryStarted,
                telemetryEvents);
            _gate.Release();
        }
    }

    public async ValueTask<LiveDiffBatch<T, TKey>> ResetAsync(
        LiveResetReason reason,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureStarted();
            var rows = await ExecuteAuthoritativeQueryAsync(cancellationToken).ConfigureAwait(false);
            var nextSequence = checked(Interlocked.Read(ref _lastSequence) + 1);
            var initial = LiveResultDiffer.Initial(
                rows,
                _plan.KeySelector,
                _plan.KeyComparer,
                nextSequence);
            var reset = new LiveResultEvent<T, TKey>(
                nextSequence,
                LiveEventKind.ResultReset,
                default,
                default,
                null,
                null,
                initial.Snapshot.Rows,
                initial.Snapshot.Keys,
                reason);
            var batch = new LiveDiffBatch<T, TKey>(initial.Snapshot, [reset]);
            Volatile.Write(ref _snapshot, batch.Snapshot);
            Interlocked.Exchange(ref _lastSequence, nextSequence);
            _cursor = await _invalidationLog.GetCurrentCursorAsync(
                _plan.DatabaseIdentity,
                cancellationToken).ConfigureAwait(false);
            return batch;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
        _gate.Dispose();
    }

    private async ValueTask<IReadOnlyList<T>> ExecuteAuthoritativeQueryAsync(
        CancellationToken cancellationToken)
    {
        var telemetryStarted = LiveDiagnostics.GetTimestamp();
        using var activity = LiveDiagnostics.StartAuthoritativeQuery(_plan.Name);
        try
        {
            var rows = await _plan.ExecuteAsync(_context, cancellationToken).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(rows);
            if (rows.Count > Identity.ResultLimit)
            {
                throw new LiveQueryResultLimitException(
                    $"Live query '{_plan.Name}' returned {rows.Count} rows, exceeding its bound of {Identity.ResultLimit}.");
            }

            Interlocked.Increment(ref _authoritativeQueryCount);
            LiveDiagnostics.RecordAuthoritativeQuery(
                _plan.Name,
                "success",
                telemetryStarted,
                rows.Count);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            activity?.SetTag("bluetusk.live.result.rows", rows.Count);
            return rows;
        }
        catch (OperationCanceledException)
        {
            LiveDiagnostics.RecordAuthoritativeQuery(
                _plan.Name,
                "canceled",
                telemetryStarted,
                rowCount: -1);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "canceled");
            throw;
        }
        catch
        {
            LiveDiagnostics.RecordAuthoritativeQuery(
                _plan.Name,
                "error",
                telemetryStarted,
                rowCount: -1);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error);
            throw;
        }
    }

    private void EnsureStarted()
    {
        if (Volatile.Read(ref _started) == 0)
        {
            throw new InvalidOperationException("The live query session has not been started.");
        }
    }
}

public class LiveQueryException : Exception
{
    public LiveQueryException(string message)
        : base(message)
    {
    }

    public LiveQueryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class LiveInvalidationCursorException : LiveQueryException
{
    public LiveInvalidationCursorException(string message)
        : base(message)
    {
    }
}

public sealed class LiveInitialCatchUpException : LiveQueryException
{
    public LiveInitialCatchUpException(string message)
        : base(message)
    {
    }
}

public sealed class LiveQueryResultLimitException : LiveQueryException
{
    public LiveQueryResultLimitException(string message)
        : base(message)
    {
    }
}
