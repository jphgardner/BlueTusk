using BlueTusk.Live;
using BlueTusk.Live.Testing;
using BlueTusk.Projections.Live;
using BlueTusk.Replication;
using BlueTusk.Streams;
using BlueTusk.TypeSystem;

namespace BlueTusk.Projections.Tests;

public sealed class ProjectionRebuildIntegrationTests
{
    [Fact]
    public async Task TwoRealSnapshotSlotsShareVerifiedLineageCatchUpToSourceBarrierAndPublishLiveResetWithoutGap()
    {
        await using var db = await ProjectionDatabase.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        var publication = "proj_rebuild_pub_" + Guid.NewGuid().ToString("N");
        var activeSlot = "proj_active_" + Guid.NewGuid().ToString("N");
        var candidateSlot = "proj_rebuild_" + Guid.NewGuid().ToString("N");
        await SqlAsync(db, $"""
            CREATE TABLE "{db.Schema}".customers(id text NOT NULL, tenant text NOT NULL, name text NOT NULL, PRIMARY KEY(tenant,id));
            CREATE TABLE "{db.Schema}".orders(id text NOT NULL, tenant text NOT NULL, customer text NOT NULL, amount text NOT NULL, PRIMARY KEY(tenant,id));
            ALTER TABLE "{db.Schema}".customers REPLICA IDENTITY FULL;
            ALTER TABLE "{db.Schema}".orders REPLICA IDENTITY FULL;
            INSERT INTO "{db.Schema}".customers VALUES('customer','first','Alice');
            INSERT INTO "{db.Schema}".orders VALUES('1','first','customer','10');
            CREATE PUBLICATION "{publication}" FOR TABLE "{db.Schema}".customers,"{db.Schema}".orders
            """, token);
        try
        {
            BlueTuskReplicationSystemIdentity system;
            await using (var identify = await BlueTuskLogicalReplicationConnection.OpenAsync(db.DataSource.CreateDedicatedSessionOptions(), token))
            {
                system = await identify.IdentifySystemAsync(token);
            }
            var activeSource = new ChangeSourceIdentity(system.SystemIdentifier, system.DatabaseName!, activeSlot, publication);
            var candidateSource = new ChangeSourceIdentity(system.SystemIdentifier, system.DatabaseName!, candidateSlot, publication);
            var activeEvidence = await PostgreSqlProjectionLineage.CaptureAsync(db.DataSource, activeSource, [publication], token);
            var candidateEvidence = await PostgreSqlProjectionLineage.CaptureAsync(db.DataSource, candidateSource, [publication], token);
            Assert.Equal(activeEvidence.Fingerprint, candidateEvidence.Fingerprint);
            Assert.Equal(system.Timeline, candidateEvidence.Timeline);
            Assert.NotEqual(activeEvidence.Source, candidateEvidence.Source);
            var activeIdentity = new ProjectionIdentity("orders", 1, "joined-v1", activeSource);
            var candidateIdentity = new ProjectionIdentity("orders", 2, "joined-v2", candidateSource);
            await db.Store.RegisterWithLineageAsync(activeIdentity, activeEvidence, token);
            await db.Store.RegisterWithLineageAsync(candidateIdentity, candidateEvidence, token);
            var active = Assert.IsType<ProjectionLease>(await db.Store.AcquireAsync(activeIdentity, "a", TimeSpan.FromMinutes(1), token));
            var candidate = Assert.IsType<ProjectionLease>(await db.Store.AcquireAsync(candidateIdentity, "b", TimeSpan.FromMinutes(1), token));
            var activeConsumer = new StreamsProjectionConsumer(db.Store, active, new OrdersProjection(activeIdentity));
            var candidateConsumer = new StreamsProjectionConsumer(db.Store, candidate, new OrdersProjection(candidateIdentity));
            await using var activeAttempt = await Snapshot(db, activeEvidence, publication).BeginAttemptAsync(null, token);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await activeConsumer.StartSnapshotAsync(new(activeAttempt.Epoch, 1), token));
            await BuildAsync(activeAttempt, activeConsumer, token);
            await db.Store.PromoteAsync(active, activeAttempt.Epoch.ConsistentPosition, null, token);
            var query = new ProjectionLiveQuery<OrderView>(db.Store, "orders", "first", new("tenant:first", "v1"), "orders", "db", "v1", ProjectionJson.Default.OrderView);
            await using var live = new ProjectionLiveSubscription<OrderView>(query, new InMemoryLiveReplayStore(), ProjectionLiveJson.EventTypeInfo);
            await live.StartAsync(token);
            var protector = new LiveResumeTokenProtector([new("key", new byte[32], true)]);
            var resume = protector.Protect(live.Identity, 1, TimeSpan.FromMinutes(1));
            await using var candidateAttempt = await Snapshot(db, candidateEvidence, publication).BeginAttemptAsync(null, token);
            await candidateConsumer.StartSnapshotAsync(new(candidateAttempt.Epoch, 2), token);
            var unexpected = new ChangeTable(999, db.Schema, "unexpected", 'f', [new(0, "id", 25, -1, true)]);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await candidateConsumer.ConsumeSnapshotBatchAsync(new(candidateAttempt.Epoch, unexpected, 0, [], true), token));
            // A source commit races the candidate's exported snapshot. Its new order and customer
            // name must arrive together through both independently retained Streams WAL histories.
            await SqlAsync(db, $"""
                BEGIN; UPDATE "{db.Schema}".customers SET name='Bob' WHERE tenant='first';
                INSERT INTO "{db.Schema}".orders VALUES('2','first','customer','20'),('private','another','customer','999'); COMMIT;
                """, token);
            await BuildAsync(candidateAttempt, candidateConsumer, token);
            var cutoverEvidence = await PostgreSqlProjectionLineage.CaptureForCutoverAsync(db.DataSource, candidateSource, [publication], token);
            var barrier = cutoverEvidence.BarrierPosition;
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Store.PromoteAsync(candidate, barrier, 1, token));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Store.PromoteWithLineageAsync(candidate, BlueTuskLogSequenceNumber.Zero, 1, cutoverEvidence, token));
            await using var activeWal = activeAttempt.CreateChangeStream().ReadTransactionsAsync(token).GetAsyncEnumerator(token);
            await using var candidateWal = candidateAttempt.CreateChangeStream().ReadTransactionsAsync(token).GetAsyncEnumerator(token);
            await CatchUpAsync(activeWal, activeConsumer, barrier, token);
            Assert.Equal("Bob", (await db.ReadAsync())!.CustomerName);
            var published = await db.Store.ReadPublicationAsync("orders", token);
            await CatchUpAsync(candidateWal, candidateConsumer, barrier, token);
            Assert.Equal(published, await db.Store.ReadPublicationAsync("orders", token));
            await db.Store.PromoteWithLineageAsync(candidate, barrier, 1, cutoverEvidence, token);
            var reconnected = await live.ConnectWithTokenAsync(resume, protector, token);
            Assert.Equal(LiveSubscriptionConnectStatus.Connected, reconnected.Status);
            var reset = Assert.Single(reconnected.Connection!.Replay);
            Assert.Equal(LiveEventKind.ResultReset, reset.Kind);
            var body = ProjectionLiveTests.Initial(reset);
            Assert.Equal("SchemaChanged", body.GetProperty("resetReason").GetString());
            Assert.Equal(2, body.GetProperty("rows").GetArrayLength());
            Assert.All(body.GetProperty("rows").EnumerateArray(), row => Assert.Equal(2, row.GetProperty("PublishedVersion").GetInt32()));
            Assert.All(body.GetProperty("rows").EnumerateArray(), row => Assert.Equal("Bob", row.GetProperty("Value").GetProperty("CustomerName").GetString()));
            await reconnected.Connection.DisposeAsync();
            Assert.Equal(30m, (await db.Store.ReadActiveAggregateAsync("orders", "first", "all", "total", token)).Value);
            Assert.Equal(999m, (await db.Store.ReadActiveAggregateAsync("orders", "another", "all", "total", token)).Value);
            // Publication drift changes actual lineage even if its name is unchanged.
            await SqlAsync(db, $"ALTER PUBLICATION \"{publication}\" SET(publish='insert,update,delete')", token);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await PostgreSqlProjectionLineage.CaptureAsync(db.DataSource, candidateSource, [publication], token));
        }
        finally
        {
            await SqlAsync(db, $"SELECT pg_drop_replication_slot(slot_name) FROM pg_replication_slots WHERE slot_name IN ('{activeSlot}','{candidateSlot}'); DROP PUBLICATION IF EXISTS \"{publication}\"", CancellationToken.None);
        }
    }

    private static PostgreSqlConsistentSnapshotSource Snapshot(ProjectionDatabase db, ProjectionSourceLineage evidence, string publication) =>
        new(db.DataSource, new PostgreSqlConsistentSnapshotOptions
        {
            Source = evidence.Source,
            PublicationNames = [publication],
            MaximumBatchRows = 1,
            MaximumParallelTables = 2,
            Tables = evidence.Tables.Select(static table => new PostgreSqlSnapshotTable(table, table.Columns.Where(static column => column.IsKey).Select(static column => column.Ordinal))).ToArray()
        });

    private static async Task BuildAsync(IConsistentSnapshotAttempt snapshot, StreamsProjectionConsumer consumer, CancellationToken token)
    {
        await consumer.StartSnapshotAsync(new(snapshot.Epoch, snapshot.Tables.Count), token);
        long rows = 0;
        await foreach (var batch in snapshot.ReadSnapshotAsync(token)) { rows += batch.Rows.Count; await consumer.ConsumeSnapshotBatchAsync(batch, token); }
        await consumer.CompleteSnapshotAsync(new(snapshot.Epoch, rows, snapshot.Tables.Count), token);
    }

    private static async Task CatchUpAsync(IAsyncEnumerator<ChangeTransactionDelivery> stream, StreamsProjectionConsumer consumer,
        BlueTuskLogSequenceNumber barrier, CancellationToken token)
    {
        for (var count = 0; count < 100; count++)
        {
            Assert.True(await stream.MoveNextAsync());
            await using var delivery = stream.Current;
            await consumer.ConsumeTransactionAsync(delivery, token);
            if (delivery.Transaction.CommitEndPosition >= barrier) { return; }
        }
        Assert.Fail("The real WAL stream did not cover the source barrier.");
    }

    private static async Task SqlAsync(ProjectionDatabase db, string sql, CancellationToken token)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token);
    }
}
