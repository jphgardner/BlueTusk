using BlueTusk.Live;
using BlueTusk.Projections.Live;
using BlueTusk.Streams;

namespace BlueTusk.Projections.Tests;

public sealed class ProjectionLiveOwnershipTests
{
    [Fact]
    public async Task IndependentNodesAcquireOneWriterFenceStaleSubscribersAndResumeReplacementResetWithoutGap()
    {
        await using var db = await ProjectionDatabase.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        var (lease, definition) = await db.ReadyAsync();
        await db.Store.PromoteAsync(lease, new(100), null, token);
        var query = new ProjectionLiveQuery<OrderView>(db.Store, "orders", "first", new("tenant:first", "v1"), "orders", "db", "owned-v1", ProjectionJson.Default.OrderView);
        await using var identitySession = query.CreateSession();
        var identity = identitySession.Identity;
        var options = new PostgreSqlProjectionLiveReplayOptions { Schema = db.Schema };
        var firstNode = new PostgreSqlProjectionLiveReplayStore(db.DataSource, options);
        var secondNode = new PostgreSqlProjectionLiveReplayStore(db.DataSource, options);
        await firstNode.InitializeAsync(token);
        await secondNode.InitializeAsync(token);
        var acquired = await Task.WhenAll(
            firstNode.AcquireAsync(identity, "node-a", TimeSpan.FromMinutes(1), token).AsTask(),
            secondNode.AcquireAsync(identity, "node-b", TimeSpan.FromMinutes(1), token).AsTask());
        var owner = Assert.Single(acquired.OfType<ProjectionLivePublisher>());
        Assert.Single(acquired, static value => value is null);
        await using var first = new ProjectionLiveSubscription<OrderView>(query, owner, ProjectionLiveJson.EventTypeInfo);
        await first.StartAsync(token);
        var connected = await first.ConnectAsync(0, token);
        Assert.Equal(1, Assert.Single(connected.Connection!.Replay).Sequence);
        await using var oldMessages = connected.Connection.ReadAllAsync(token).GetAsyncEnumerator(token);
        var waiting = oldMessages.MoveNextAsync().AsTask();
        var protector = new LiveResumeTokenProtector([new("key", new byte[32], true)]);
        var resume = protector.Protect(first.Identity, 1, TimeSpan.FromMinutes(1));
        await SqlAsync(db, $"UPDATE \"{db.Schema}\".projection_live_replay SET expires_at=clock_timestamp()-interval '1 second'", token);
        var replacement = Assert.IsType<ProjectionLivePublisher>(await secondNode.AcquireAsync(identity, "replacement", TimeSpan.FromMinutes(1), token));
        Assert.True(replacement.Lease.FencingToken > owner.Lease.FencingToken);
        await Assert.ThrowsAsync<ProjectionLivePublisherFencedException>(async () => await first.RefreshAsync(token));
        Assert.False(await waiting.WaitAsync(token));
        await Assert.ThrowsAsync<ProjectionLivePublisherFencedException>(async () => await owner.AppendAsync(new(identity, 1, [new(2, LiveEventKind.ResultReset, "json", "stale"u8)]), token));
        await using var second = new ProjectionLiveSubscription<OrderView>(query, replacement, ProjectionLiveJson.EventTypeInfo);
        await second.StartAsync(token);
        var reset = await second.ConnectWithTokenAsync(resume, protector, token);
        var restored = Assert.Single(reset.Connection!.Replay);
        Assert.Equal(2, restored.Sequence);
        Assert.Equal("ServerRestart", ProjectionLiveTests.Initial(restored).GetProperty("resetReason").GetString());
        await reset.Connection.DisposeAsync();
        await using var change = db.Delivery(101, id => new UpdateChange(id, ProjectionDatabase.Customer("customer", "first", "Alice"), ProjectionDatabase.Customer("customer", "first", "Bob"), new ChangedColumnSet(true, [2])));
        await db.Store.ApplyAsync(lease, definition, change.Transaction, token);
        Assert.Equal(1, await second.RefreshAsync(token));
        var recovered = await second.ConnectWithTokenAsync(resume, protector, token);
        Assert.Equal([2L, 3L], recovered.Connection!.Replay.Select(static item => item.Sequence));
        Assert.Equal(LiveEventKind.RowUpdated, recovered.Connection.Replay[^1].Kind);
        await recovered.Connection.DisposeAsync();
        // Disposing the stale node cannot release the replacement's fencing token.
        await first.DisposeAsync();
        await replacement.EnsureActiveAsync(token);
        Assert.Null(await firstNode.AcquireAsync(identity, "third", TimeSpan.FromMinutes(1), token));
        await connected.Connection.DisposeAsync();
    }

