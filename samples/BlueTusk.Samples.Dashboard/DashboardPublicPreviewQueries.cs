using BlueTusk.ControlPlane;

internal sealed class DashboardPublicPreviewQueries :
    IControlPlaneQueryService,
    IControlPlaneSyncQueryService,
    IControlPlaneLiveQueryService,
    IControlPlaneFleetQueryService
{
    public ValueTask<ControlPlaneOverview> GetOverviewAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ControlPlaneOverview(DateTimeOffset.UtcNow, []));
    }

    public ValueTask<ControlPlaneSyncOverview> GetSyncOverviewAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ControlPlaneSyncOverview(DateTimeOffset.UtcNow, []));
    }

    public ValueTask<ControlPlaneLiveOverview> GetLiveOverviewAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ControlPlaneLiveOverview(
            DateTimeOffset.UtcNow,
            new ControlPlaneLiveRegistrySnapshot(0, 0, 0),
            []));
    }

    public ValueTask<ControlPlaneFleetOverview> GetFleetOverviewAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ControlPlaneFleetOverview(DateTimeOffset.UtcNow, []));
    }
}
