namespace BlueTusk.ControlPlane.Kubernetes;

/// <summary>
/// Converts BlueTuskDeployment resources into fenced managed-hosting reconciliations.
/// </summary>
public sealed class KubernetesManagedDeploymentOperator
{
    public const string Finalizer = "controlplane.bluetusk.io/finalizer";

    private readonly IManagedDeploymentStore _store;
    private readonly ManagedDeploymentController _controller;
    private readonly IKubernetesManagedDeploymentClient _client;
    private readonly int _maximumConcurrency;
    private readonly int _pageSize;
    private readonly TimeProvider _timeProvider;

    public KubernetesManagedDeploymentOperator(
        IManagedDeploymentStore store,
        ManagedDeploymentController controller,
        IKubernetesManagedDeploymentClient client,
        int maximumConcurrency = 4,
        int pageSize = 100,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConcurrency, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumConcurrency, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 500);
        _maximumConcurrency = maximumConcurrency;
        _pageSize = pageSize;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<IReadOnlyList<KubernetesManagedDeploymentReconcileResult>> ReconcileAllAsync(
        CancellationToken cancellationToken = default)
    {
        var resources = new List<KubernetesManagedDeploymentResource>();
        string? continuation = null;
        do
        {
            var page = await _client.ListAsync(_pageSize, continuation, cancellationToken)
                .ConfigureAwait(false);
            if (page.Resources.Count > _pageSize)
            {
                throw new InvalidOperationException("Kubernetes returned more custom resources than requested.");
            }

            resources.AddRange(page.Resources);
            continuation = page.ContinuationToken;
        }
        while (continuation is not null);

        var results = new KubernetesManagedDeploymentReconcileResult[resources.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, resources.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _maximumConcurrency,
                CancellationToken = cancellationToken,
            },
            async (index, token) =>
            {
                results[index] = await ReconcileAsync(resources[index], token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        return Array.AsReadOnly(results);
    }

    public async ValueTask<KubernetesManagedDeploymentReconcileResult> ReconcileAsync(
        KubernetesManagedDeploymentResource resource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ValidateResource(resource);
        var current = resource;
        try
        {
            if (!current.Finalizers.Contains(Finalizer, StringComparer.Ordinal))
            {
                if (current.DeletionTimestamp is not null)
                {
                    return new KubernetesManagedDeploymentReconcileResult(
                        current.DeploymentId,
                        succeeded: true,
                        changed: false,
                        diagnosticCode: null);
                }

                current = await _client.ReplaceFinalizersAsync(
                    current,
                    Array.AsReadOnly(current.Finalizers.Append(Finalizer).Distinct(StringComparer.Ordinal).ToArray()),
                    cancellationToken).ConfigureAwait(false);
            }

            var existing = await _store.GetAsync(current.DeploymentId, cancellationToken)
                .ConfigureAwait(false);
            if (current.DeletionTimestamp is not null)
            {
                if (existing is not null && existing.Status.State != ManagedDeploymentState.Deleted)
                {
                    _ = await _controller.DeleteAsync(
                        current.DeploymentId,
                        existing.Spec.Generation,
                        overrideProtection: false,
                        cancellationToken).ConfigureAwait(false);
                }

                _ = await _client.ReplaceFinalizersAsync(
                    current,
                    Array.AsReadOnly(
                        current.Finalizers.Where(static value =>
                                !string.Equals(value, Finalizer, StringComparison.Ordinal))
                            .ToArray()),
                    cancellationToken).ConfigureAwait(false);
                return new KubernetesManagedDeploymentReconcileResult(
                    current.DeploymentId,
                    succeeded: true,
                    changed: existing is not null,
                    diagnosticCode: null);
            }

            var desired = GetDesired(current, existing);
            var stored = await _store.PutAsync(
                desired,
                existing?.Spec.Generation ?? 0,
                cancellationToken).ConfigureAwait(false);
            var reconciled = await _controller.ReconcileAsync(current.DeploymentId, cancellationToken)
                .ConfigureAwait(false);
            var status = new KubernetesManagedDeploymentStatus(
                current.Generation,
                stored.Spec.Generation,
                reconciled.State,
                reconciled.DiagnosticCode,
                _timeProvider.GetUtcNow());
            await _client.ReplaceStatusAsync(current, status, cancellationToken).ConfigureAwait(false);
            return new KubernetesManagedDeploymentReconcileResult(
                current.DeploymentId,
                succeeded: true,
                changed: reconciled.Changed || existing is null ||
                    existing.Spec.Generation != stored.Spec.Generation,
                diagnosticCode: reconciled.DiagnosticCode);
        }
        catch (Exception exception) when (exception is ManagedDeploymentValidationException or
                                          ManagedDeploymentConcurrencyException or
                                          ManagedDeploymentLeaseException or
                                          KeyNotFoundException)
        {
            var existing = await _store.GetAsync(current.DeploymentId, cancellationToken)
                .ConfigureAwait(false);
            var code = DiagnosticCode(exception);
            await _client.ReplaceStatusAsync(
                current,
                new KubernetesManagedDeploymentStatus(
                    current.Generation,
                    existing?.Spec.Generation ?? 0,
                    ManagedDeploymentState.Failed,
                    code,
                    _timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
            return new KubernetesManagedDeploymentReconcileResult(
                current.DeploymentId,
                succeeded: false,
                changed: false,
                diagnosticCode: code);
        }
    }

    private static ManagedDeploymentSpec GetDesired(
        KubernetesManagedDeploymentResource resource,
        ManagedDeployment? existing)
    {
        var candidate = resource.Desired with
        {
            DeploymentId = resource.DeploymentId,
            Generation = existing?.Spec.Generation ?? 1,
        };
        ManagedDeploymentValidation.Validate(candidate);
        if (existing is null)
        {
            return candidate;
        }

        return string.Equals(
            ManagedDeploymentValidation.GetFingerprint(candidate),
            ManagedDeploymentValidation.GetFingerprint(existing.Spec),
            StringComparison.Ordinal)
            ? candidate
            : candidate with { Generation = checked(existing.Spec.Generation + 1) };
    }

    private static void ValidateResource(KubernetesManagedDeploymentResource resource)
    {
        ValidateToken(resource.ResourceNamespace, 63, nameof(resource.ResourceNamespace));
        ValidateToken(resource.Name, 253, nameof(resource.Name));
        ValidateToken(resource.Uid, 128, nameof(resource.Uid));
        ValidateToken(resource.ResourceVersion, 128, nameof(resource.ResourceVersion));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resource.Generation);
        if (resource.Finalizers.Count > 32 ||
            resource.Finalizers.Any(static value => string.IsNullOrWhiteSpace(value) || value.Length > 253))
        {
            throw new ArgumentException("Kubernetes finalizers are invalid.", nameof(resource));
        }
    }

    private static void ValidateToken(string value, int maximumLength, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        if (value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new ArgumentException("Kubernetes resource identity is invalid.", parameter);
        }
    }

    private static string DiagnosticCode(Exception exception) => exception switch
    {
        ManagedDeploymentValidationException validation => validation.Code,
        ManagedDeploymentConcurrencyException => "concurrent-update",
        ManagedDeploymentLeaseException => "lease-unavailable",
        KeyNotFoundException => "deployment-not-found",
        _ => "reconcile-failed",
    };
}
