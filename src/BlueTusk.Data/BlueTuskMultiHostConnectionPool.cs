using BlueTusk.Diagnostics;
using BlueTusk.Security;
using BlueTusk.Client;
using BlueTusk.Transport;
using System.Net.Sockets;

namespace BlueTusk.Data;

internal sealed class BlueTuskMultiHostConnectionPool : BlueTuskConnectionPoolBase
{
    private readonly PoolEntry[] _entries;
    private readonly BlueTuskTargetSessionAttributes _target;
    private readonly BlueTuskLoadBalanceHosts _loadBalanceHosts;
    private readonly TimeProvider _timeProvider;
    private static readonly TimeSpan FailedHostRecheckInterval = TimeSpan.FromSeconds(10);
    private int _disposed;

    internal BlueTuskMultiHostConnectionPool(
        BlueTuskConnectionStringBuilder settings,
        BlueTuskClientConfiguration? clientConfiguration = null,
        TimeProvider? timeProvider = null,
        Func<BlueTuskConnectionStringBuilder, BlueTuskConnectionPool>? poolFactory = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        _target = settings.TargetSessionAttributes;
        _loadBalanceHosts = settings.LoadBalanceHosts;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _entries = settings.HostEndpoints
            .Select(endpoint =>
            {
                var hostSettings = new BlueTuskConnectionStringBuilder(settings.ConnectionString)
                {
                    Host = endpoint.Host,
                    Port = endpoint.Port,
                    TargetSessionAttributes = BlueTuskTargetSessionAttributes.Any,
                    LoadBalanceHosts = BlueTuskLoadBalanceHosts.Disable,
                };
                return new PoolEntry(
                    endpoint,
                    poolFactory?.Invoke(hostSettings) ?? new BlueTuskConnectionPool(
                        hostSettings,
                        clientConfiguration: clientConfiguration));
            })
            .ToArray();
    }

    internal override BlueTuskPoolStatistics Statistics
    {
        get
        {
            var statistics = _entries.Select(static entry => entry.Pool.Statistics).ToArray();
            return new BlueTuskPoolStatistics(
                PoolingEnabled: true,
                MinimumSize: statistics.Sum(static value => value.MinimumSize),
                MaximumSize: statistics.Sum(static value => value.MaximumSize),
                Total: statistics.Sum(static value => value.Total),
                Idle: statistics.Sum(static value => value.Idle),
                Busy: statistics.Sum(static value => value.Busy),
                Waiting: statistics.Sum(static value => value.Waiting),
                Opened: statistics.Sum(static value => value.Opened),
                Reused: statistics.Sum(static value => value.Reused),
                Discarded: statistics.Sum(static value => value.Discarded));
        }
    }

    internal override IReadOnlyDictionary<BlueTuskHostEndpoint, BlueTuskPoolStatistics> HostStatistics =>
        _entries.ToDictionary(static entry => entry.Endpoint, static entry => entry.Pool.Statistics);

    internal override BlueTuskPooledSession Rent() => RentCore(waitForProbe: true);

    private BlueTuskPooledSession RentCore(bool waitForProbe)
    {
        ThrowIfDisposed();
        var entries = GetOrderedEntries(out var firstEndpoint);
        var failures = new List<Exception>();
        BlueTuskPooledSession? fallback = null;
        var sawSaturatedPool = false;
        Task? pendingProbe = null;
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if ((sawSaturatedPool || fallback is not null) && entry.IsCooling(_timeProvider, _timeProvider.GetTimestamp())) { continue; }
            if (!entry.TryBeginProbe(out var probe, out var pending))
            { pendingProbe ??= pending; continue; }
            RecordRetry(index, entry.Endpoint);
            try
            {
                var lease = entry.Pool.TryRent();
                if (lease is null)
                {
                    sawSaturatedPool = true;
                    continue;
                }

                var selection = SelectLease(lease);
                entry.MarkHealthy();
                if (selection == LeaseSelection.Accept)
                {
                    ReturnFallback(fallback);
                    RecordFailover(firstEndpoint, lease.Session.Endpoint);
                    return lease;
                }

                if (selection == LeaseSelection.Fallback && fallback is null)
                {
                    fallback = lease;
                }
                else
                {
                    failures.Add(new BlueTuskHostPoolSelectionException(
                        entry.Endpoint,
                        _target,
                        lease.Session.IsPrimary,
                        lease.Session.IsReadOnly));
                    lease.Owner.Return(lease);
                }
            }
            catch (BlueTuskAuthenticationException)
            {
                ReturnFallback(fallback);
                throw;
            }
            catch (OperationCanceledException)
            {
                ReturnFallback(fallback);
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                MarkUnavailable(entry, exception);
                failures.Add(new BlueTuskHostPoolException(entry.Endpoint, exception));
            }
            finally { entry.EndProbe(probe); }
        }

