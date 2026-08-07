using BlueTusk.ContinuousGraph;

namespace BlueTusk.ControlPlane;

public sealed class HostedContinuousGraphControlPlaneQueryService :
    IControlPlaneContinuousGraphQueryService
{
    private readonly ContinuousGraphQueryRegistry _registry;
    private readonly TimeProvider _timeProvider;

    public HostedContinuousGraphControlPlaneQueryService(
        ContinuousGraphQueryRegistry registry,
        TimeProvider? timeProvider = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<ControlPlaneContinuousGraphOverview> GetContinuousGraphOverviewAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var queries = _registry.GetQueries()
            .Select(static query => new ControlPlaneContinuousGraphQuerySnapshot(
                query.Name,
                query.DatabaseIdentity,
                query.Fingerprint,
                query.GraphName,
                query.GraphSchema,
                query.ElementTableAliases,
                query.Dependencies
                    .Select(static dependency => dependency.ToString())
                    .ToArray(),
                query.MaximumResultCount,
                query.Capabilities.ToString()))
            .ToArray();
        return ValueTask.FromResult(
            new ControlPlaneContinuousGraphOverview(
                _timeProvider.GetUtcNow(),
                queries));
    }
}
