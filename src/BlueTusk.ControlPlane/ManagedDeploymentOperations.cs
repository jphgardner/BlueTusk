namespace BlueTusk.ControlPlane;

/// <summary>Performs provider-specific rebuild preparation before normal reconciliation.</summary>
public interface IManagedDeploymentRebuildHandler
{
    ValueTask RebuildAsync(
        ManagedDeployment deployment,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Routes audited fleet commands to durable desired-state and fenced controller operations.
/// </summary>
public sealed class ManagedDeploymentControlPlaneOperationHandler : IControlPlaneOperationHandler
{
    private const string TargetPrefix = "deployment:";
    private readonly IManagedDeploymentStore _store;
    private readonly ManagedDeploymentController _controller;
    private readonly IManagedDeploymentRebuildHandler _rebuilds;
    private readonly IControlPlaneOperationHandler? _fallback;

    public ManagedDeploymentControlPlaneOperationHandler(
        IManagedDeploymentStore store,
        ManagedDeploymentController controller,
        IManagedDeploymentRebuildHandler rebuilds,
        IControlPlaneOperationHandler? fallback = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _rebuilds = rebuilds ?? throw new ArgumentNullException(nameof(rebuilds));
        _fallback = fallback;
    }

    public async ValueTask ExecuteAsync(
        ControlPlaneOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind is not (ControlPlaneOperationKind.PauseDeployment or
            ControlPlaneOperationKind.ResumeDeployment or
            ControlPlaneOperationKind.ReconcileDeployment or
            ControlPlaneOperationKind.RebuildDeployment or
            ControlPlaneOperationKind.DeleteDeployment))
        {
            if (_fallback is null)
            {
                throw new NotSupportedException(
                    $"Operation '{request.Kind}' is not a managed-deployment operation.");
            }

            await _fallback.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        var deploymentId = ParseTarget(request.Target);
        var deployment = await _store.GetAsync(deploymentId, cancellationToken)
            .ConfigureAwait(false) ??
            throw new KeyNotFoundException($"Deployment '{deploymentId}' does not exist.");
        switch (request.Kind)
        {
            case ControlPlaneOperationKind.PauseDeployment:
                await SetPausedAsync(deployment, paused: true, cancellationToken).ConfigureAwait(false);
                break;
            case ControlPlaneOperationKind.ResumeDeployment:
                await SetPausedAsync(deployment, paused: false, cancellationToken).ConfigureAwait(false);
                break;
            case ControlPlaneOperationKind.ReconcileDeployment:
                _ = await _controller.ReconcileAsync(deploymentId, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case ControlPlaneOperationKind.RebuildDeployment:
                await _rebuilds.RebuildAsync(deployment, cancellationToken).ConfigureAwait(false);
                _ = await _controller.ReconcileAsync(deploymentId, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case ControlPlaneOperationKind.DeleteDeployment:
                _ = await _controller.DeleteAsync(
                    deploymentId,
                    deployment.Spec.Generation,
                    overrideProtection: false,
                    cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async ValueTask SetPausedAsync(
        ManagedDeployment deployment,
        bool paused,
        CancellationToken cancellationToken)
    {
        if (deployment.Spec.Paused == paused)
        {
            _ = await _controller.ReconcileAsync(
                deployment.Spec.DeploymentId,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var desired = deployment.Spec with
        {
            Generation = checked(deployment.Spec.Generation + 1),
            Paused = paused,
        };
        _ = await _store.PutAsync(
            desired,
            deployment.Spec.Generation,
            cancellationToken).ConfigureAwait(false);
        _ = await _controller.ReconcileAsync(
            deployment.Spec.DeploymentId,
            cancellationToken).ConfigureAwait(false);
    }

    private static string ParseTarget(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (!target.StartsWith(TargetPrefix, StringComparison.Ordinal) ||
            target.Length == TargetPrefix.Length ||
            target.Length > TargetPrefix.Length + 128)
        {
            throw new ArgumentException(
                "Managed-deployment targets must use 'deployment:<deployment-id>'.",
                nameof(target));
        }

        return target[TargetPrefix.Length..];
    }
}
