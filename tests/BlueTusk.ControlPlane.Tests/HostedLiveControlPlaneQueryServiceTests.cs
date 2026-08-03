using BlueTusk.Live;
using BlueTusk.Live.Testing;

namespace BlueTusk.ControlPlane.Tests;

public sealed class HostedLiveControlPlaneQueryServiceTests
{
    [Fact]
    public async Task Hosted_live_projection_reports_lag_fanout_and_redacts_security_scope()
    {
        var invalidations = new InMemoryLiveInvalidationLog();
        var currentRowId = 1;
        var plan = new LiveQueryPlan<Row, int>(
            "orders",
            "database",
            new string('a', 64),
            LiveQueryCapabilities.SingleTable |
                LiveQueryCapabilities.TenantFilter |
                LiveQueryCapabilities.DeterministicOrdering |
                LiveQueryCapabilities.BoundedTake,
            [new LiveTableDependency("sales", "orders")],
            [],
            10,
            (_, _) => ValueTask.FromResult<IReadOnlyList<Row>>([new Row(currentRowId)]),
            static row => row.Id);
        var session = new LiveQuerySession<Row, int>(
            plan,
            LiveQueryArguments.Create([], new Dictionary<string, object?>()),
            new LiveSecurityScope("tenant:sensitive-customer", "policy:v1"),
            invalidations);
        await using var shared = new LiveSharedSubscription<Row, int>(
            session,
            new InMemoryLiveReplayStore());
        await shared.StartAsync(TestContext.Current.CancellationToken);
        var connected = await shared.ConnectAsync(0, TestContext.Current.CancellationToken);
        currentRowId = 2;
        _ = invalidations.Append("database", [new LiveTableDependency("sales", "orders")]);
        _ = await shared.RefreshAsync(TestContext.Current.CancellationToken);
        _ = invalidations.Append("database", [new LiveTableDependency("sales", "orders")]);
        await using var registry = new LiveSharedSubscriptionRegistry();
        _ = registry.GetOrAdd(shared);
        var service = new HostedLiveControlPlaneQueryService(registry, invalidations);

        var overview = await service.GetLiveOverviewAsync(TestContext.Current.CancellationToken);
        var subscription = Assert.Single(overview.Subscriptions);

        Assert.Equal(1, subscription.SubscriberCount);
        Assert.Equal(1, subscription.InvalidationLag);
        Assert.Equal(2d / 3d, subscription.FanOutRatio, precision: 6);
        Assert.StartsWith("tenant:#", subscription.SecurityScopeLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive", subscription.SecurityScopeLabel, StringComparison.Ordinal);
        await connected.Connection!.DisposeAsync();
    }

    private sealed record Row(int Id);
}
