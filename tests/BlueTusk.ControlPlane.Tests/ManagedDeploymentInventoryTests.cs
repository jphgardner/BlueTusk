using System.Globalization;
using System.Text.Json;

namespace BlueTusk.ControlPlane.Tests;

public sealed class ManagedDeploymentInventoryTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(1_000)]
    [InlineData(10_000)]
    public async Task Pages_filter_before_taking_rows_and_visit_each_matching_id_once(int count)
    {
        var store = new InMemoryManagedDeploymentStore();
        for (var index = count - 1; index >= 0; index--)
            await store.PutAsync(FleetInventoryTestStore.Spec(index), 0);
        var actual = new List<string>();
        string? after = null;
        do
        {
            var page = await store.QueryAsync(new ManagedDeploymentQuery
            {
                PageSize = 17,
                AfterDeploymentId = after,
                TenantId = "tenant-a",
                DeploymentIdPrefix = "deployment-",
                State = ManagedDeploymentState.Pending,
                WorkloadKind = ManagedWorkloadKind.Streams,
            });
            Assert.InRange(page.Deployments.Count, 1, 17);
            actual.AddRange(page.Deployments.Select(item => item.Spec.DeploymentId));
            if (page.NextAfterDeploymentId is not null)
                Assert.Equal(page.Deployments[^1].Spec.DeploymentId, page.NextAfterDeploymentId);
            after = page.NextAfterDeploymentId;
        } while (after is not null);
        Assert.Equal(Enumerable.Range(0, count).Where(index => index % 2 == 0)
            .Select(index => FleetInventoryTestStore.Spec(index).DeploymentId), actual);
        Assert.Empty((await store.QueryAsync(new ManagedDeploymentQuery { AfterDeploymentId = "zzzz" })).Deployments);
        Assert.Empty((await store.QueryAsync(new ManagedDeploymentQuery { Provider = "missing" })).Deployments);
        Assert.Empty((await store.QueryAsync(new ManagedDeploymentQuery { Region = "missing" })).Deployments);
    }

    [Fact]
    public async Task Direct_and_page_projections_do_not_enumerate_the_fleet_or_return_secret_material()
    {
        var store = new FleetInventoryTestStore();
        for (var index = 0; index < 100; index++) await store.PutAsync(FleetInventoryTestStore.Spec(index), 0);
        var queries = new ManagedDeploymentFleetQueryService(store);
        var page = await queries.GetDeploymentPageAsync(new ManagedDeploymentQuery { PageSize = 7, TenantId = "tenant-b" });
        Assert.Equal(7, page.Deployments.Count);
        Assert.NotNull(page.NextAfterDeploymentId);
        var direct = await queries.GetDeploymentAsync("deployment-000099");
        Assert.Equal("tenant-b", direct!.TenantId);
        Assert.Null(await queries.GetDeploymentAsync("missing"));
        Assert.Equal(1, store.PageReads);
        Assert.Equal(2, store.DirectReads);
        var json = JsonSerializer.Serialize(page) + JsonSerializer.Serialize(direct);
        Assert.DoesNotContain("private-setting", json, StringComparison.Ordinal);
        Assert.DoesNotContain("vault-secret-name", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prefix_is_literal_and_cursor_order_is_ordinal()
    {
        var store = new InMemoryManagedDeploymentStore();
        foreach (var id in new[] { "a%_\\one", "a%_\\two", "abc", "z", "ä" })
            await store.PutAsync(FleetInventoryTestStore.Spec(0) with { DeploymentId = id }, 0);
        var page = await store.QueryAsync(new ManagedDeploymentQuery { DeploymentIdPrefix = "a%_\\", PageSize = 1 });
        Assert.Equal("a%_\\one", Assert.Single(page.Deployments).Spec.DeploymentId);
        page = await store.QueryAsync(new ManagedDeploymentQuery
        {
            DeploymentIdPrefix = "a%_\\",
            PageSize = 1,
            AfterDeploymentId = page.NextAfterDeploymentId,
        });
        Assert.Equal("a%_\\two", Assert.Single(page.Deployments).Spec.DeploymentId);
        Assert.Null(page.NextAfterDeploymentId);
        Assert.Equal(["z", "ä"], (await store.QueryAsync(new ManagedDeploymentQuery { AfterDeploymentId = "abc" }))
            .Deployments.Select(item => item.Spec.DeploymentId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(501)]
    public async Task Page_size_is_bounded_before_store_work(int size)
    {
        var store = new InMemoryManagedDeploymentStore();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.QueryAsync(new ManagedDeploymentQuery { PageSize = size }).AsTask());
    }

    [Fact]
    public async Task Queries_validate_filters_and_honour_cancellation()
    {
        var store = new InMemoryManagedDeploymentStore();
        foreach (var query in new[]
                 {
                     new ManagedDeploymentQuery { TenantId = new string('x', 129) },
                     new ManagedDeploymentQuery { AfterDeploymentId = "\0" },
                     new ManagedDeploymentQuery { DeploymentIdPrefix = " " },
                     new ManagedDeploymentQuery { State = (ManagedDeploymentState)999 },
                     new ManagedDeploymentQuery { WorkloadKind = (ManagedWorkloadKind)999 },
                 })
            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.QueryAsync(query).AsTask());
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.QueryAsync(new ManagedDeploymentQuery(), caller.Token).AsTask());
    }
}

internal sealed class FleetInventoryTestStore : IManagedDeploymentStore
{
    private readonly InMemoryManagedDeploymentStore _inner = new();
    public int PageReads { get; private set; }
    public int DirectReads { get; private set; }

    public ValueTask<ManagedDeployment?> GetAsync(string deploymentId, CancellationToken cancellationToken = default)
    {
        DirectReads++;
        return _inner.GetAsync(deploymentId, cancellationToken);
    }
    public ValueTask<ManagedDeploymentPage> QueryAsync(ManagedDeploymentQuery query, CancellationToken cancellationToken = default)
    {
        PageReads++;
        return _inner.QueryAsync(query, cancellationToken);
    }
    public IAsyncEnumerable<ManagedDeployment> ListAsync(string? tenantId = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("A bounded inventory route must not enumerate the complete store.");
    public ValueTask<ManagedDeployment> PutAsync(ManagedDeploymentSpec spec, long expectedGeneration, CancellationToken cancellationToken = default) =>
        _inner.PutAsync(spec, expectedGeneration, cancellationToken);
    public ValueTask<ManagedDeployment> UpdateStatusAsync(string deploymentId, ManagedDeploymentStatus status, long expectedRevision,
        CancellationToken cancellationToken = default) => _inner.UpdateStatusAsync(deploymentId, status, expectedRevision, cancellationToken);

    public static ManagedDeploymentSpec Spec(int index) => new(
        "deployment-" + index.ToString("D6", CultureInfo.InvariantCulture), index % 2 == 0 ? "tenant-a" : "tenant-b",
        "test", "eu-west", 1, false, true,
        [new(ManagedWorkloadKind.Streams, "1.2.0", new(1, 500, 256 * 1024 * 1024, 1024 * 1024 * 1024),
            [new("vault", "vault-secret-name")], new Dictionary<string, string> { ["private-setting"] = "private-value" })],
        new Dictionary<string, string>());
}
