namespace BlueTusk.ControlPlane;

/// <summary>An ordinal, immutable provider registry.</summary>
public sealed class ManagedInfrastructureProviderResolver :
    IManagedInfrastructureProviderResolver
{
    private readonly Dictionary<string, IManagedInfrastructureProvider> _providers;

    public ManagedInfrastructureProviderResolver(
        IEnumerable<IManagedInfrastructureProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var registry = new Dictionary<string, IManagedInfrastructureProvider>(
            StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            ArgumentException.ThrowIfNullOrWhiteSpace(provider.Name);
            if (provider.Name.Length > 128 || !registry.TryAdd(provider.Name, provider))
            {
                throw new ArgumentException(
                    $"Provider name '{provider.Name}' is invalid or duplicated.",
                    nameof(providers));
            }
        }

        if (registry.Count == 0)
        {
            throw new ArgumentException(
                "At least one infrastructure provider is required.",
                nameof(providers));
        }

        _providers = registry;
    }

    public IManagedInfrastructureProvider Resolve(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        if (!_providers.TryGetValue(provider, out var resolved))
        {
            throw new ManagedDeploymentValidationException(
                "provider-not-registered",
                $"Infrastructure provider '{provider}' is not registered.");
        }

        return resolved;
    }
}

/// <summary>Fixed tenant limits with usage calculated from durable desired state.</summary>
public sealed class ManagedDeploymentQuotaSource : IManagedTenantQuotaSource
{
    private readonly IManagedDeploymentStore _store;
    private readonly Dictionary<string, ManagedTenantQuota> _tenantQuotas;
    private readonly ManagedTenantQuota? _defaultQuota;

    public ManagedDeploymentQuotaSource(
        IManagedDeploymentStore store,
        IReadOnlyDictionary<string, ManagedTenantQuota> tenantQuotas,
        ManagedTenantQuota? defaultQuota = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(tenantQuotas);
        if (tenantQuotas.Any(
                static pair =>
                    string.IsNullOrWhiteSpace(pair.Key) ||
                    pair.Key.Length > 128 ||
                    pair.Value is null))
        {
            throw new ArgumentException("Tenant quota entries are invalid.", nameof(tenantQuotas));
        }

        _store = store;
        _tenantQuotas = new Dictionary<string, ManagedTenantQuota>(
            tenantQuotas,
            StringComparer.Ordinal);
        _defaultQuota = defaultQuota;
    }

    public ValueTask<ManagedTenantQuota> GetQuotaAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        cancellationToken.ThrowIfCancellationRequested();
        if (_tenantQuotas.TryGetValue(tenantId, out var quota))
        {
            return ValueTask.FromResult(quota);
        }

        if (_defaultQuota is not null)
        {
            return ValueTask.FromResult(_defaultQuota);
        }

        throw new ManagedDeploymentValidationException(
            "tenant-quota-missing",
            $"Tenant '{tenantId}' has no managed-hosting quota.");
    }

    public async ValueTask<ManagedTenantUsage> GetUsageAsync(
        string tenantId,
        string replacingDeploymentId,
        ManagedDeploymentSpec desired,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacingDeploymentId);
        ArgumentNullException.ThrowIfNull(desired);
        var requested = ManagedDeploymentValidation.GetRequestedUsage(desired);
        var deployments = requested.Deployments;
        var replicas = requested.Replicas;
        var cpu = requested.CpuMillicores;
        var memory = requested.MemoryBytes;
        var storage = requested.StorageBytes;
        await foreach (var deployment in _store.ListAsync(tenantId, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (string.Equals(
                    deployment.Spec.DeploymentId,
                    replacingDeploymentId,
                    StringComparison.Ordinal) ||
                deployment.Status.State == ManagedDeploymentState.Deleted)
            {
                continue;
            }

            var usage = ManagedDeploymentValidation.GetRequestedUsage(deployment.Spec);
            checked
            {
                deployments += usage.Deployments;
                replicas += usage.Replicas;
                cpu += usage.CpuMillicores;
                memory += usage.MemoryBytes;
                storage += usage.StorageBytes;
            }
        }

        return new ManagedTenantUsage(deployments, replicas, cpu, memory, storage);
    }
}

