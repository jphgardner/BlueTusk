using BlueTusk.Security;

namespace BlueTusk.Data;

internal sealed class BlueTuskMultiHostConnectionPool : BlueTuskConnectionPoolBase
{
    private readonly PoolEntry[] _entries;
    private readonly BlueTuskTargetSessionAttributes _target;
    private readonly BlueTuskLoadBalanceHosts _loadBalanceHosts;
    private int _disposed;

    internal BlueTuskMultiHostConnectionPool(BlueTuskConnectionStringBuilder settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        _target = settings.TargetSessionAttributes;
        _loadBalanceHosts = settings.LoadBalanceHosts;
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
                return new PoolEntry(endpoint, new BlueTuskConnectionPool(hostSettings));
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

    internal override BlueTuskPooledSession Rent()
    {
        ThrowIfDisposed();
        var entries = GetOrderedEntries();
        var failures = new List<Exception>();
        BlueTuskPooledSession? fallback = null;
        var sawSaturatedPool = false;
        foreach (var entry in entries)
        {
            try
            {
                var lease = entry.Pool.TryRent();
                if (lease is null)
                {
                    sawSaturatedPool = true;
                    continue;
                }

                var selection = SelectLease(lease);
                if (selection == LeaseSelection.Accept)
                {
                    ReturnFallback(fallback);
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
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failures.Add(new BlueTuskHostPoolException(entry.Endpoint, exception));
            }
        }

        if (fallback is not null)
        {
            return fallback;
        }

        if (sawSaturatedPool)
        {
            return RentFromSaturatedPools(entries, failures);
        }

        throw CreatePoolException(failures);
    }

    internal override async ValueTask<BlueTuskPooledSession> RentAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var entries = GetOrderedEntries();
        var failures = new List<Exception>();
        BlueTuskPooledSession? fallback = null;
        var sawSaturatedPool = false;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                if (selection == LeaseSelection.Accept)
                {
                    ReturnFallback(fallback);
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
                failures.Add(new BlueTuskHostPoolException(entry.Endpoint, exception));
            }
        }

        if (fallback is not null)
        {
            return fallback;
        }

        if (sawSaturatedPool)
        {
            return await RentFromSaturatedPoolsAsync(
                entries,
                failures,
                cancellationToken).ConfigureAwait(false);
        }

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
        }
    }

    internal override async ValueTask ClearAsync()
    {
        ThrowIfDisposed();
        foreach (var entry in _entries)
        {
            await entry.Pool.ClearAsync().ConfigureAwait(false);
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
        CancellationToken cancellationToken)
    {
        BlueTuskPooledSession? fallback = null;
        foreach (var entry in entries)
        {
            try
            {
                var lease = await entry.Pool.RentAsync(cancellationToken).ConfigureAwait(false);
                var selection = await SelectLeaseAsync(
                    lease,
                    cancellationToken).ConfigureAwait(false);
                if (selection == LeaseSelection.Accept)
                {
                    ReturnFallback(fallback);
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
            catch (OperationCanceledException)
            {
                ReturnFallback(fallback);
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failures.Add(new BlueTuskHostPoolException(entry.Endpoint, exception));
            }
        }

        return fallback ?? throw CreatePoolException(failures);
    }

    private BlueTuskPooledSession RentFromSaturatedPools(
        IReadOnlyList<PoolEntry> entries,
        List<Exception> failures)
    {
        BlueTuskPooledSession? fallback = null;
        foreach (var entry in entries)
        {
            try
            {
                var lease = entry.Pool.Rent();
                var selection = SelectLease(lease);
                if (selection == LeaseSelection.Accept)
                {
                    ReturnFallback(fallback);
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
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failures.Add(new BlueTuskHostPoolException(entry.Endpoint, exception));
            }
        }

        return fallback ?? throw CreatePoolException(failures);
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

    private PoolEntry[] GetOrderedEntries()
    {
        var entries = _entries.ToArray();
        if (_loadBalanceHosts == BlueTuskLoadBalanceHosts.Random)
        {
            Random.Shared.Shuffle(entries);
        }

        return entries;
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

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed record PoolEntry(
        BlueTuskHostEndpoint Endpoint,
        BlueTuskConnectionPool Pool);

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
