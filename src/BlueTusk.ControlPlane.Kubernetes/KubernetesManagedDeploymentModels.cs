namespace BlueTusk.ControlPlane.Kubernetes;

/// <summary>One immutable BlueTuskDeployment custom resource observation.</summary>
public sealed class KubernetesManagedDeploymentResource
{
    public KubernetesManagedDeploymentResource(
        string resourceNamespace,
        string name,
        string uid,
        string resourceVersion,
        long generation,
        DateTimeOffset? deletionTimestamp,
        IReadOnlyList<string> finalizers,
        ManagedDeploymentSpec desired)
    {
        ResourceNamespace = resourceNamespace;
        Name = name;
        Uid = uid;
        ResourceVersion = resourceVersion;
        Generation = generation;
        DeletionTimestamp = deletionTimestamp;
        Finalizers = finalizers ?? throw new ArgumentNullException(nameof(finalizers));
        Desired = desired ?? throw new ArgumentNullException(nameof(desired));
    }

    public string ResourceNamespace { get; }

    public string Name { get; }

    public string Uid { get; }

    public string ResourceVersion { get; }

    public long Generation { get; }

    public DateTimeOffset? DeletionTimestamp { get; }

    public IReadOnlyList<string> Finalizers { get; }

    public ManagedDeploymentSpec Desired { get; }

    public string DeploymentId => ResourceNamespace + "/" + Name;
}

/// <summary>A bounded page returned by the Kubernetes custom-resource API.</summary>
public sealed class KubernetesManagedDeploymentPage
{
    public KubernetesManagedDeploymentPage(
        IReadOnlyList<KubernetesManagedDeploymentResource> resources,
        string? continuationToken)
    {
        Resources = resources ?? throw new ArgumentNullException(nameof(resources));
        ContinuationToken = continuationToken;
    }

    public IReadOnlyList<KubernetesManagedDeploymentResource> Resources { get; }

    public string? ContinuationToken { get; }
}

/// <summary>Non-sensitive status written to a BlueTuskDeployment status subresource.</summary>
public sealed class KubernetesManagedDeploymentStatus
{
    public KubernetesManagedDeploymentStatus(
        long observedResourceGeneration,
        long managedGeneration,
        ManagedDeploymentState state,
        string? diagnosticCode,
        DateTimeOffset updatedAt)
    {
        ObservedResourceGeneration = observedResourceGeneration;
        ManagedGeneration = managedGeneration;
        State = state;
        DiagnosticCode = diagnosticCode;
        UpdatedAt = updatedAt;
    }

    public long ObservedResourceGeneration { get; }

    public long ManagedGeneration { get; }

    public ManagedDeploymentState State { get; }

    public string? DiagnosticCode { get; }

    public DateTimeOffset UpdatedAt { get; }
}

/// <summary>The isolated outcome of reconciling one custom resource.</summary>
public sealed class KubernetesManagedDeploymentReconcileResult
{
    public KubernetesManagedDeploymentReconcileResult(
        string deploymentId,
        bool succeeded,
        bool changed,
        string? diagnosticCode)
    {
        DeploymentId = deploymentId;
        Succeeded = succeeded;
        Changed = changed;
        DiagnosticCode = diagnosticCode;
    }

    public string DeploymentId { get; }

    public bool Succeeded { get; }

    public bool Changed { get; }

    public string? DiagnosticCode { get; }
}

/// <summary>Abstracts Kubernetes list and compare-and-swap patch operations.</summary>
public interface IKubernetesManagedDeploymentClient
{
    ValueTask<KubernetesManagedDeploymentPage> ListAsync(
        int limit,
        string? continuationToken = null,
        CancellationToken cancellationToken = default);

    ValueTask<KubernetesManagedDeploymentResource> ReplaceFinalizersAsync(
        KubernetesManagedDeploymentResource resource,
        IReadOnlyList<string> finalizers,
        CancellationToken cancellationToken = default);

    ValueTask ReplaceStatusAsync(
        KubernetesManagedDeploymentResource resource,
        KubernetesManagedDeploymentStatus status,
        CancellationToken cancellationToken = default);
}
