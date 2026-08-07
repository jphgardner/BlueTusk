using System.Collections.ObjectModel;

namespace BlueTusk.ControlPlane;

/// <summary>The lifecycle observed for a managed deployment.</summary>
public enum ManagedDeploymentState
{
    Pending,
    Planning,
    Applying,
    Ready,
    Degraded,
    Paused,
    Deleting,
    Deleted,
    Failed,
}

/// <summary>A deployable BlueTusk workload family.</summary>
public enum ManagedWorkloadKind
{
    Streams,
    Sync,
    Live,
    ControlPlane,
    Dashboard,
    ContinuousGraph,
}

/// <summary>A provider-owned secret reference. Secret material never enters the control-plane model.</summary>
public sealed record ManagedSecretReference(
    string Store,
    string Name,
    string? Version = null);

/// <summary>Bounded compute and storage assigned to one workload.</summary>
public sealed record ManagedResourceRequest(
    int Replicas,
    int CpuMillicoresPerReplica,
    long MemoryBytesPerReplica,
    long StorageBytes);

/// <summary>Desired state for one managed workload.</summary>
public sealed record ManagedWorkloadSpec(
    ManagedWorkloadKind Kind,
    string Version,
    ManagedResourceRequest Resources,
    IReadOnlyList<ManagedSecretReference> SecretReferences,
    IReadOnlyDictionary<string, string> Settings);

/// <summary>Versioned desired state for an isolated managed deployment.</summary>
public sealed record ManagedDeploymentSpec(
    string DeploymentId,
    string TenantId,
    string Provider,
    string Region,
    long Generation,
    bool Paused,
    bool DeleteProtection,
    IReadOnlyList<ManagedWorkloadSpec> Workloads,
    IReadOnlyDictionary<string, string> Labels);

/// <summary>Aggregated tenant limits enforced before a plan reaches a provider.</summary>
public sealed record ManagedTenantQuota(
    int MaximumDeployments,
    int MaximumReplicas,
    long MaximumCpuMillicores,
    long MaximumMemoryBytes,
    long MaximumStorageBytes);

/// <summary>Current quota consumption, including the deployment being reconciled.</summary>
public sealed record ManagedTenantUsage(
    int Deployments,
    int Replicas,
    long CpuMillicores,
    long MemoryBytes,
    long StorageBytes);

/// <summary>A non-sensitive provider action included in an immutable deployment plan.</summary>
public sealed record ManagedDeploymentAction(
    string Kind,
    string Resource,
    string Summary);

/// <summary>An immutable, fingerprinted provider plan.</summary>
public sealed record ManagedDeploymentPlan(
    string DeploymentId,
    long Generation,
    string DesiredFingerprint,
    string PlanFingerprint,
    bool RequiresChange,
    IReadOnlyList<ManagedDeploymentAction> Actions);

/// <summary>The durable observed state of a managed deployment.</summary>
public sealed record ManagedDeploymentStatus(
    ManagedDeploymentState State,
    long ObservedGeneration,
    long Revision,
    long? FencingToken,
    string? DesiredFingerprint,
    string? AppliedPlanFingerprint,
    string? ProviderResourceId,
    string? DiagnosticCode,
    DateTimeOffset UpdatedAt);

/// <summary>Desired and observed state stored as one logical deployment record.</summary>
public sealed record ManagedDeployment(
    ManagedDeploymentSpec Spec,
    ManagedDeploymentStatus Status);

/// <summary>An exclusive, fenced reconciliation lease.</summary>
public sealed record ManagedDeploymentLease(
    string DeploymentId,
    string Owner,
    long FencingToken,
    DateTimeOffset ExpiresAt);

/// <summary>The outcome of one bounded reconciliation.</summary>
public sealed record ManagedReconcileResult(
    string DeploymentId,
    long Generation,
    ManagedDeploymentState State,
    bool Changed,
    string? DiagnosticCode);

/// <summary>Utilities for producing immutable, ordinally compared public collections.</summary>
internal static class ManagedHostingCollections
{
    internal static IReadOnlyDictionary<string, string> Copy(
        IReadOnlyDictionary<string, string> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(source, StringComparer.Ordinal));
    }

    internal static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Array.AsReadOnly(source.ToArray());
    }
}