    [Fact]
    public async Task OwnedAppendRetriesAreExactBoundedReadsRejectReplayOverflowAndPruningKeepsSequenceAndFenceTombstone()
    {
        using var listener = new System.Diagnostics.Metrics.MeterListener
        { InstrumentPublished = static (instrument,l) => { if(instrument.Meter.Name=="BlueTusk.Projections.Live") { l.EnableMeasurementEvents(instrument); } } };
        listener.SetMeasurementEventCallback<long>(static (_,_,_,_)=>throw new InvalidOperationException("Faulty owned Live observer."));
        listener.Start();
        await using var db = await ProjectionDatabase.CreateAsync();
        var identity = new LiveSubscriptionIdentity("db", new string('a',64), new string('b',64), "tenant:first", "v1", 10);
        var store = new PostgreSqlProjectionLiveReplayStore(db.DataSource, new()
        { Schema = db.Schema, MaximumAppendEvents = 8, MaximumEventBytes = 128, MaximumBatchBytes = 512, MaximumReadEvents = 16, PruneBatchRows = 3 });
        await store.InitializeAsync();
        await using var owner = Assert.IsType<ProjectionLivePublisher>(await store.AcquireAsync(identity, "writer", TimeSpan.FromMinutes(1)));
        LiveReplayEvent[] Events(int first, int count) => Enumerable.Range(first,count).Select(sequence => new LiveReplayEvent(sequence, LiveEventKind.RowAdded, "json", new byte[128])).ToArray();
        await Assert.ThrowsAsync<ProjectionBoundExceededException>(async () => await owner.AppendAsync(new(identity,0,Events(1,8))));
        var batch = new LiveReplayAppendRequest(identity,0,Events(1,4));
        Assert.Equal(LiveReplayAppendStatus.Stored,(await owner.AppendAsync(batch)).Status);
        Assert.Equal(LiveReplayAppendStatus.AlreadyStored,(await owner.AppendAsync(batch)).Status);
        Assert.Equal(LiveReplayAppendStatus.SequenceConflict,(await owner.AppendAsync(new(identity,0,[new(1,LiveEventKind.RowAdded,"json","changed"u8)]))).Status);
        await owner.AppendAsync(new(identity,4,Events(5,1)));
        var first = await store.ReadAsync(identity,0,16);
        Assert.Equal([1L,2L,3L,4L], first.Events.Select(static item => item.Sequence));
        Assert.Equal(5,first.LastSequence);
        Assert.Equal(5,Assert.Single((await store.ReadAsync(identity,4,16)).Events).Sequence);
        await SqlAsync(db,$"UPDATE \"{db.Schema}\".projection_live_events SET recorded_at=clock_timestamp()-interval '2 hours'",CancellationToken.None);
        Assert.Equal(3,await store.PruneAsync());
        Assert.Equal(LiveReplayReadStatus.Expired,(await store.ReadAsync(identity,0,16)).Status);
        Assert.Equal([4L,5L],(await store.ReadAsync(identity,3,16)).Events.Select(static item=>item.Sequence));
        Assert.Equal(2,await store.PruneAsync());
        Assert.Equal(0,await store.PruneAsync());
        await owner.DisposeAsync();
        await using var resumed = Assert.IsType<ProjectionLivePublisher>(await store.AcquireAsync(identity,"replacement",TimeSpan.FromMinutes(1)));
        Assert.True(resumed.Lease.FencingToken>owner.Lease.FencingToken);
        Assert.Equal(5,(await store.ReadAsync(identity,5,16)).LastSequence);
        Assert.Equal(LiveReplayAppendStatus.Stored,(await resumed.AppendAsync(new(identity,5,Events(6,1)))).Status);
        Assert.Equal(6,Assert.Single((await store.ReadAsync(identity,5,16)).Events).Sequence);
    }

