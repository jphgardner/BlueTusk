namespace BlueTusk.ControlPlane;

public interface IControlPlaneQueryService
{
    ValueTask<ControlPlaneOverview> GetOverviewAsync(
        CancellationToken cancellationToken = default);
}
