using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
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
    private readonly Channel<BlueTuskPoolSlot> _available = Channel.CreateUnbounded<BlueTuskPoolSlot>(
        new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = false,
            SingleWriter = false,
        });
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

    internal override BlueTuskPooledSession Rent()
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
                    TryAcquireAvailableOrReserveCreation();
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
                    lock (_stateSync)
                    {
                        Interlocked.Decrement(ref _idle);
                    }
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

    internal override ValueTask<BlueTuskPooledSession> RentAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_minimumSize > 0 && Volatile.Read(ref _total) < _minimumSize)
        {
            return WarmUpAndRentAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var started = StartCheckoutMeasurement();
        var (hasSlot, slot, creationReserved, cleanLease, idleRemoved) =
            TryAcquireAvailableOrReserveCreation();
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
            cancellationToken);
    }

    private async ValueTask<BlueTuskPooledSession> WarmUpAndRentAsync(
        CancellationToken cancellationToken)
    {
        await WarmUpAsync(cancellationToken).ConfigureAwait(false);
        return await RentAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<BlueTuskPooledSession> RentAsyncSlow(
        long started,
        bool hasSlot,
        BlueTuskPoolSlot slot,
        bool creationReserved,
        bool cleanLease,
        bool idleRemoved,
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
                        TryAcquireAvailableOrReserveCreation();
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
                    lock (_stateSync)
                    {
                        Interlocked.Decrement(ref _idle);
                    }
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
                    TryAcquireAvailableOrReserveCreation();
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
                    lock (_stateSync)
                    {
                        Interlocked.Decrement(ref _idle);
                    }
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
                    lock (_stateSync)
                    {
                        Interlocked.Decrement(ref _idle);
                    }
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
        lock (_stateSync)
        {
            Monitor.PulseAll(_stateSync);
        }
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
        var discard = Volatile.Read(ref _disposed) != 0 ||
            session.Generation != Volatile.Read(ref _generation) ||
            !session.Session.IsOpen ||
            IsExpired(session, includeIdleLifetime: false);
        if (!discard)
        {
            session.LastReturned = _timeProvider.GetUtcNow();
            Interlocked.Increment(ref _idle);
            if (Interlocked.CompareExchange(ref _fastSession, session, null) is null)
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
                        session.Generation != _generation ||
                        !_available.Writer.TryWrite(new BlueTuskPoolSlot(session)))
                    {
                        Interlocked.Decrement(ref _idle);
                        discard = true;
                    }
                    else
                    {
                        Monitor.Pulse(_stateSync);
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
    }

    private (
        bool HasSlot,
        BlueTuskPoolSlot Slot,
        bool CreationReserved,
        bool CleanLease,
        bool IdleRemoved)
        TryAcquireAvailableOrReserveCreation()
    {
        var fastSession = Interlocked.Exchange(ref _fastSession, null);
        if (fastSession is not null)
        {
            Interlocked.Decrement(ref _idle);
            if (Volatile.Read(ref _disposed) == 0 &&
                fastSession.Generation == Volatile.Read(ref _generation) &&
                fastSession.Session.IsOpen &&
                !IsExpired(fastSession, includeIdleLifetime: true) &&
                !fastSession.RequiresReset &&
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
            if (_available.Reader.TryRead(out var slot))
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
                    !pooledSession.RequiresReset &&
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

        lock (_stateSync)
        {
            _available.Writer.TryWrite(default);
            Monitor.Pulse(_stateSync);
        }
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

    private async ValueTask<BlueTuskPoolSlot> ReadAvailableAsync(CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        Interlocked.Increment(ref _waiting);
        if (Volatile.Read(ref _fastSession) is not null)
        {
            _available.Writer.TryWrite(default);
        }

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

    private BlueTuskPoolSlot ReadAvailable()
    {
        Interlocked.Increment(ref _waiting);
        if (Volatile.Read(ref _fastSession) is not null)
        {
            _available.Writer.TryWrite(default);
        }

        BlueTuskDiagnostics.PoolWaiters.Add(1);
        try
        {
            lock (_stateSync)
            {
                while (true)
                {
                    ThrowIfDisposed();
                    if (_available.Reader.TryRead(out var slot))
                    {
                        return slot;
                    }

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
                    if (!_available.Writer.TryWrite(new BlueTuskPoolSlot(pooledSession)))
                    {
                        throw new InvalidOperationException("Could not publish a warmed physical session.");
                    }

                    Monitor.Pulse(_stateSync);
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
        lock (_stateSync)
        {
            if (Volatile.Read(ref _disposed) != 0 || session.Generation != _generation)
            {
                return false;
            }

            Interlocked.Increment(ref _busy);
            Interlocked.Increment(ref _reused);
            return true;
        }
    }

    private List<BlueTuskPooledSession> DrainIdleSessions(bool complete)
    {
        var sessions = new List<BlueTuskPooledSession>();
        lock (_stateSync)
        {
            _generation++;
            var fastSession = Interlocked.Exchange(ref _fastSession, null);
            if (fastSession is not null)
            {
                Interlocked.Decrement(ref _idle);
                sessions.Add(fastSession);
            }

            while (_available.Reader.TryRead(out var slot))
            {
                if (slot.Session is not null)
                {
                    Interlocked.Decrement(ref _idle);
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
        lock (_stateSync)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _available.Writer.TryWrite(default);
                Monitor.PulseAll(_stateSync);
            }
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
    int generation,
    BlueTuskConnectionPool owner)
{
    internal IBlueTuskPhysicalSession Session { get; } = session;

    internal DateTimeOffset CreatedAt { get; } = createdAt;

    internal DateTimeOffset LastReturned { get; set; } = createdAt;

    internal int Generation { get; } = generation;

    internal BlueTuskConnectionPool Owner { get; } = owner;

    internal bool RequiresReset => Volatile.Read(ref _requiresReset) != 0;

    private int _requiresReset;

    internal void MarkDirty() => Volatile.Write(ref _requiresReset, 1);

    internal void ResetCompleted() => Volatile.Write(ref _requiresReset, 0);
}
