namespace BlueTusk.ControlPlane;

/// <summary>Bounded deployment inventory filters and an exclusive deployment-ID cursor.</summary>
public sealed class ManagedDeploymentQuery
{
    public int PageSize { get; init; } = 100;
    public string? AfterDeploymentId { get; init; }
    public string? DeploymentIdPrefix { get; init; }
    public string? TenantId { get; init; }
    public string? Provider { get; init; }
    public string? Region { get; init; }
    public ManagedDeploymentState? State { get; init; }
    public ManagedWorkloadKind? WorkloadKind { get; init; }

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(PageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(PageSize, 500);
        ValidateOptional(AfterDeploymentId, nameof(AfterDeploymentId));
        ValidateOptional(DeploymentIdPrefix, nameof(DeploymentIdPrefix));
        ValidateOptional(TenantId, nameof(TenantId));
        ValidateOptional(Provider, nameof(Provider));
        ValidateOptional(Region, nameof(Region));
        if (State is { } state && !Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(State));
        if (WorkloadKind is { } kind && !Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(WorkloadKind));
    }

    internal bool Matches(ManagedDeployment deployment) =>
        (DeploymentIdPrefix is null || deployment.Spec.DeploymentId.StartsWith(DeploymentIdPrefix, StringComparison.Ordinal)) &&
        (TenantId is null || string.Equals(deployment.Spec.TenantId, TenantId, StringComparison.Ordinal)) &&
        (Provider is null || string.Equals(deployment.Spec.Provider, Provider, StringComparison.Ordinal)) &&
        (Region is null || string.Equals(deployment.Spec.Region, Region, StringComparison.Ordinal)) &&
        (State is null || deployment.Status.State == State) &&
        (WorkloadKind is null || deployment.Spec.Workloads.Any(workload => workload.Kind == WorkloadKind));

    private static void ValidateOptional(string? value, string parameter)
    {
        if (value is null) return;
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        if (value.Length > 128 || value.Any(character => char.IsControl(character) || char.IsSurrogate(character)))
        {
            throw new ArgumentException("Inventory filters must be bounded printable values.", parameter);
        }
    }
}

/// <summary>One bounded store page; the next cursor is the last included deployment ID.</summary>
public sealed class ManagedDeploymentPage
{
    public ManagedDeploymentPage(IReadOnlyList<ManagedDeployment> deployments, string? nextAfterDeploymentId)
    {
        Deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
        NextAfterDeploymentId = nextAfterDeploymentId;
    }

    public IReadOnlyList<ManagedDeployment> Deployments { get; }
    public string? NextAfterDeploymentId { get; }
}
