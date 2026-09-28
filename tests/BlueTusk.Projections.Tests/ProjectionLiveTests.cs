using System.Text.Json;
using System.Text.Json.Serialization;
using BlueTusk.Live;
using BlueTusk.Live.DependencyInjection;
using BlueTusk.Live.Testing;
using BlueTusk.Projections.Live;
using BlueTusk.Streams;

namespace BlueTusk.Projections.Tests;

public sealed class ProjectionLiveTests
{
    [Fact]
    public async Task PublishedPageBoundsBytesAndCountAndRejectsStaleContinuation()
    {
        await using var db = await ProjectionDatabase.CreateAsync();
        var (lease, definition) = await db.ReadyAsync(orderCount: 3);
        await db.Store.PromoteAsync(lease, new(100), null);
        var page = await db.Store.ReadActivePageAsync("orders", "first", 1);
        Assert.Equal("1", Assert.Single(page.Documents).Key);
        var second = await db.Store.ReadActivePageAsync("orders", "first", 1, after: page.ContinueAfter);
        Assert.Equal("2", Assert.Single(second.Documents).Key);
        var bytes = page.Documents[0].Payload.Length;
        var bytePage = await db.Store.ReadActivePageAsync("orders", "first", 3, bytes);
        Assert.Single(bytePage.Documents);
        Assert.NotNull(bytePage.ContinueAfter);
        await Assert.ThrowsAsync<ProjectionBoundExceededException>(async () => await db.Store.ReadActivePageAsync("orders", "first", 3, bytes - 1));
        Assert.Empty((await db.Store.ReadActivePageAsync("orders", "another")).Documents);
        var range = await db.Store.ReadActivePageAsync("orders", "first", 3, afterKey: "1", throughKey: "2");
        Assert.Equal("2", Assert.Single(range.Documents).Key);
        await using var change = db.Delivery(101, id => new UpdateChange(id, ProjectionDatabase.Customer("customer", "first", "Alice"), ProjectionDatabase.Customer("customer", "first", "Bob"), new ChangedColumnSet(true, [2])));
        await db.Store.ApplyAsync(lease, definition, change.Transaction);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Store.ReadActivePageAsync("orders", "first", 1, after: page.ContinueAfter));
    }

    [Fact]
    public async Task AuthoritativeJoinLiveUpdatesDeletesFiltersAndResumesPersistedReplayWithoutGap()
    {
        await using var db = await ProjectionDatabase.CreateAsync();
        var (lease, definition) = await db.ReadyAsync(orderCount: 2);
        await db.Store.PromoteAsync(lease, new(100), null);
        var replay = new PostgreSqlLiveInvalidationStore(new PostgreSqlLiveStoreOptions { ControlDataSource = db.DataSource, ControlSchema = db.Schema });
        await using var live = Subscription(db, "first", replay, predicate: static value => value.Amount >= 20m);
        await live.StartAsync();
        var initial = await live.ConnectAsync(0);
        Assert.Equal(LiveEventKind.InitialResult, Assert.Single(initial.Connection!.Replay).Kind);
        Assert.Equal("2", Initial(initial.Connection.Replay[0]).GetProperty("rows")[0].GetProperty("Key").GetString());
        var protector = new LiveResumeTokenProtector([new LiveResumeTokenKey("k", new byte[32], true)]);
        var token = protector.Protect(live.Identity, live.Status.PersistedSequence, TimeSpan.FromMinutes(1));
        await initial.Connection.DisposeAsync();
        await using var update = db.Delivery(101,
            id => new UpdateChange(id, ProjectionDatabase.Customer("customer", "first", "Alice"), ProjectionDatabase.Customer("customer", "first", "Bob"), new ChangedColumnSet(true, [2])),
            id => new UpdateChange(id, ProjectionDatabase.Order("1", "first", "customer", 10m), ProjectionDatabase.Order("1", "first", "customer", 30m), new ChangedColumnSet(true, [3])),
            id => new InsertChange(id, ProjectionDatabase.Order("3", "another", "customer", 100m)));
        await db.Store.ApplyAsync(lease, definition, update.Transaction);
        // Reconnect itself catches up any committed materialized revision before opening fan-out.
        var resumed = await live.ConnectWithTokenAsync(token, protector);
        Assert.Equal(LiveSubscriptionConnectStatus.Connected, resumed.Status);
        Assert.Contains(resumed.Connection!.Replay, value => value.Kind == LiveEventKind.RowUpdated);
        Assert.Contains(resumed.Connection.Replay, value => value.Kind == LiveEventKind.RowAdded);
        Assert.DoesNotContain(resumed.Connection.Replay, value => value.Payload.Span.IndexOf("another"u8) >= 0);
        var sequences = resumed.Connection.Replay.Select(static value => value.Sequence).ToArray();
        Assert.Equal(Enumerable.Range(2, sequences.Length).Select(static value => (long)value), sequences);
        await resumed.Connection.DisposeAsync();
        var after = live.Status.PersistedSequence;
        await using var deleted = db.Delivery(102, id => new DeleteChange(id, ProjectionDatabase.Order("2", "first", "customer", 20m)));
        await db.Store.ApplyAsync(lease, definition, deleted.Transaction);
        Assert.True(await live.RefreshAsync() > 0);
        var reconnect = await live.ConnectAsync(after);
        Assert.Contains(reconnect.Connection!.Replay, value => value.Kind == LiveEventKind.RowRemoved);
        await reconnect.Connection.DisposeAsync();
        Assert.Equal(30m, (await db.Store.ReadActiveAggregateAsync("orders", "first", "all", "total")).Value);
        Assert.Equal(100m, (await db.Store.ReadActiveAggregateAsync("orders", "another", "all", "total")).Value);
        await using var other = Subscription(db, "another", replay);
        await other.StartAsync();
        Assert.Equal(LiveSubscriptionConnectStatus.InvalidResumeToken, (await other.ConnectWithTokenAsync(token, protector)).Status);
    }

    [Fact]
    public async Task UnpublishedRebuildDoesNotInvalidateButCutoverPersistsResetIncludingEmptyFilteredResult()
    {
        await using var db = await ProjectionDatabase.CreateAsync();
        var (lease, _) = await db.ReadyAsync();
        await db.Store.PromoteAsync(lease, new(100), null);
        var replay = new InMemoryLiveReplayStore();
        await using var live = Subscription(db, "first", replay, predicate: static _ => false);
        await live.StartAsync();
        var before = await db.Store.ReadPublicationAsync("orders");
        var (next, _) = await db.ReadyAsync(2);
        Assert.Equal(before, await db.Store.ReadPublicationAsync("orders"));
        Assert.Equal(0, await live.RefreshAsync());
        await db.Store.PromoteAsync(next, new(100), 1);
        Assert.Equal(before.Revision + 1, (await db.Store.ReadPublicationAsync("orders")).Revision);
        Assert.Equal(1, await live.RefreshAsync());
        var connect = await live.ConnectAsync(1);
        var reset = Assert.Single(connect.Connection!.Replay);
        Assert.Equal(LiveEventKind.ResultReset, reset.Kind);
        Assert.Equal("SchemaChanged", Initial(reset).GetProperty("resetReason").GetString());
        Assert.Equal(0, Initial(reset).GetProperty("rows").GetArrayLength());
        await connect.Connection.DisposeAsync();
    }

    [Fact]
    public async Task FailedWritesAndDuplicateReplayDoNotAdvancePublicationAndQueryEnforcesSecurityScope()
    {
        await using var db = await ProjectionDatabase.CreateAsync();
        var (lease, definition) = await db.ReadyAsync();
        await db.Store.PromoteAsync(lease, new(100), null);
        var before = await db.Store.ReadPublicationAsync("orders");
        await using var delivery = db.Delivery(101, id => new UpdateChange(id, ProjectionDatabase.Customer("customer", "first", "Alice"), ProjectionDatabase.Customer("customer", "first", "Bob"), new ChangedColumnSet(true, [2])));
        definition.FailAfterWrites = true;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Store.ApplyAsync(lease, definition, delivery.Transaction));
        Assert.Equal(before, await db.Store.ReadPublicationAsync("orders"));
        definition.FailAfterWrites = false;
        await db.Store.ApplyAsync(lease, definition, delivery.Transaction);
        var after = await db.Store.ReadPublicationAsync("orders");
        await db.Store.ApplyAsync(lease, definition, delivery.Transaction);
        Assert.Equal(after, await db.Store.ReadPublicationAsync("orders"));
        var query = Query(db, "first");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await query.Plan.ExecuteAsync(new(query.Plan.Bind(new Dictionary<string, object?>()), new("tenant:another", "v1")), default));
        await Assert.ThrowsAsync<ArgumentException>(async () => await query.InvalidationLog.GetCurrentCursorAsync("wrong-database"));
    }

    internal static ProjectionLiveQuery<OrderView> Query(ProjectionDatabase db, string tenant, Func<OrderView, bool>? predicate = null) =>
        new(db.Store, "orders", tenant, new("tenant:" + tenant, "v1"), "orders", "test-db", "query-v1", ProjectionJson.Default.OrderView, predicate: predicate);

    internal static ProjectionLiveSubscription<OrderView> Subscription(ProjectionDatabase db, string tenant, ILiveReplayStore replay, Func<OrderView, bool>? predicate = null) =>
        new(Query(db, tenant, predicate), replay, ProjectionLiveJson.EventTypeInfo);

    internal static JsonElement Initial(LiveReplayEvent value)
    {
        using var document = JsonDocument.Parse(value.Payload);
        return document.RootElement.Clone();
    }
}

[JsonSerializable(typeof(LiveResultEvent<ProjectionLiveRow<OrderView>, string>))]
internal sealed partial class ProjectionLiveJson : JsonSerializerContext
{
    internal static System.Text.Json.Serialization.Metadata.JsonTypeInfo<LiveResultEvent<ProjectionLiveRow<OrderView>, string>> EventTypeInfo =>
        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<LiveResultEvent<ProjectionLiveRow<OrderView>, string>>)Default.GetTypeInfo(typeof(LiveResultEvent<ProjectionLiveRow<OrderView>, string>))!;
}
