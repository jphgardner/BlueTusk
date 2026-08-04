namespace BlueTusk.ControlPlane;

/// <summary>Durable compare-and-swap storage for managed desired and observed state.</summary>
public interface IManagedDeploymentStore
{
    ValueTask<ManagedDeployment?> GetAsync(
        string deploymentId,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ManagedDeployment> ListAsync(
        string? tenantId = null,
        CancellationToken cancellationToken = default);

    ValueTask<ManagedDeployment> PutAsync(
        ManagedDeploymentSpec spec,
        long expectedGeneration,
        CancellationToken cancellationToken = default);

    ValueTask<ManagedDeployment> UpdateStatusAsync(
        string deploymentId,
        ManagedDeploymentStatus status,
        long expectedRevision,
        CancellationToken cancellationToken = default);
}

/// <summary>Exclusive lease storage with monotonically increasing fencing tokens.</summary>
public interface IManagedDeploymentLeaseStore
{
    ValueTask<ManagedDeploymentLease?> TryAcquireAsync(
        string deploymentId,
        string owner,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    ValueTask<ManagedDeploymentLease> RenewAsync(
        ManagedDeploymentLease lease,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    ValueTask ReleaseAsync(
        ManagedDeploymentLease lease,
        CancellationToken cancellationToken = default);
}

/// <summary>Supplies enforced limits and current usage for a tenant.</summary>
public interface IManagedTenantQuotaSource
{
    ValueTask<ManagedTenantQuota> GetQuotaAsync(
        string tenantId,
        CancellationToken cancellationToken = default);

    ValueTask<ManagedTenantUsage> GetUsageAsync(
        string tenantId,
        string replacingDeploymentId,
        ManagedDeploymentSpec desired,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Plans and applies infrastructure without returning credentials or accepting raw secret values.
/// </summary>
public interface IManagedInfrastructureProvider
{
    string Name { get; }

    ValueTask<ManagedDeploymentPlan> PlanAsync(
        ManagedDeploymentSpec desired,
        ManagedDeploymentStatus current,
        CancellationToken cancellationToken = default);

    ValueTask<ManagedProviderResult> ApplyAsync(
        ManagedDeploymentSpec desired,
        ManagedDeploymentPlan plan,
        long fencingToken,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(
        ManagedDeploymentSpec desired,
        long fencingToken,
        CancellationToken cancellationToken = default);
}

/// <summary>The durable, non-sensitive result of a provider apply.</summary>
public sealed record ManagedProviderResult(
    string ProviderResourceId,
    string AppliedPlanFingerprint);

/// <summary>Resolves the provider adapter selected by desired state.</summary>
public interface IManagedInfrastructureProviderResolver
{
    IManagedInfrastructureProvider Resolve(string provider);
}

/// <summary>Reports a compare-and-swap conflict without hiding concurrent desired-state changes.</summary>
public sealed class ManagedDeploymentConcurrencyException : Exception
{
    public ManagedDeploymentConcurrencyException(string message)
        : base(message)
    {
    }
}

/// <summary>Reports an invalid or over-quota managed deployment before provider mutation.</summary>
public sealed class ManagedDeploymentValidationException : Exception
{
    public ManagedDeploymentValidationException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}

/// <summary>Reports lease loss or a stale fencing token.</summary>
public sealed class ManagedDeploymentLeaseException : Exception
{
    public ManagedDeploymentLeaseException(string message)
        : base(message)
    {
    }
}