/// <summary>Thread-safe ephemeral storage for tests and single-process development.</summary>
public sealed class InMemoryManagedDeploymentStore :
    IManagedDeploymentStore,
    IManagedDeploymentLeaseStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ManagedDeployment> _deployments =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ManagedDeploymentLease> _leases =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _fencingTokens =
        new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public InMemoryManagedDeploymentStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<ManagedDeployment?> GetAsync(
        string deploymentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return ValueTask.FromResult(
                _deployments.TryGetValue(deploymentId, out var deployment)
                    ? Copy(deployment)
                    : null);
        }
    }

    public async IAsyncEnumerable<ManagedDeployment> ListAsync(
        string? tenantId = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        if (tenantId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        }

        ManagedDeployment[] snapshot;
        lock (_sync)
        {
            snapshot = _deployments.Values
                .Where(
                    deployment =>
                        tenantId is null ||
                        string.Equals(
                            deployment.Spec.TenantId,
                            tenantId,
                            StringComparison.Ordinal))
                .OrderBy(static deployment => deployment.Spec.DeploymentId, StringComparer.Ordinal)
                .Select(Copy)
                .ToArray();
        }

        foreach (var deployment in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return deployment;
            await Task.Yield();
        }
    }

    public ValueTask<ManagedDeployment> PutAsync(
        ManagedDeploymentSpec spec,
        long expectedGeneration,
        CancellationToken cancellationToken = default)
    {
        ManagedDeploymentValidation.Validate(spec);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedGeneration);

        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_deployments.TryGetValue(spec.DeploymentId, out var existing))
            {
                if (expectedGeneration != 0 || spec.Generation != 1)
                {
                    throw Conflict("A new deployment must use expected generation zero and generation one.");
                }

                var created = new ManagedDeployment(
                    Copy(spec),
                    new ManagedDeploymentStatus(
                        ManagedDeploymentState.Pending,
                        0,
                        0,
                        null,
                        null,
                        null,
                        null,
                        null,
                        _timeProvider.GetUtcNow()));
                _deployments.Add(spec.DeploymentId, created);
                return ValueTask.FromResult(Copy(created));
            }

            if (existing.Spec.Generation != expectedGeneration)
            {
                throw Conflict("Desired-state generation no longer matches.");
            }

            if (spec.Generation == existing.Spec.Generation)
            {
                var existingFingerprint = ManagedDeploymentValidation.GetFingerprint(existing.Spec);
                var desiredFingerprint = ManagedDeploymentValidation.GetFingerprint(spec);
                if (!string.Equals(existingFingerprint, desiredFingerprint, StringComparison.Ordinal))
                {
                    throw Conflict("A generation cannot identify two different desired states.");
                }

                return ValueTask.FromResult(Copy(existing));
            }

            if (spec.Generation != checked(existing.Spec.Generation + 1))
            {
                throw Conflict("Desired-state generations must increase by exactly one.");
            }

            if (!string.Equals(spec.TenantId, existing.Spec.TenantId, StringComparison.Ordinal) ||
                !string.Equals(spec.Provider, existing.Spec.Provider, StringComparison.Ordinal) ||
                !string.Equals(spec.Region, existing.Spec.Region, StringComparison.Ordinal))
            {
                throw new ManagedDeploymentValidationException(
                    "deployment-placement-immutable",
                    "Tenant, provider, and region cannot change after deployment creation.");
            }

            var updated = new ManagedDeployment(
                Copy(spec),
                existing.Status with { State = ManagedDeploymentState.Pending });
            _deployments[spec.DeploymentId] = updated;
            return ValueTask.FromResult(Copy(updated));
        }
    }

    public ValueTask<ManagedDeployment> UpdateStatusAsync(
        string deploymentId,
        ManagedDeploymentStatus status,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);
        ArgumentNullException.ThrowIfNull(status);
        if (expectedRevision < 0 ||
            status.Revision != checked(expectedRevision + 1) ||
            status.ObservedGeneration <= 0 ||
            !Enum.IsDefined(status.State))
        {
            throw new ArgumentException("Managed deployment status is invalid.", nameof(status));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_deployments.TryGetValue(deploymentId, out var existing))
            {
                throw new KeyNotFoundException($"Deployment '{deploymentId}' does not exist.");
            }

            if (existing.Status.Revision != expectedRevision ||
                status.ObservedGeneration != existing.Spec.Generation)
            {
                throw Conflict("Observed state or desired generation changed.");
            }

            var now = _timeProvider.GetUtcNow();
            if (status.FencingToken is not long token ||
                !_leases.TryGetValue(deploymentId, out var lease) ||
                lease.FencingToken != token ||
                lease.ExpiresAt <= now)
            {
                throw new ManagedDeploymentLeaseException(
                    "Observed state cannot be written without the current lease.");
            }

            var updated = new ManagedDeployment(existing.Spec, status);
            _deployments[deploymentId] = updated;
            return ValueTask.FromResult(Copy(updated));
        }
    }

    public ValueTask<ManagedDeploymentLease?> TryAcquireAsync(
        string deploymentId,
        string owner,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ValidateLeaseInput(deploymentId, owner, duration);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            if (_leases.TryGetValue(deploymentId, out var existing) &&
                existing.ExpiresAt > now)
            {
                return ValueTask.FromResult<ManagedDeploymentLease?>(null);
            }

            var token = checked(_fencingTokens.GetValueOrDefault(deploymentId) + 1);
            _fencingTokens[deploymentId] = token;
            var lease = new ManagedDeploymentLease(
                deploymentId,
                owner,
                token,
                now + duration);
            _leases[deploymentId] = lease;
            return ValueTask.FromResult<ManagedDeploymentLease?>(lease);
        }
    }

    public ValueTask<ManagedDeploymentLease> RenewAsync(
        ManagedDeploymentLease lease,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateLeaseInput(lease.DeploymentId, lease.Owner, duration);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            if (!_leases.TryGetValue(lease.DeploymentId, out var existing) ||
                existing.ExpiresAt <= now ||
                existing.FencingToken != lease.FencingToken ||
                !string.Equals(existing.Owner, lease.Owner, StringComparison.Ordinal))
            {
                throw new ManagedDeploymentLeaseException(
                    "The managed deployment lease expired or was fenced.");
            }

            var renewed = existing with { ExpiresAt = now + duration };
            _leases[lease.DeploymentId] = renewed;
            return ValueTask.FromResult(renewed);
        }
    }

    public ValueTask ReleaseAsync(
        ManagedDeploymentLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_leases.TryGetValue(lease.DeploymentId, out var existing) ||
                existing.FencingToken != lease.FencingToken ||
                !string.Equals(existing.Owner, lease.Owner, StringComparison.Ordinal))
            {
                throw new ManagedDeploymentLeaseException(
                    "The managed deployment lease was already fenced.");
            }

            _leases.Remove(lease.DeploymentId);
            return ValueTask.CompletedTask;
        }
    }

    private static void ValidateLeaseInput(
        string deploymentId,
        string owner,
        TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (deploymentId.Length > 128 ||
            owner.Length > 512 ||
            duration <= TimeSpan.Zero ||
            duration > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Lease input is invalid.");
        }
    }

    private static ManagedDeployment Copy(ManagedDeployment deployment) =>
        new(Copy(deployment.Spec), deployment.Status);

    private static ManagedDeploymentSpec Copy(ManagedDeploymentSpec spec) =>
        spec with
        {
            Workloads = Array.AsReadOnly(
                spec.Workloads.Select(Copy).ToArray()),
            Labels = ManagedHostingCollections.Copy(spec.Labels),
        };

    private static ManagedWorkloadSpec Copy(ManagedWorkloadSpec workload) =>
        workload with
        {
            SecretReferences = Array.AsReadOnly(workload.SecretReferences.ToArray()),
            Settings = ManagedHostingCollections.Copy(workload.Settings),
        };

    private static ManagedDeploymentConcurrencyException Conflict(string message) =>
        new(message);
}
