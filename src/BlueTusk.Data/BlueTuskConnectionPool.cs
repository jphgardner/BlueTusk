using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
using BlueTusk.Diagnostics;
using BlueTusk.Protocol;

namespace BlueTusk.Data;

internal abstract class BlueTuskConnectionPoolBase : IDisposable, IAsyncDisposable
{
    internal abstract BlueTuskPoolStatistics Statistics { get; }

    internal abstract IReadOnlyDictionary<BlueTuskHostEndpoint, BlueTuskPoolStatistics> HostStatistics { get; }

    internal abstract BlueTuskPooledSession Rent();

    internal abstract ValueTask<BlueTuskPooledSession> RentAsync(CancellationToken cancellationToken);

    internal abstract void Return(BlueTuskPooledSession session);

    internal abstract void WarmUp();

    internal abstract ValueTask WarmUpAsync(CancellationToken cancellationToken);

    internal abstract void Clear();

    internal abstract ValueTask ClearAsync();

    public abstract void Dispose();

    public abstract ValueTask DisposeAsync();
}

internal sealed class BlueTuskConnectionPool : BlueTuskConnectionPoolBase
{
    // Queue operations are protected by _stateSync. A waiter is removed under
    // that lock, but completed afterward so consumer work never extends it.
    private readonly Queue<BlueTuskPoolSlot> _available = new();
    private readonly LinkedList<PoolWaiter> _asyncWaiters = new();
    private readonly Stack<PoolWaiter> _cachedWaiters = new();
    private const int MaximumCachedWaiters = 256;
    private readonly Func<CancellationToken, ValueTask<IBlueTuskPhysicalSession>> _sessionFactory;
    private readonly Func<IBlueTuskPhysicalSession> _synchronousSessionFactory;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleLifetime;
    private readonly TimeSpan _connectionLifetime;
    private readonly SemaphoreSlim _warmUpLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _stateSync = new();
    private readonly int _minimumSize;
    private readonly int _maximumSize;
    private readonly BlueTuskHostEndpoint _endpoint;
    private BlueTuskPooledSession? _fastSession;
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
        TimeProvider? timeProvider = null,
        Func<IBlueTuskPhysicalSession>? synchronousSessionFactory = null,
        BlueTuskClientConfiguration? clientConfiguration = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        _minimumSize = settings.MinimumPoolSize;
        _maximumSize = settings.MaximumPoolSize;
        _endpoint = settings.HostEndpoints.Single();
        _idleLifetime = settings.ConnectionIdleLifetime;
        _connectionLifetime = settings.ConnectionLifetime;
        _timeProvider = timeProvider ?? TimeProvider.System;
        var configuration = clientConfiguration ?? BlueTuskClientConfiguration.Empty;
        _sessionFactory = sessionFactory ?? (token => BlueTuskPhysicalSession.OpenAsync(settings, configuration, token));
        _synchronousSessionFactory = synchronousSessionFactory ?? (() => BlueTuskPhysicalSession.Open(settings, configuration));
    }

    internal override BlueTuskPoolStatistics Statistics => new(
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

    internal override IReadOnlyDictionary<BlueTuskHostEndpoint, BlueTuskPoolStatistics> HostStatistics =>
        new Dictionary<BlueTuskHostEndpoint, BlueTuskPoolStatistics>
        {
            [_endpoint] = Statistics,
        };

    internal override BlueTuskPooledSession Rent() => Rent(allowPendingReset: false);

    internal BlueTuskPooledSession RentForCommand() => Rent(allowPendingReset: true);

    private BlueTuskPooledSession Rent(bool allowPendingReset)
    {
        ThrowIfDisposed();
        if (_minimumSize > 0 && Volatile.Read(ref _total) < _minimumSize)
        {
            WarmUp();
        }

        var started = StartCheckoutMeasurement();
        try
        {
            while (true)
            {
                var (hasSlot, slot, creationReserved, cleanLease, idleRemoved) =
                    TryAcquireAvailableOrReserveCreation(allowPendingReset);
                if (!hasSlot && !creationReserved)
                {
                    slot = ReadAvailable();
                    hasSlot = true;
                }

                if (creationReserved)
                {
                    return CreateReservedSession(lease: true);
                }

                if (!hasSlot || slot.Session is not { } pooledSession)
                {
                    continue;
                }

                if (cleanLease)
                {
                    RecordCleanReuse();
                    return pooledSession;
                }

                if (!idleRemoved)
                {
                    Interlocked.Decrement(ref _idle);
                }

                if (allowPendingReset &&
                    IsCurrent(pooledSession) &&
                    !IsExpired(pooledSession, includeIdleLifetime: true) &&
                    pooledSession.Session.IsOpen &&
                    pooledSession.Session.TransactionStatus == BlueTuskTransactionStatus.Idle &&
                    TryLeaseCurrent(pooledSession))
                {
                    BlueTuskDiagnostics.PoolLeases.Add(1);
                    BlueTuskDiagnostics.PoolReuses.Add(1);
                    return pooledSession;
                }

                if (IsCurrent(pooledSession) &&
                    !IsExpired(pooledSession, includeIdleLifetime: true) &&
                    ResetAndValidate(pooledSession) &&
                    TryLeaseCurrent(pooledSession))
                {
                    BlueTuskDiagnostics.PoolLeases.Add(1);
                    BlueTuskDiagnostics.PoolReuses.Add(1);
                    return pooledSession;
                }

                Discard(pooledSession);
            }
        }
        finally
        {
            RecordCheckoutDuration(started);
        }
    }

    internal override ValueTask<BlueTuskPooledSession> RentAsync(CancellationToken cancellationToken) =>
        RentAsync(allowPendingReset: false, cancellationToken);

    internal ValueTask<BlueTuskPooledSession> RentForCommandAsync(
        CancellationToken cancellationToken) =>
        RentAsync(allowPendingReset: true, cancellationToken);

    private ValueTask<BlueTuskPooledSession> RentAsync(
        bool allowPendingReset,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_minimumSize > 0 && Volatile.Read(ref _total) < _minimumSize)
        {
            return WarmUpAndRentAsync(allowPendingReset, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var started = StartCheckoutMeasurement();
        var (hasSlot, slot, creationReserved, cleanLease, idleRemoved) =
            TryAcquireAvailableOrReserveCreation(allowPendingReset);
        if (cleanLease && slot.Session is { } cleanSession)
        {
            RecordCleanReuse();
            RecordCheckoutDuration(started);
            return new ValueTask<BlueTuskPooledSession>(cleanSession);
        }

        return RentAsyncSlow(
            started,
            hasSlot,
            slot,
            creationReserved,
            cleanLease,
            idleRemoved,
            allowPendingReset,
            cancellationToken);
    }

    private async ValueTask<BlueTuskPooledSession> WarmUpAndRentAsync(
        bool allowPendingReset,
        CancellationToken cancellationToken)
    {
        await WarmUpAsync(cancellationToken).ConfigureAwait(false);
        return await RentAsync(allowPendingReset, cancellationToken).ConfigureAwait(false);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<BlueTuskPooledSession> RentAsyncSlow(
        long started,
        bool hasSlot,
        BlueTuskPoolSlot slot,
        bool creationReserved,
        bool cleanLease,
        bool idleRemoved,
        bool allowPendingReset,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                if (!hasSlot && !creationReserved)
                {
                    slot = await ReadAvailableAsync(cancellationToken).ConfigureAwait(false);
                    hasSlot = true;
                    cleanLease = false;
                }

                if (creationReserved)
                {
                    var created = await CreateReservedSessionAsync(
                        lease: true,
                        cancellationToken).ConfigureAwait(false);
                    return created;
                }

                if (!hasSlot || slot.Session is null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    (hasSlot, slot, creationReserved, cleanLease, idleRemoved) =
                        TryAcquireAvailableOrReserveCreation(allowPendingReset);
                    continue;
                }

                var pooledSession = slot.Session;
                if (cleanLease)
                {
                    RecordCleanReuse();
                    return pooledSession;
                }

                if (!idleRemoved)
                {
                    Interlocked.Decrement(ref _idle);
                }

                if (allowPendingReset &&
                    IsCurrent(pooledSession) &&
                    !IsExpired(pooledSession, includeIdleLifetime: true) &&
                    pooledSession.Session.IsOpen &&
                    pooledSession.Session.TransactionStatus == BlueTuskTransactionStatus.Idle &&
                    TryLeaseCurrent(pooledSession))
                {
                    BlueTuskDiagnostics.PoolLeases.Add(1);
                    BlueTuskDiagnostics.PoolReuses.Add(1);
                    return pooledSession;
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
                        return pooledSession;
                    }
                }
                catch (OperationCanceledException)
                {
                    await DiscardAsync(pooledSession).ConfigureAwait(false);
                    ThrowDisposedInsteadOfCancellation(cancellationToken);
                    throw;
                }

                await DiscardAsync(pooledSession).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                (hasSlot, slot, creationReserved, cleanLease, idleRemoved) =
                    TryAcquireAvailableOrReserveCreation(allowPendingReset);
            }
        }
        finally
        {
            RecordCheckoutDuration(started);
        }
    }

    internal async ValueTask<BlueTuskPooledSession?> TryRentAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var started = StartCheckoutMeasurement();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (hasSlot, slot, creationReserved, cleanLease, idleRemoved) =
                    TryAcquireAvailableOrReserveCreation();
                if (!hasSlot && !creationReserved)
                {
                    return null;
                }

                if (creationReserved)
                {
                    return await CreateReservedSessionAsync(
                        lease: true,
                        cancellationToken).ConfigureAwait(false);
                }

                if (slot.Session is not { } pooledSession)
                {
                    continue;
                }

                if (cleanLease)
                {
                    RecordCleanReuse();
                    return pooledSession;
                }

                if (!idleRemoved)
                {
                    Interlocked.Decrement(ref _idle);
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
                        return pooledSession;
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
            RecordCheckoutDuration(started);
        }
    }

    internal BlueTuskPooledSession? TryRent()
    {
        ThrowIfDisposed();
        var started = StartCheckoutMeasurement();
        try
        {
            while (true)
            {
                var (hasSlot, slot, creationReserved, cleanLease, idleRemoved) =
                    TryAcquireAvailableOrReserveCreation();
                if (!hasSlot && !creationReserved)
                {
                    return null;
                }

                if (creationReserved)
                {
                    return CreateReservedSession(lease: true);
                }

                if (slot.Session is not { } pooledSession)
                {
                    continue;
                }

                if (cleanLease)
                {
                    RecordCleanReuse();
                    return pooledSession;
                }

                if (!idleRemoved)
                {
                    Interlocked.Decrement(ref _idle);
                }

                if (IsCurrent(pooledSession) &&
                    !IsExpired(pooledSession, includeIdleLifetime: true) &&
                    ResetAndValidate(pooledSession) &&
                    TryLeaseCurrent(pooledSession))
                {
                    BlueTuskDiagnostics.PoolLeases.Add(1);
                    BlueTuskDiagnostics.PoolReuses.Add(1);
                    return pooledSession;
                }

                Discard(pooledSession);
            }
        }
        finally
        {
            RecordCheckoutDuration(started);
        }
    }

    internal override void WarmUp()
    {
        ThrowIfDisposed();
        if (_minimumSize == 0 || Volatile.Read(ref _total) >= _minimumSize)
        {
            return;
        }

        _warmUpLock.Wait();
        try
        {
            while (TryReserveWarmUpCreation())
            {
                _ = CreateReservedSession(lease: false);
            }
        }
        finally
        {
            _warmUpLock.Release();
        }
    }

    internal override async ValueTask WarmUpAsync(CancellationToken cancellationToken)
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

    internal override void Clear()
    {
        ThrowIfDisposed();
        foreach (var session in DrainIdleSessions(complete: false))
        {
            Discard(session);
        }
    }

    internal override async ValueTask ClearAsync()
    {
        ThrowIfDisposed();
        foreach (var session in DrainIdleSessions(complete: false))
        {
            await DiscardAsync(session).ConfigureAwait(false);
        }
    }

    public override void Dispose()
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

    public override async ValueTask DisposeAsync()
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

    internal override void Return(BlueTuskPooledSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Interlocked.Decrement(ref _busy);
        var now = _timeProvider.GetUtcNow();
        var discard = Volatile.Read(ref _disposed) != 0 ||
            session.Generation != Volatile.Read(ref _generation) ||
            !session.Session.IsOpen ||
            IsExpired(session, includeIdleLifetime: false, now);
        PoolWaiter? recipient = null;
        if (!discard)
        {
            session.LastReturned = now;
            Interlocked.Increment(ref _idle);
            if (Volatile.Read(ref _waiting) > 0)
            {
                lock (_stateSync)
                {
                    if (Volatile.Read(ref _disposed) != 0 ||
                        session.Generation != _generation)
                    {
                        Interlocked.Decrement(ref _idle);
                        discard = true;
                    }
                    else
                    {
                        recipient = PublishAvailableUnderLock(new BlueTuskPoolSlot(session));
                    }
                }
            }
            else if (Interlocked.CompareExchange(ref _fastSession, session, null) is null)
            {
                if (Volatile.Read(ref _disposed) != 0 ||
                    session.Generation != Volatile.Read(ref _generation))
                {
                    if (ReferenceEquals(
                            Interlocked.CompareExchange(ref _fastSession, null, session),
                            session))
                    {
                        Interlocked.Decrement(ref _idle);
                        discard = true;
                    }
                }
                else
                {
                    SignalFastSessionAvailable();
                }
            }
            else
            {
                lock (_stateSync)
                {
                    if (Volatile.Read(ref _disposed) != 0 ||
                        session.Generation != _generation)
                    {
                        Interlocked.Decrement(ref _idle);
                        discard = true;
                    }
                    else
                    {
                        recipient = PublishAvailableUnderLock(new BlueTuskPoolSlot(session));
                    }
                }
            }
        }

        if (BlueTuskDiagnostics.PoolLeases.Enabled)
        {
            BlueTuskDiagnostics.PoolLeases.Add(-1);
        }
        if (discard)
        {
            Discard(session);
        }
        else
        {
            recipient?.Complete(new BlueTuskPoolSlot(session));
        }
    }

    private (
        bool HasSlot,
        BlueTuskPoolSlot Slot,
        bool CreationReserved,
        bool CleanLease,
        bool IdleRemoved)
        TryAcquireAvailableOrReserveCreation(bool allowPendingReset = false)
    {
        var fastSession = Interlocked.Exchange(ref _fastSession, null);
        if (fastSession is not null)
        {
            Interlocked.Decrement(ref _idle);
            if (Volatile.Read(ref _disposed) == 0 &&
                fastSession.Generation == Volatile.Read(ref _generation) &&
                fastSession.Session.IsOpen &&
                !IsExpired(fastSession, includeIdleLifetime: true) &&
                (allowPendingReset || !fastSession.RequiresReset) &&
                fastSession.Session.TransactionStatus == BlueTuskTransactionStatus.Idle)
            {
                Interlocked.Increment(ref _busy);
                Interlocked.Increment(ref _reused);
                return (true, new BlueTuskPoolSlot(fastSession), false, true, true);
            }

            return (true, new BlueTuskPoolSlot(fastSession), false, false, true);
        }

        lock (_stateSync)
        {
            ThrowIfDisposed();
            if (_available.TryDequeue(out var slot))
            {
                var pooledSession = slot.Session;
                if (pooledSession is null)
                {
                    return (true, slot, false, false, false);
                }

                Interlocked.Decrement(ref _idle);
                if (pooledSession.Generation == _generation &&
                    pooledSession.Session.IsOpen &&
                    !IsExpired(pooledSession, includeIdleLifetime: true) &&
                    (allowPendingReset || !pooledSession.RequiresReset) &&
                    pooledSession.Session.TransactionStatus == BlueTuskTransactionStatus.Idle)
                {
                    Interlocked.Increment(ref _busy);
                    Interlocked.Increment(ref _reused);
                    return (true, slot, false, true, true);
                }

                return (true, slot, false, false, true);
            }

            if (_total + _creating < _maximumSize)
            {
                _creating++;
                return (false, default, true, false, false);
            }

            return (false, default, false, false, false);
        }
    }

    private void SignalFastSessionAvailable()
    {
        if (Volatile.Read(ref _waiting) == 0)
        {
            return;
        }

        SignalCapacityAvailable();
    }

    private PoolWaiter? PublishAvailableUnderLock(BlueTuskPoolSlot slot)
    {
        if (_asyncWaiters.First is { } first)
        {
            _asyncWaiters.Remove(first);
            return first.Value;
        }

        _available.Enqueue(slot);
        Monitor.Pulse(_stateSync);
        return null;
    }

    private static void RecordCleanReuse()
    {
        if (BlueTuskDiagnostics.PoolLeases.Enabled)
        {
            BlueTuskDiagnostics.PoolLeases.Add(1);
        }

        if (BlueTuskDiagnostics.PoolReuses.Enabled)
        {
            BlueTuskDiagnostics.PoolReuses.Add(1);
        }
    }

    private static long StartCheckoutMeasurement() =>
        BlueTuskDiagnostics.PoolCheckoutDuration.Enabled
            ? Stopwatch.GetTimestamp()
            : 0;

    private static void RecordCheckoutDuration(long started)
    {
        if (started != 0)
        {
            BlueTuskDiagnostics.PoolCheckoutDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalSeconds);
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

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<BlueTuskPoolSlot> ReadAvailableAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _waiting);
        BlueTuskDiagnostics.PoolWaiters.Add(1);
        try
        {
            PoolWaiter waiter;
            lock (_stateSync)
            {
                ThrowIfDisposed();
                cancellationToken.ThrowIfCancellationRequested();
                if (_available.TryDequeue(out var slot)) { return slot; }
                if (Volatile.Read(ref _fastSession) is not null) { return default; }

                waiter = _cachedWaiters.TryPop(out var cached) ? cached : new PoolWaiter(this);
                _asyncWaiters.AddLast(waiter.Node);
                // Register before the waiter can be handed off and recycled.
                // Already-cancelled registration can complete inline here, but
                // its ValueTask has not yet been exposed to any consumer.
                waiter.RegisterCancellation(cancellationToken);
            }

            return await waiter.WaitAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ThrowDisposedInsteadOfCancellation(cancellationToken);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
            BlueTuskDiagnostics.PoolWaiters.Add(-1);
        }
    }

    private BlueTuskPoolSlot ReadAvailable()
    {
        Interlocked.Increment(ref _waiting);
        BlueTuskDiagnostics.PoolWaiters.Add(1);
        try
        {
            lock (_stateSync)
            {
                while (true)
                {
                    ThrowIfDisposed();
                    if (_available.TryDequeue(out var slot))
                    {
                        return slot;
                    }
                    if (Volatile.Read(ref _fastSession) is not null) { return default; }

                    Monitor.Wait(_stateSync);
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
            BlueTuskDiagnostics.PoolWaiters.Add(-1);
        }
    }

    private BlueTuskPooledSession CreateReservedSession(bool lease)
    {
        IBlueTuskPhysicalSession session;
        try
        {
            session = _synchronousSessionFactory();
        }
        catch
        {
            ReleaseCreationReservation();
            throw;
        }

        BlueTuskPooledSession pooledSession;
        var reject = false;
        PoolWaiter? recipient = null;
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
                pooledSession = new BlueTuskPooledSession(session, now, _generation, this);
                _total++;
                _opened++;
                if (lease)
                {
                    Interlocked.Increment(ref _busy);
                }
                else
                {
                    Interlocked.Increment(ref _idle);
                    recipient = PublishAvailableUnderLock(new BlueTuskPoolSlot(pooledSession));
                }
            }
        }

        if (reject)
        {
            DisposeUnacceptedSession(session);
            throw new ObjectDisposedException(nameof(BlueTuskDataSource));
        }

        BlueTuskDiagnostics.PoolConnections.Add(1);
        if (lease)
        {
            BlueTuskDiagnostics.PoolLeases.Add(1);
        }
        recipient?.Complete(new BlueTuskPoolSlot(pooledSession));
        return pooledSession;
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
        PoolWaiter? recipient = null;
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
                pooledSession = new BlueTuskPooledSession(session, now, _generation, this);
                _total++;
                _opened++;
                if (lease)
                {
                    Interlocked.Increment(ref _busy);
                }
                else
                {
                    Interlocked.Increment(ref _idle);
                    recipient = PublishAvailableUnderLock(new BlueTuskPoolSlot(pooledSession));
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
        recipient?.Complete(new BlueTuskPoolSlot(pooledSession));
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
            if (!pooledSession.RequiresReset &&
                session.TransactionStatus == BlueTuskTransactionStatus.Idle)
            {
                return true;
            }

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
            pooledSession.ResetCompleted();
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

    private static bool ResetAndValidate(BlueTuskPooledSession pooledSession)
    {
        var session = pooledSession.Session;
        if (!session.IsOpen)
        {
            return false;
        }

        try
        {
            if (!pooledSession.RequiresReset &&
                session.TransactionStatus == BlueTuskTransactionStatus.Idle)
            {
                return true;
            }

            if (session.TransactionStatus != BlueTuskTransactionStatus.Idle)
            {
                _ = session.ExecuteSimpleQuery("ROLLBACK");
            }

            if (!session.IsOpen || session.TransactionStatus != BlueTuskTransactionStatus.Idle)
            {
                return false;
            }

            _ = session.ExecuteSimpleQuery("DISCARD ALL");
            if (!session.IsOpen || session.TransactionStatus != BlueTuskTransactionStatus.Idle)
            {
                return false;
            }

            BlueTuskDiagnostics.PoolResets.Add(1);
            pooledSession.ResetCompleted();
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    private bool TryLeaseCurrent(BlueTuskPooledSession session)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            session.Generation != Volatile.Read(ref _generation))
        {
            return false;
        }

        Interlocked.Increment(ref _busy);
        Interlocked.Increment(ref _reused);
        return true;
    }

    private List<BlueTuskPooledSession> DrainIdleSessions(bool complete)
    {
        var sessions = new List<BlueTuskPooledSession>();
        List<PoolWaiter>? waiters = null;
        lock (_stateSync)
        {
            _generation++;
            var fastSession = Interlocked.Exchange(ref _fastSession, null);
            if (fastSession is not null)
            {
                Interlocked.Decrement(ref _idle);
                sessions.Add(fastSession);
            }

            while (_available.TryDequeue(out var slot))
            {
                if (slot.Session is not null)
                {
                    Interlocked.Decrement(ref _idle);
                    sessions.Add(slot.Session);
                }
            }

            if (complete)
            {
                waiters = new List<PoolWaiter>(_asyncWaiters.Count);
                while (_asyncWaiters.First is { } first)
                {
                    _asyncWaiters.Remove(first);
                    waiters.Add(first.Value);
                }
                _cachedWaiters.Clear();
                Monitor.PulseAll(_stateSync);
            }
        }
        if (waiters is not null)
        {
            foreach (var waiter in waiters) { waiter.CompleteDisposed(); }
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
        return IsExpired(session, includeIdleLifetime, _timeProvider.GetUtcNow());
    }

    private bool IsExpired(
        BlueTuskPooledSession session,
        bool includeIdleLifetime,
        DateTimeOffset now)
    {
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

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A session rejected during concurrent disposal is already outside the pool.")]
    private static void DisposeUnacceptedSession(IBlueTuskPhysicalSession session)
    {
        try
        {
            session.Dispose();
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
        PoolWaiter? recipient = null;
        lock (_stateSync)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                recipient = PublishAvailableUnderLock(default);
                Monitor.PulseAll(_stateSync);
            }
        }
        recipient?.Complete(default);
    }

    private void ThrowDisposedInsteadOfCancellation(CancellationToken callerToken)
        => ObjectDisposedException.ThrowIf(
            _shutdown.IsCancellationRequested && !callerToken.IsCancellationRequested,
            this);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed class PoolWaiter : IValueTaskSource<BlueTuskPoolSlot>
    {
        private readonly BlueTuskConnectionPool _owner;
        private ManualResetValueTaskSourceCore<BlueTuskPoolSlot> _completion;
        private CancellationTokenRegistration _cancellationRegistration;
        private CancellationToken _callerToken;

        internal PoolWaiter(BlueTuskConnectionPool owner)
        {
            _owner = owner;
            Node = new LinkedListNode<PoolWaiter>(this);
        }

        internal LinkedListNode<PoolWaiter> Node { get; }

        internal void RegisterCancellation(CancellationToken callerToken)
        {
            _callerToken = callerToken;
            _cancellationRegistration = callerToken.UnsafeRegister(
                static state => ((PoolWaiter)state!).Cancel(), this);
        }

        internal ValueTask<BlueTuskPoolSlot> WaitAsync() => new(this, _completion.Version);

        internal void Complete(BlueTuskPoolSlot slot) => _completion.SetResult(slot);

        internal void CompleteDisposed() =>
            _completion.SetException(new ObjectDisposedException(nameof(BlueTuskDataSource)));

        private void Cancel()
        {
            lock (_owner._stateSync)
            {
                // Removing the node claims completion. Return/dispose and
                // cancellation therefore cannot both complete the same waiter.
                if (Node.List is null) { return; }
                _owner._asyncWaiters.Remove(Node);
            }
            _completion.SetException(new OperationCanceledException(_callerToken));
        }

        public BlueTuskPoolSlot GetResult(short token)
        {
            if (_completion.GetStatus(token) == ValueTaskSourceStatus.Pending)
            {
                throw new InvalidOperationException("The pool wait has not completed.");
            }
            try { return _completion.GetResult(token); }
            finally
            {
                // Wait for any cancellation callback before making the source
                // reusable. Reset clears result, exception and execution-context
                // references; only internal, single-consumption ValueTasks use it.
                _cancellationRegistration.Dispose();
                _cancellationRegistration = default;
                _callerToken = default;
                _completion.Reset();
                lock (_owner._stateSync)
                {
                    if (Volatile.Read(ref _owner._disposed) == 0 &&
                        _owner._cachedWaiters.Count < MaximumCachedWaiters)
                    {
                        _owner._cachedWaiters.Push(this);
                    }
                }
            }
        }

        public ValueTaskSourceStatus GetStatus(short token) => _completion.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token,
            ValueTaskSourceOnCompletedFlags flags) => _completion.OnCompleted(continuation, state, token, flags);
    }
}

internal readonly record struct BlueTuskPoolSlot(BlueTuskPooledSession? Session);

internal sealed class BlueTuskPooledSession(
    IBlueTuskPhysicalSession session,
    DateTimeOffset createdAt,
    int generation,
    BlueTuskConnectionPool owner)
{
    internal IBlueTuskPhysicalSession Session { get; } = session;

    internal DateTimeOffset CreatedAt { get; } = createdAt;

    internal DateTimeOffset LastReturned { get; set; } = createdAt;

    internal int Generation { get; } = generation;

    internal BlueTuskConnectionPool Owner { get; } = owner;

    internal bool RequiresReset => Volatile.Read(ref _requiresReset) != 0;

    internal Action ResetCompletedCallback =>
        _resetCompletedCallback ??= ResetCompletedAndRecord;

    private int _requiresReset;
    private Action? _resetCompletedCallback;

    internal void MarkDirty() => Volatile.Write(ref _requiresReset, 1);

    internal void ResetCompleted() => Volatile.Write(ref _requiresReset, 0);

    internal void ResetCompletedAndRecord()
    {
        ResetCompleted();
        BlueTuskDiagnostics.PoolResets.Add(1);
    }
}
