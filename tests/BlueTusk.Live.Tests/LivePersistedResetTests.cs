using System.Text.Json;
using System.Text.Json.Serialization;
using BlueTusk.Live.Testing;

namespace BlueTusk.Live.Tests;

public sealed class LivePersistedResetTests
{
    [Fact]
    public async Task ServerRestartAppendsAuthoritativeResetAfterDurableHeadAndOldTokensCanReplayIt()
    {
        var replay = new InMemoryLiveReplayStore();
        await using (var initial = Create(replay, static () => [new(1, "before")])) { await initial.StartAsync(); }
        await using var restarted = Create(replay, static () => [new(2, "after restart")]);
        await restarted.StartAsync();
        Assert.Equal(2, restarted.Status.PersistedSequence);
        var connected = await restarted.ConnectAsync(1);
        var reset = Assert.Single(connected.Connection!.Replay);
        Assert.Equal(LiveEventKind.ResultReset, reset.Kind);
        using var json = JsonDocument.Parse(reset.Payload);
        Assert.Equal("ServerRestart", json.RootElement.GetProperty("resetReason").GetString());
        Assert.Equal("after restart", json.RootElement.GetProperty("rows")[0].GetProperty("Value").GetString());
        await connected.Connection.DisposeAsync();
    }

    [Fact]
    public async Task SourceGeneratedReplayPersistsAuthoritativeResetAndResumesInSequence()
    {
        var replay = new InMemoryLiveReplayStore();
        IReadOnlyList<PersistedResetRow> rows = [new(1, "before")];
        await using var shared = Create(replay, () => rows);
        await shared.StartAsync();
        rows = [new(2, "after")];
        Assert.Equal(1, await shared.ResetAsync(LiveResetReason.SchemaChanged));
        var connected = await shared.ConnectAsync(1);
        var reset = Assert.Single(connected.Connection!.Replay);
        Assert.Equal(2, reset.Sequence);
        Assert.Equal(LiveEventKind.ResultReset, reset.Kind);
        Assert.True(LiveReplayJsonSerializer.VerifyIntegrity(reset));
        using var json = JsonDocument.Parse(reset.Payload);
        Assert.Equal("SchemaChanged", json.RootElement.GetProperty("resetReason").GetString());
        Assert.Equal("after", json.RootElement.GetProperty("rows")[0].GetProperty("Value").GetString());
        await connected.Connection.DisposeAsync();
    }

    [Fact]
    public async Task FailedResetReplayAppendRecoversExactProposalBeforeReconnectFanout()
    {
        var replay = new FailingResetReplay();
        IReadOnlyList<PersistedResetRow> rows = [new(1, "before")];
        await using var shared = Create(replay, () => rows);
        await shared.StartAsync();
        rows = [new(2, "after")];
        replay.FailNext = true;
        await Assert.ThrowsAsync<IOException>(async () => await shared.ResetAsync(LiveResetReason.SchemaChanged));
        Assert.Equal(1, shared.Status.PersistedSequence);
        rows = [new(3, "later")];
        var connected = await shared.ConnectAsync(1);
        var reset = Assert.Single(connected.Connection!.Replay);
        using var json = JsonDocument.Parse(reset.Payload);
        Assert.Equal("after", json.RootElement.GetProperty("rows")[0].GetProperty("Value").GetString());
        Assert.Equal(2, shared.Status.PersistedSequence);
        await connected.Connection.DisposeAsync();
    }

    private static LiveSharedSubscription<PersistedResetRow, int> Create(ILiveReplayStore replay, Func<IReadOnlyList<PersistedResetRow>> rows)
    {
        var plan = new LiveQueryPlan<PersistedResetRow, int>("reset", "db", new string('a', 64), LiveQueryCapabilities.BoundedTake,
            [new("public", "rows")], [], 10, (_, _) => ValueTask.FromResult(rows()), static row => row.Id);
        var session = new LiveQuerySession<PersistedResetRow, int>(plan, plan.Bind(new Dictionary<string, object?>()),
            new("tenant:a", "v1"), new InMemoryLiveInvalidationLog());
        var metadata = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<LiveResultEvent<PersistedResetRow, int>>)
            PersistedResetJson.Default.GetTypeInfo(typeof(LiveResultEvent<PersistedResetRow, int>))!;
        return LiveSharedSubscriptions.CreateWithJsonMetadata(session, replay, metadata);
    }

    private sealed class FailingResetReplay : ILiveReplayStore
    {
        private readonly InMemoryLiveReplayStore _inner = new();
        internal bool FailNext { get; set; }
        public ValueTask<LiveReplayAppendResult> AppendAsync(LiveReplayAppendRequest request, CancellationToken cancellationToken = default)
        {
            if (FailNext) { FailNext = false; throw new IOException("Intentional replay append outage."); }
            return _inner.AppendAsync(request, cancellationToken);
        }
        public ValueTask<LiveReplayReadResult> ReadAsync(LiveSubscriptionIdentity identity, long afterSequence, int maximumEvents, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(identity, afterSequence, maximumEvents, cancellationToken);
        public ValueTask<int> PruneAsync(CancellationToken cancellationToken = default) => _inner.PruneAsync(cancellationToken);
    }
}

public sealed record PersistedResetRow(int Id, string Value);
[JsonSerializable(typeof(LiveResultEvent<PersistedResetRow, int>))]
internal sealed partial class PersistedResetJson : JsonSerializerContext;
