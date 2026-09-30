namespace BlueTusk.ControlPlane;

/// <summary>One redacted, coherent view of all managed BlueTusk deployments.</summary>
public sealed class ControlPlaneFleetOverview
{
    public ControlPlaneFleetOverview(
        DateTimeOffset observedAt,
        IReadOnlyList<ControlPlaneManagedDeploymentSnapshot> deployments)
    {
        ObservedAt = observedAt;
        Deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
    }

    public DateTimeOffset ObservedAt { get; }

    public IReadOnlyList<ControlPlaneManagedDeploymentSnapshot> Deployments { get; }
}

/// <summary>Non-sensitive desired and observed state for one managed deployment.</summary>
public sealed class ControlPlaneManagedDeploymentSnapshot
{
    public ControlPlaneManagedDeploymentSnapshot(
        string deploymentId,
        string tenantId,
        string provider,
        string region,
        long desiredGeneration,
        long observedGeneration,
        long statusRevision,
        ManagedDeploymentState state,
        bool paused,
        bool deleteProtection,
        int workloadCount,
        IReadOnlyList<ManagedWorkloadKind> workloadKinds,
        int replicas,
        long cpuMillicores,
        long memoryBytes,
        long storageBytes,
        string? diagnosticCode,
        DateTimeOffset updatedAt)
    {
        DeploymentId = deploymentId;
        TenantId = tenantId;
        Provider = provider;
        Region = region;
        DesiredGeneration = desiredGeneration;
        ObservedGeneration = observedGeneration;
        StatusRevision = statusRevision;
        State = state;
        Paused = paused;
        DeleteProtection = deleteProtection;
        WorkloadCount = workloadCount;
        WorkloadKinds = workloadKinds ?? throw new ArgumentNullException(nameof(workloadKinds));
        Replicas = replicas;
        CpuMillicores = cpuMillicores;
        MemoryBytes = memoryBytes;
        StorageBytes = storageBytes;
        DiagnosticCode = diagnosticCode;
        UpdatedAt = updatedAt;
    }

    public string DeploymentId { get; }

    public string TenantId { get; }

    public string Provider { get; }

    public string Region { get; }

    public long DesiredGeneration { get; }

    public long ObservedGeneration { get; }

    public long StatusRevision { get; }

    public ManagedDeploymentState State { get; }

    public bool Paused { get; }

    public bool DeleteProtection { get; }

    public int WorkloadCount { get; }

    public IReadOnlyList<ManagedWorkloadKind> WorkloadKinds { get; }

    public int Replicas { get; }

    public long CpuMillicores { get; }

    public long MemoryBytes { get; }

    public long StorageBytes { get; }

    public string? DiagnosticCode { get; }

    public DateTimeOffset UpdatedAt { get; }
}

/// <summary>Reads a redacted deployment fleet without exposing settings or secret references.</summary>
public interface IControlPlaneFleetQueryService
{
    /// <summary>Gets the latest fleet overview in stable deployment order.</summary>
    ValueTask<ControlPlaneFleetOverview> GetFleetOverviewAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Projects durable managed-hosting state into a redacted fleet inventory.</summary>
public sealed class ManagedDeploymentFleetQueryService : IControlPlaneFleetQueryService
{
    private readonly IManagedDeploymentStore _store;
    private readonly TimeProvider _timeProvider;

    public ManagedDeploymentFleetQueryService(
        IManagedDeploymentStore store,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<ControlPlaneFleetOverview> GetFleetOverviewAsync(
        CancellationToken cancellationToken = default)
    {
        var deployments = new List<ControlPlaneManagedDeploymentSnapshot>();
        await foreach (var deployment in _store.ListAsync(cancellationToken: cancellationToken)
                           .ConfigureAwait(false))
        {
            var usage = ManagedDeploymentValidation.GetRequestedUsage(deployment.Spec);
            deployments.Add(new ControlPlaneManagedDeploymentSnapshot(
                deployment.Spec.DeploymentId,
                deployment.Spec.TenantId,
                deployment.Spec.Provider,
                deployment.Spec.Region,
                deployment.Spec.Generation,
                deployment.Status.ObservedGeneration,
                deployment.Status.Revision,
                deployment.Status.State,
                deployment.Spec.Paused,
                deployment.Spec.DeleteProtection,
                deployment.Spec.Workloads.Count,
                Array.AsReadOnly(
                    deployment.Spec.Workloads
                        .Select(static workload => workload.Kind)
                        .OrderBy(static kind => kind)
                        .ToArray()),
                usage.Replicas,
                usage.CpuMillicores,
                usage.MemoryBytes,
                usage.StorageBytes,
                deployment.Status.DiagnosticCode,
                deployment.Status.UpdatedAt));
        }

        return new ControlPlaneFleetOverview(
            _timeProvider.GetUtcNow(),
            Array.AsReadOnly(
                deployments
                    .OrderBy(static deployment => deployment.DeploymentId, StringComparer.Ordinal)
                    .ToArray()));
    }
}
