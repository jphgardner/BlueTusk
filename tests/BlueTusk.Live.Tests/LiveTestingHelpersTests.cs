using BlueTusk.Live.Testing;

namespace BlueTusk.Live.Tests;

public sealed class LiveTestingHelpersTests
{
    [Fact]
    public async Task In_memory_replay_store_passes_public_conformance()
    {
        var report = await LiveReplayStoreConformance.RunAsync(
            new InMemoryLiveReplayStore(),
            "memory",
            TestContext.Current.CancellationToken);

        Assert.Equal(7, report.Assertions);
    }

    [Fact]
    public async Task Invalidation_log_is_database_and_dependency_scoped()
    {
        var log = new InMemoryLiveInvalidationLog();
        var orders = new LiveTableDependency("sales", "orders");
        var cursor = log.Append("app", [orders]);
        _ = log.Append("other", [orders]);

        Assert.Equal(1, cursor.Value);
        Assert.True(await log.HasChangesAsync(
            "app",
            [orders],
            new LiveInvalidationCursor(0),
            cursor,
            TestContext.Current.CancellationToken));
        Assert.False(await log.HasChangesAsync(
            "app",
            [new LiveTableDependency("sales", "customers")],
            new LiveInvalidationCursor(0),
            cursor,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Replay_retention_expires_old_resume_positions()
    {
        var time = new ManualTimeProvider();
        var store = new InMemoryLiveReplayStore(TimeSpan.FromMinutes(1), time);
        var identity = new LiveSubscriptionIdentity(
            "database",
            new string('a', 64),
            new string('b', 64),
            "scope",
            "policy:v1",
            10);
        await store.AppendAsync(
            new LiveReplayAppendRequest(
                identity,
                0,
                [new LiveReplayEvent(
                    1,
                    LiveEventKind.InitialResult,
                    LiveReplayJsonSerializer.ContentType,
                    "{}"u8)]),
            TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(1, await store.PruneAsync(TestContext.Current.CancellationToken));
        var read = await store.ReadAsync(
            identity,
            0,
            10,
            TestContext.Current.CancellationToken);
        Assert.Equal(LiveReplayReadStatus.Expired, read.Status);
        Assert.Equal(2, read.FirstAvailableSequence);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan value) => _now += value;
    }
}
