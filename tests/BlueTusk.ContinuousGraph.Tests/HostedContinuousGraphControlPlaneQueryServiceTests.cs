using BlueTusk.ControlPlane;
using BlueTusk.Live;

namespace BlueTusk.ContinuousGraph.Tests;

public sealed class HostedContinuousGraphControlPlaneQueryServiceTests
{
    [Fact]
    public async Task Hosted_graph_projection_reports_registration_metadata_without_rows_or_parameters()
    {
        var registry = new ContinuousGraphQueryRegistry();
        Assert.True(registry.Register(new ContinuousGraphQueryDescriptor(
            "suspicious-transfers",
            "risk-primary",
            new string('a', 64),
            "payments",
            "risk",
            ["accounts", "transfers"],
            [
                new LiveTableDependency("risk", "accounts"),
                new LiveTableDependency("risk", "transfers"),
            ],
            100,
            LiveQueryCapabilities.TenantFilter |
            LiveQueryCapabilities.DeterministicOrdering |
            LiveQueryCapabilities.BoundedTake)));
        var service = new HostedContinuousGraphControlPlaneQueryService(
            registry,
            new FixedTimeProvider(
                new DateTimeOffset(2026, 8, 3, 18, 0, 0, TimeSpan.Zero)));

        var overview = await service.GetContinuousGraphOverviewAsync(
            TestContext.Current.CancellationToken);
        var query = Assert.Single(overview.Queries);

        Assert.Equal("suspicious-transfers", query.Name);
        Assert.Equal("risk.payments", $"{query.GraphSchema}.{query.GraphName}");
        Assert.Equal(["risk.accounts", "risk.transfers"], query.TableDependencies);
        Assert.Equal(100, query.MaximumResultCount);
        Assert.Contains("BoundedTake", query.Capabilities, StringComparison.Ordinal);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