    [Fact]
    public async Task ExpiryInsideAppendRollsBackReplayAndAllowsReplacementAtUnchangedHead()
    {
        await using var db = await ProjectionDatabase.CreateAsync();
        var identity = new LiveSubscriptionIdentity("db",new string('a',64),new string('b',64),"tenant:first","v1",10);
        var store = new PostgreSqlProjectionLiveReplayStore(db.DataSource,new(){Schema=db.Schema});
        await store.InitializeAsync();
        await SqlAsync(db,$"""
            CREATE FUNCTION "{db.Schema}".delay_append() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_sleep(0.25); RETURN NEW; END $$;
            CREATE TRIGGER delay_append BEFORE INSERT ON "{db.Schema}".projection_live_events FOR EACH ROW EXECUTE FUNCTION "{db.Schema}".delay_append()
            """,CancellationToken.None);
        await using var owner=Assert.IsType<ProjectionLivePublisher>(await store.AcquireAsync(identity,"slow",TimeSpan.FromMilliseconds(150)));
        await Assert.ThrowsAsync<ProjectionLivePublisherFencedException>(async()=>await owner.AppendAsync(new(identity,0,[new(1,LiveEventKind.InitialResult,"json","data"u8)])));
        Assert.Equal(0,(await store.ReadAsync(identity,0,10)).LastSequence);
        await using var replacement=Assert.IsType<ProjectionLivePublisher>(await store.AcquireAsync(identity,"replacement",TimeSpan.FromMinutes(1)));
        Assert.Equal(LiveReplayAppendStatus.Stored,(await replacement.AppendAsync(new(identity,0,[new(1,LiveEventKind.InitialResult,"json","data"u8)]))).Status);
        Assert.Equal(1,(await store.ReadAsync(identity,0,10)).LastSequence);
    }

    [Fact]
    public async Task RetentionOnlyPrunesExpiredPrefixAndChangedReaderBoundsCannotSilentlySkipNextEvent()
    {
        await using var db = await ProjectionDatabase.CreateAsync();
        var identity = new LiveSubscriptionIdentity("db",new string('a',64),new string('b',64),"tenant:first","v1",10);
        var store = new PostgreSqlProjectionLiveReplayStore(db.DataSource,new(){Schema=db.Schema,MaximumEventBytes=512,MaximumBatchBytes=512});
        await store.InitializeAsync();
        await using var owner=Assert.IsType<ProjectionLivePublisher>(await store.AcquireAsync(identity,"owner",TimeSpan.FromMinutes(1)));
        await owner.AppendAsync(new(identity,0,[new(1,LiveEventKind.InitialResult,"json",new byte[256]),new(2,LiveEventKind.RowUpdated,"json",new byte[128])]));
        await SqlAsync(db,$"UPDATE \"{db.Schema}\".projection_live_events SET recorded_at=clock_timestamp()-interval '2 hours' WHERE sequence=2",CancellationToken.None);
        Assert.Equal(0,await store.PruneAsync());
        Assert.Equal([1L,2L],(await store.ReadAsync(identity,0,10)).Events.Select(static item=>item.Sequence));
        var wrongBounds=new PostgreSqlProjectionLiveReplayStore(db.DataSource,new(){Schema=db.Schema,MaximumEventBytes=128,MaximumBatchBytes=128});
        await Assert.ThrowsAsync<ProjectionBoundExceededException>(async()=>await wrongBounds.ReadAsync(identity,0,10));
        await SqlAsync(db,$"UPDATE \"{db.Schema}\".projection_live_events SET recorded_at=clock_timestamp()-interval '2 hours' WHERE sequence=1",CancellationToken.None);
        Assert.Equal(2,await store.PruneAsync());
        Assert.Equal(LiveReplayReadStatus.Expired,(await store.ReadAsync(identity,0,10)).Status);
        Assert.Equal(LiveReplayReadStatus.Current,(await store.ReadAsync(identity,2,10)).Status);
    }

    private static async Task SqlAsync(ProjectionDatabase db,string sql,CancellationToken token)
    { await using var connection=await db.DataSource.OpenConnectionAsync(token); await using var command=connection.CreateCommand(); command.CommandText=sql; await command.ExecuteNonQueryAsync(token); }
}