        if (fallback is not null)
        {
            RecordFailover(firstEndpoint, fallback.Session.Endpoint);
            return fallback;
        }

        if (sawSaturatedPool)
        {
            return RentFromSaturatedPools(entries, failures, firstEndpoint, waitForProbe);
        }

        if (pendingProbe is not null && waitForProbe)
        {
            pendingProbe.GetAwaiter().GetResult();
            return RentCore(waitForProbe: false);
        }

        if (pendingProbe is not null && failures.Count == 0)
        { failures.Add(new InvalidOperationException("A host availability probe is already in progress.")); }

        throw CreatePoolException(failures);
    }

    internal override ValueTask<BlueTuskPooledSession> RentAsync(
        CancellationToken cancellationToken) => RentAsyncCore(waitForProbe: true, cancellationToken);

    private async ValueTask<BlueTuskPooledSession> RentAsyncCore(
        bool waitForProbe, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var entries = GetOrderedEntries(out var firstEndpoint);
        var failures = new List<Exception>();
        BlueTuskPooledSession? fallback = null;
        var sawSaturatedPool = false;
        Task? pendingProbe = null;
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            cancellationToken.ThrowIfCancellationRequested();
            if ((sawSaturatedPool || fallback is not null) && entry.IsCooling(_timeProvider, _timeProvider.GetTimestamp())) { continue; }
            if (!entry.TryBeginProbe(out var probe, out var pending))
            { pendingProbe ??= pending; continue; }
            RecordRetry(index, entry.Endpoint);
            try
            {
                var lease = await entry.Pool.TryRentAsync(cancellationToken).ConfigureAwait(false);
                if (lease is null)
                {
                    sawSaturatedPool = true;
                    continue;
                }

                var selection = await SelectLeaseAsync(
                    lease,
                    cancellationToken).ConfigureAwait(false);
                entry.MarkHealthy();
                if (selection == LeaseSelection.Accept)
                {
                    ReturnFallback(fallback);
                    RecordFailover(firstEndpoint, lease.Session.Endpoint);
                    return lease;
                }

                if (selection == LeaseSelection.Fallback && fallback is null)
                {
                    fallback = lease;
                }
                else
                {
                    failures.Add(new BlueTuskHostPoolSelectionException(
                        entry.Endpoint,
                        _target,
                        lease.Session.IsPrimary,
                        lease.Session.IsReadOnly));
                    lease.Owner.Return(lease);
                }
            }
            catch (BlueTuskAuthenticationException)
            {
                ReturnFallback(fallback);
                throw;
            }
            catch (OperationCanceledException)
            {
                ReturnFallback(fallback);
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                MarkUnavailable(entry, exception);
                failures.Add(new BlueTuskHostPoolException(entry.Endpoint, exception));
            }
            finally { entry.EndProbe(probe); }
        }

        if (fallback is not null)
        {
            RecordFailover(firstEndpoint, fallback.Session.Endpoint);
            return fallback;
        }

        if (sawSaturatedPool)
        {
            return await RentFromSaturatedPoolsAsync(
                entries,
                failures,
                firstEndpoint,
                waitForProbe,
                cancellationToken).ConfigureAwait(false);
        }

        if (pendingProbe is not null && waitForProbe)
        {
            await pendingProbe.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await RentAsyncCore(waitForProbe: false, cancellationToken).ConfigureAwait(false);
        }

        if (pendingProbe is not null && failures.Count == 0)
        { failures.Add(new InvalidOperationException("A host availability probe is already in progress.")); }

        throw CreatePoolException(failures);
    }

    internal override void Return(BlueTuskPooledSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Owner.Return(session);
    }

    internal override void WarmUp()
    {
        ThrowIfDisposed();
        foreach (var entry in _entries)
        {
            entry.Pool.WarmUp();
        }
    }

    internal override async ValueTask WarmUpAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        foreach (var entry in _entries)
        {
            await entry.Pool.WarmUpAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal override void Clear()
    {
        ThrowIfDisposed();
        foreach (var entry in _entries)
        {
            entry.Pool.Clear();
            entry.MarkHealthy();
        }
    }

    internal override async ValueTask ClearAsync()
    {
        ThrowIfDisposed();
        foreach (var entry in _entries)
        {
            await entry.Pool.ClearAsync().ConfigureAwait(false);
            entry.MarkHealthy();
        }
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var entry in _entries)
        {
            entry.Pool.Dispose();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var entry in _entries)
        {
            await entry.Pool.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<BlueTuskPooledSession> RentFromSaturatedPoolsAsync(
        IReadOnlyList<PoolEntry> entries,
        List<Exception> failures,
        BlueTuskHostEndpoint firstEndpoint,
        bool waitForProbe,
        CancellationToken cancellationToken)
    {
        BlueTuskPooledSession? fallback = null;
        Task? pendingProbe = null;
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            cancellationToken.ThrowIfCancellationRequested();
            if (fallback is not null && entry.IsCooling(_timeProvider, _timeProvider.GetTimestamp())) { continue; }
            if (!entry.TryBeginProbe(out var probe, out var pending))
            { pendingProbe ??= pending; continue; }
            RecordRetry(index, entry.Endpoint);
            try
            {
                var lease = await entry.Pool.RentAsync(cancellationToken).ConfigureAwait(false);
                var selection = await SelectLeaseAsync(
                    lease,
                    cancellationToken).ConfigureAwait(false);
                entry.MarkHealthy();
                if (selection == LeaseSelection.Accept)
                {
                    ReturnFallback(fallback);
                    RecordFailover(firstEndpoint, lease.Session.Endpoint);
                    return lease;
                }

                if (selection == LeaseSelection.Fallback && fallback is null)
                {
                    fallback = lease;
                }
                else
                {
                    failures.Add(new BlueTuskHostPoolSelectionException(
                        entry.Endpoint,
                        _target,
                        lease.Session.IsPrimary,
                        lease.Session.IsReadOnly));
                    lease.Owner.Return(lease);
                }
            }
            catch (BlueTuskAuthenticationException)
            {
                ReturnFallback(fallback);
                throw;
            }
            catch (OperationCanceledException)
            {
                ReturnFallback(fallback);
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                MarkUnavailable(entry, exception);
                failures.Add(new BlueTuskHostPoolException(entry.Endpoint, exception));
            }
            finally { entry.EndProbe(probe); }
        }

        if (fallback is not null)
        {
            RecordFailover(firstEndpoint, fallback.Session.Endpoint);
            return fallback;
        }

        if (pendingProbe is not null && waitForProbe)
        {
            await pendingProbe.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await RentAsyncCore(waitForProbe: false, cancellationToken).ConfigureAwait(false);
        }

        if (pendingProbe is not null && failures.Count == 0)
        { failures.Add(new InvalidOperationException("A host availability probe is already in progress.")); }

        throw CreatePoolException(failures);
    }

    private BlueTuskPooledSession RentFromSaturatedPools(
        IReadOnlyList<PoolEntry> entries,
        List<Exception> failures,
        BlueTuskHostEndpoint firstEndpoint,
        bool waitForProbe)
    {
        BlueTuskPooledSession? fallback = null;
        Task? pendingProbe = null;
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (fallback is not null && entry.IsCooling(_timeProvider, _timeProvider.GetTimestamp())) { continue; }
            if (!entry.TryBeginProbe(out var probe, out var pending))
            { pendingProbe ??= pending; continue; }
            RecordRetry(index, entry.Endpoint);
            try
            {
                var lease = entry.Pool.Rent();
                var selection = SelectLease(lease);
                entry.MarkHealthy();
                if (selection == LeaseSelection.Accept)
                {
                    ReturnFallback(fallback);
                    RecordFailover(firstEndpoint, lease.Session.Endpoint);
                    return lease;
                }

                if (selection == LeaseSelection.Fallback && fallback is null)
                {
                    fallback = lease;
                }
                else
                {
                    failures.Add(new BlueTuskHostPoolSelectionException(
                        entry.Endpoint,
                        _target,
                        lease.Session.IsPrimary,
                        lease.Session.IsReadOnly));
                    lease.Owner.Return(lease);
                }
            }
            catch (BlueTuskAuthenticationException)
            {
                ReturnFallback(fallback);
                throw;
            }
            catch (OperationCanceledException)
            {
                ReturnFallback(fallback);
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                MarkUnavailable(entry, exception);
                failures.Add(new BlueTuskHostPoolException(entry.Endpoint, exception));
            }
            finally { entry.EndProbe(probe); }
        }

        if (fallback is not null)
        {
            RecordFailover(firstEndpoint, fallback.Session.Endpoint);
            return fallback;
        }

        if (pendingProbe is not null && waitForProbe)
        {
            pendingProbe.GetAwaiter().GetResult();
            return RentCore(waitForProbe: false);
        }

        if (pendingProbe is not null && failures.Count == 0)
        { failures.Add(new InvalidOperationException("A host availability probe is already in progress.")); }

        throw CreatePoolException(failures);
    }

    private LeaseSelection SelectLease(BlueTuskPooledSession lease)
    {
        var requiredTarget = _target switch
        {
            BlueTuskTargetSessionAttributes.PreferPrimary =>
                BlueTuskTargetSessionAttributes.Primary,
            BlueTuskTargetSessionAttributes.PreferStandby =>
                BlueTuskTargetSessionAttributes.Standby,
            _ => _target,
        };
        if (requiredTarget == BlueTuskTargetSessionAttributes.Any)
        {
            return LeaseSelection.Accept;
        }

        try
        {
            lease.Session.RefreshHostState();
        }
        catch
        {
            try
            {
                lease.Session.Dispose();
            }
            finally
            {
                lease.Owner.Return(lease);
            }

            throw;
        }

        if (MatchesTarget(lease.Session, requiredTarget))
        {
            return LeaseSelection.Accept;
        }

        return _target is
            BlueTuskTargetSessionAttributes.PreferPrimary or
            BlueTuskTargetSessionAttributes.PreferStandby
                ? LeaseSelection.Fallback
                : LeaseSelection.Reject;
    }

    private async ValueTask<LeaseSelection> SelectLeaseAsync(
        BlueTuskPooledSession lease,
        CancellationToken cancellationToken)
    {
        var requiredTarget = _target switch
        {
            BlueTuskTargetSessionAttributes.PreferPrimary =>
                BlueTuskTargetSessionAttributes.Primary,
            BlueTuskTargetSessionAttributes.PreferStandby =>
                BlueTuskTargetSessionAttributes.Standby,
            _ => _target,
        };
        if (requiredTarget == BlueTuskTargetSessionAttributes.Any)
        {
            return LeaseSelection.Accept;
        }

        try
        {
            await lease.Session.RefreshHostStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                lease.Session.Dispose();
            }
            finally
            {
                lease.Owner.Return(lease);
            }

            throw;
        }

        if (MatchesTarget(lease.Session, requiredTarget))
        {
            return LeaseSelection.Accept;
        }

        return _target is
            BlueTuskTargetSessionAttributes.PreferPrimary or
            BlueTuskTargetSessionAttributes.PreferStandby
                ? LeaseSelection.Fallback
                : LeaseSelection.Reject;
    }

    private PoolEntry[] GetOrderedEntries(out BlueTuskHostEndpoint firstEndpoint)
    {
        var entries = _loadBalanceHosts == BlueTuskLoadBalanceHosts.Random ? _entries.ToArray() : _entries;
        if (_loadBalanceHosts == BlueTuskLoadBalanceHosts.Random)
        {
            Random.Shared.Shuffle(entries);
        }

        firstEndpoint = entries[0].Endpoint;
        var now = _timeProvider.GetTimestamp();
        var hasCooling = false;
        foreach (var entry in entries)
        { if (entry.IsCooling(_timeProvider, now)) { hasCooling = true; break; } }
        if (!hasCooling) { return entries; }
        var ordered = new PoolEntry[entries.Length];
        var available = 0;
        var deferred = entries.Length - 1;
        foreach (var entry in entries)
        {
            if (entry.IsCooling(_timeProvider, now)) { ordered[deferred--] = entry; }
            else { ordered[available++] = entry; }
        }
        Array.Reverse(ordered, available, ordered.Length - available);
        return ordered;
    }

    private void MarkUnavailable(PoolEntry entry, Exception exception)
    {
        // Role mismatch, authentication rejection and caller cancellation are
        // never evidence that an endpoint is unavailable.
        if (IsAvailabilityFailure(exception))
        { entry.MarkUnavailable(_timeProvider.GetTimestamp()); }
    }

    private static bool IsAvailabilityFailure(Exception exception)
    {
        var availability = false;
        // Single-host startup wraps its failure in an aggregate. Follow only a
        // bounded unambiguous cause chain, including normalized connect timeouts.
        for (var depth = 0; depth < 16; depth++)
        {
            if (exception is BlueTuskAuthenticationException or OperationCanceledException or
                System.Security.Authentication.AuthenticationException) { return false; }
            if (exception is BlueTuskTransportException) { return true; }
            if (exception is BlueTuskServerException server)
            { return server.SqlState is "57P01" or "57P02" or "57P03" or "53300"; }
            if (exception is BlueTuskException { SqlState: not null } database)
            { return database.SqlState is "57P01" or "57P02" or "57P03" or "53300"; }
            availability |= exception is IOException or SocketException or TimeoutException;
            if (exception is AggregateException aggregate)
            {
                if (aggregate.InnerExceptions.Count != 1) { return false; }
                exception = aggregate.InnerExceptions[0];
            }
            else if (exception.InnerException is { } inner) { exception = inner; }
            else { return availability; }
        }
        return false;
    }

    private static bool MatchesTarget(
        IBlueTuskPhysicalSession session,
        BlueTuskTargetSessionAttributes target) => target switch
        {
            BlueTuskTargetSessionAttributes.Any => true,
            BlueTuskTargetSessionAttributes.Primary => session.IsPrimary == true,
            BlueTuskTargetSessionAttributes.Standby => session.IsPrimary == false,
            BlueTuskTargetSessionAttributes.ReadWrite => session.IsReadOnly == false,
            BlueTuskTargetSessionAttributes.ReadOnly => session.IsReadOnly == true,
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };

    private BlueTuskException CreatePoolException(IReadOnlyCollection<Exception> failures) =>
        new(
            $"Could not rent a PostgreSQL connection matching {_target} from " +
            $"{_entries.Length} configured host pool(s).",
            new AggregateException(failures));

    private static void ReturnFallback(BlueTuskPooledSession? fallback) =>
        fallback?.Owner.Return(fallback);

    private static void RecordRetry(int index, BlueTuskHostEndpoint endpoint)
    {
        if (index > 0)
        {
            BlueTuskDiagnostics.RecordConnectionRetry(
                endpoint.Host,
                endpoint.Port,
                "multi_host");
        }
    }

    private static void RecordFailover(
        BlueTuskHostEndpoint first,
        BlueTuskHostEndpoint selected)
    {
        if (first != selected)
        {
            BlueTuskDiagnostics.RecordConnectionFailover(selected.Host, selected.Port);
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed record PoolEntry(
        BlueTuskHostEndpoint Endpoint,
        BlueTuskConnectionPool Pool)
    {
        private long _unavailableSince = long.MinValue;
        private TaskCompletionSource<bool>? _probe;
        internal bool IsCooling(TimeProvider clock, long now)
        {
            var since = Volatile.Read(ref _unavailableSince);
            return since != long.MinValue && clock.GetElapsedTime(since, now) < FailedHostRecheckInterval;
        }
        internal void MarkUnavailable(long now) => Volatile.Write(ref _unavailableSince, now);
        internal void MarkHealthy() => Volatile.Write(ref _unavailableSince, long.MinValue);
        internal bool TryBeginProbe(out TaskCompletionSource<bool>? probe, out Task? pending)
        {
            probe = null;
            pending = null;
            if (Volatile.Read(ref _unavailableSince) == long.MinValue) { return true; }
            var candidate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var current = Interlocked.CompareExchange(ref _probe, candidate, null);
            if (current is null) { probe = candidate; return true; }
            pending = current.Task;
            return false;
        }
        internal void EndProbe(TaskCompletionSource<bool>? probe)
        {
            if (probe is null) { return; }
            _ = Interlocked.CompareExchange(ref _probe, null, probe);
            probe.TrySetResult(true);
        }
    }

    private sealed class BlueTuskHostPoolException(
        BlueTuskHostEndpoint endpoint,
        Exception innerException)
        : Exception($"Host pool {endpoint} could not provide a connection.", innerException);

    private sealed class BlueTuskHostPoolSelectionException(
        BlueTuskHostEndpoint endpoint,
        BlueTuskTargetSessionAttributes target,
        bool? isPrimary,
        bool? isReadOnly)
        : Exception(
            $"Host pool {endpoint} does not match {target} " +
            $"(primary={isPrimary}, read-only={isReadOnly}).");

    private enum LeaseSelection
    {
        Accept,
        Reject,
        Fallback,
    }
}
