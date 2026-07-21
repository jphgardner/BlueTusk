using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using BlueTusk.Diagnostics;
using BlueTusk.Protocol;

namespace BlueTusk.Data;

internal sealed class BlueTuskConnectionPool : IDisposable, IAsyncDisposable
{
    private readonly Channel<BlueTuskPoolSlot> _available = Channel.CreateUnbounded<BlueTuskPoolSlot>(
        new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = false,
            SingleWriter = false,
        });
    private readonly Func<CancellationToken, ValueTask<IBlueTuskPhysicalSession>> _sessionFactory;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleLifetime;
    private readonly TimeSpan _connectionLifetime;
    private readonly SemaphoreSlim _warmUpLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _stateSync = new();
    private readonly int _minimumSize;
    private readonly int _maximumSize;
    private int _generation;
    private int _disposed;
    private int _creating;
    private int _total;
    private int _idle;
    private int _busy;
    private int _waiting;
    private long _opened;
    private long _reused;
    private long _discarded;

    internal BlueTuskConnectionPool(
        BlueTuskConnectionStringBuilder settings,
        Func<CancellationToken, ValueTask<IBlueTuskPhysicalSession>>? sessionFactory = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        _minimumSize = settings.MinimumPoolSize;
        _maximumSize = settings.MaximumPoolSize;
        _idleLifetime = settings.ConnectionIdleLifetime;
        _connectionLifetime = settings.ConnectionLifetime;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sessionFactory = sessionFactory ?? (token => BlueTuskPhysicalSession.OpenAsync(settings, token));
    }

    internal BlueTuskPoolStatistics Statistics => new(
        PoolingEnabled: true,
        MinimumSize: _minimumSize,
        MaximumSize: _maximumSize,
        Total: Volatile.Read(ref _total),
        Idle: Volatile.Read(ref _idle),
        Busy: Volatile.Read(ref _busy),
        Waiting: Volatile.Read(ref _waiting),
        Opened: Interlocked.Read(ref _opened),
        Reused: Interlocked.Read(ref _reused),
        Discarded: Interlocked.Read(ref _discarded));

    internal async ValueTask<BlueTuskPoolLease> RentAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_minimumSize > 0 && Volatile.Read(ref _total) < _minimumSize)
        {
            await WarmUpAsync(cancellationToken).ConfigureAwait(false);
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (hasSlot, slot, creationReserved) = TryAcquireAvailableOrReserveCreation();
                if (!hasSlot && !creationReserved)
                {
                    slot = await ReadAvailableAsync(cancellationToken).ConfigureAwait(false);
                    hasSlot = true;
                }

                if (creationReserved)
                {
                    var created = await CreateReservedSessionAsync(
                        lease: true,
                        cancellationToken).ConfigureAwait(false);
                    return new BlueTuskPoolLease(this, created);
                }

                if (!hasSlot || slot.Session is null)
                {
                    continue;
                }

                var pooledSession = slot.Session;
                lock (_stateSync)
                {
                    _idle--;
                }

                try
                {
                    if (IsCurrent(pooledSession) &&
                        !IsExpired(pooledSession, includeIdleLifetime: true) &&
                        await ResetAndValidateAsync(pooledSession, cancellationToken).ConfigureAwait(false) &&
                        TryLeaseCurrent(pooledSession))
                    {
                        BlueTuskDiagnostics.PoolLeases.Add(1);
                        BlueTuskDiagnostics.PoolReuses.Add(1);
                        return new BlueTuskPoolLease(this, pooledSession);
                    }
                }
                catch (OperationCanceledException)
                {
                    await DiscardAsync(pooledSession).ConfigureAwait(false);
                    ThrowDisposedInsteadOfCancellation(cancellationToken);
                    throw;
                }

                await DiscardAsync(pooledSession).ConfigureAwait(false);
            }
        }
        finally
        {
            BlueTuskDiagnostics.PoolCheckoutDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    internal async ValueTask WarmUpAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_minimumSize == 0 || Volatile.Read(ref _total) >= _minimumSize)
        {
            return;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        try
        {
            await _warmUpLock.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
            try
            {
                while (TryReserveWarmUpCreation())
                {
                    _ = await CreateReservedSessionAsync(
                        lease: false,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _warmUpLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            ThrowDisposedInsteadOfCancellation(cancellationToken);
            throw;
        }
    }

    internal void Clear()
    {
        ThrowIfDisposed();
        foreach (var session in DrainIdleSessions(complete: false))
        {
            Discard(session);
        }
    }

    internal async ValueTask ClearAsync()
    {
        ThrowIfDisposed();
        foreach (var session in DrainIdleSessions(complete: false))
        {
            await DiscardAsync(session).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var sessions = DrainIdleSessions(complete: true);
        _shutdown.Cancel();
        foreach (var session in sessions)
        {
            Discard(session);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var sessions = DrainIdleSessions(complete: true);
        await _shutdown.CancelAsync().ConfigureAwait(false);
        foreach (var session in sessions)
        {
            await DiscardAsync(session).ConfigureAwait(false);
        }
    }

    internal void Return(BlueTuskPooledSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var discard = false;
        lock (_stateSync)
        {
            _busy--;
            if (Volatile.Read(ref _disposed) != 0 ||
                session.Generation != _generation ||
                !session.Session.IsOpen ||
                IsExpired(session, includeIdleLifetime: false))
            {
                discard = true;
            }
            else
            {
                session.LastReturned = _timeProvider.GetUtcNow();
                _idle++;
                if (!_available.Writer.TryWrite(new BlueTuskPoolSlot(session)))
                {
                    _idle--;
                    discard = true;
                }
            }
        }

        BlueTuskDiagnostics.PoolLeases.Add(-1);
        if (discard)
        {
            Discard(session);
        }
    }

    private (bool HasSlot, BlueTuskPoolSlot Slot, bool CreationReserved)
        TryAcquireAvailableOrReserveCreation()
    {
        lock (_stateSync)
        {
            ThrowIfDisposed();
            if (_available.Reader.TryRead(out var slot))
            {
                return (true, slot, false);
            }

            if (_total + _creating < _maximumSize)
            {
                _creating++;
                return (false, default, true);
            }

            return (false, default, false);
        }
    }

    private bool TryReserveWarmUpCreation()
    {
        lock (_stateSync)
        {
            ThrowIfDisposed();
            if (_total + _creating >= _minimumSize)
            {
                return false;
            }

            _creating++;
            return true;
        }
    }

    private async ValueTask<BlueTuskPoolSlot> ReadAvailableAsync(CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        Interlocked.Increment(ref _waiting);
        BlueTuskDiagnostics.PoolWaiters.Add(1);
        try
        {
            return await _available.Reader.ReadAsync(linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ThrowDisposedInsteadOfCancellation(cancellationToken);
            throw;
        }
        catch (ChannelClosedException)
        {
            throw new ObjectDisposedException(nameof(BlueTuskDataSource));
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
            BlueTuskDiagnostics.PoolWaiters.Add(-1);
        }
    }

    private async ValueTask<BlueTuskPooledSession> CreateReservedSessionAsync(
        bool lease,
        CancellationToken cancellationToken)
    {
        IBlueTuskPhysicalSession session;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        try
        {
            session = await _sessionFactory(linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ReleaseCreationReservation();
            ThrowDisposedInsteadOfCancellation(cancellationToken);
            throw;
        }
        catch
        {
            ReleaseCreationReservation();
            throw;
        }

        BlueTuskPooledSession pooledSession;
        var reject = false;
        lock (_stateSync)
        {
            _creating--;
            if (Volatile.Read(ref _disposed) != 0)
            {
                reject = true;
                pooledSession = null!;
            }
            else
            {
                var now = _timeProvider.GetUtcNow();
                pooledSession = new BlueTuskPooledSession(session, now, _generation);
                _total++;
                _opened++;
                if (lease)
                {
                    _busy++;
                }
                else
                {
                    _idle++;
                    if (!_available.Writer.TryWrite(new BlueTuskPoolSlot(pooledSession)))
                    {
                        throw new InvalidOperationException("Could not publish a warmed physical session.");
                    }
                }
            }
        }

        if (reject)
        {
            await DisposeUnacceptedSessionAsync(session).ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(BlueTuskDataSource));
        }

        BlueTuskDiagnostics.PoolConnections.Add(1);
        if (lease)
        {
            BlueTuskDiagnostics.PoolLeases.Add(1);
        }

        return pooledSession;
    }

    private static async ValueTask<bool> ResetAndValidateAsync(
        BlueTuskPooledSession pooledSession,
        CancellationToken cancellationToken)
    {
        var session = pooledSession.Session;
        if (!session.IsOpen)
        {
            return false;
        }

        try
        {
            if (session.TransactionStatus != BlueTuskTransactionStatus.Idle)
            {
                _ = await session.ExecuteSimpleQueryAsync("ROLLBACK", cancellationToken).ConfigureAwait(false);
            }

            if (!session.IsOpen || session.TransactionStatus != BlueTuskTransactionStatus.Idle)
            {
                return false;
            }

            _ = await session.ExecuteSimpleQueryAsync("DISCARD ALL", cancellationToken).ConfigureAwait(false);
            if (!session.IsOpen || session.TransactionStatus != BlueTuskTransactionStatus.Idle)
            {
                return false;
            }

            BlueTuskDiagnostics.PoolResets.Add(1);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    private bool TryLeaseCurrent(BlueTuskPooledSession session)
    {
        lock (_stateSync)
        {
            if (Volatile.Read(ref _disposed) != 0 || session.Generation != _generation)
            {
                return false;
            }

            _busy++;
            _reused++;
            return true;
        }
    }

    private List<BlueTuskPooledSession> DrainIdleSessions(bool complete)
    {
        var sessions = new List<BlueTuskPooledSession>();
        lock (_stateSync)
        {
            _generation++;
            while (_available.Reader.TryRead(out var slot))
            {
                if (slot.Session is not null)
                {
                    _idle--;
                    sessions.Add(slot.Session);
                }
            }

            if (complete)
            {
                _available.Writer.TryComplete();
            }
        }

        return sessions;
    }

    private bool IsCurrent(BlueTuskPooledSession session)
    {
        lock (_stateSync)
        {
            return Volatile.Read(ref _disposed) == 0 && session.Generation == _generation;
        }
    }

    private bool IsExpired(BlueTuskPooledSession session, bool includeIdleLifetime)
    {
        var now = _timeProvider.GetUtcNow();
        return (_connectionLifetime > TimeSpan.Zero && now - session.CreatedAt >= _connectionLifetime) ||
            (includeIdleLifetime &&
             _idleLifetime > TimeSpan.Zero &&
             now - session.LastReturned >= _idleLifetime);
    }

    private void ReleaseCreationReservation()
    {
        lock (_stateSync)
        {
            _creating--;
        }

        SignalCapacityAvailable();
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A discarded physical session must not prevent the pool from releasing its capacity.")]
    private void Discard(BlueTuskPooledSession session)
    {
        try
        {
            session.Session.Dispose();
        }
        catch
        {
            // The physical session is discarded regardless.
        }
        finally
        {
            RecordDiscard();
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A discarded physical session must not prevent the pool from releasing its capacity.")]
    private async ValueTask DiscardAsync(BlueTuskPooledSession session)
    {
        try
        {
            await session.Session.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // The physical session is discarded regardless.
        }
        finally
        {
            RecordDiscard();
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A session rejected during concurrent disposal is already outside the pool.")]
    private static async ValueTask DisposeUnacceptedSessionAsync(IBlueTuskPhysicalSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // The data source has already been disposed.
        }
    }

    private void RecordDiscard()
    {
        lock (_stateSync)
        {
            _total--;
            _discarded++;
        }

        BlueTuskDiagnostics.PoolConnections.Add(-1);
        BlueTuskDiagnostics.PoolDiscards.Add(1);
        SignalCapacityAvailable();
    }

    private void SignalCapacityAvailable()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _available.Writer.TryWrite(default);
        }
    }

    private void ThrowDisposedInsteadOfCancellation(CancellationToken callerToken)
        => ObjectDisposedException.ThrowIf(
            _shutdown.IsCancellationRequested && !callerToken.IsCancellationRequested,
            this);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

internal readonly record struct BlueTuskPoolSlot(BlueTuskPooledSession? Session);

internal sealed class BlueTuskPooledSession(
    IBlueTuskPhysicalSession session,
    DateTimeOffset createdAt,
    int generation)
{
    internal IBlueTuskPhysicalSession Session { get; } = session;

    internal DateTimeOffset CreatedAt { get; } = createdAt;

    internal DateTimeOffset LastReturned { get; set; } = createdAt;

    internal int Generation { get; } = generation;
}

internal sealed class BlueTuskPoolLease : IDisposable
{
    private BlueTuskConnectionPool? _pool;
    private readonly BlueTuskPooledSession _session;

    internal BlueTuskPoolLease(BlueTuskConnectionPool pool, BlueTuskPooledSession session)
    {
        _pool = pool;
        _session = session;
    }

    internal IBlueTuskPhysicalSession Session => _session.Session;

    public void Dispose() => Interlocked.Exchange(ref _pool, null)?.Return(_session);
}
