using System.Text.Json;
using BlueTusk.Replication;
using BlueTusk.Streams;
using BlueTusk.TypeSystem;

namespace BlueTusk.Projections.Tests;

public sealed class ProjectionBoundedWorkloadTests
{
    [Fact]
    public async Task SnapshotAndRetainedWalBacklogRecoverPromoteAndPruneWithoutGapOrCrossTenantEffects()
    {
        await using var db = await ProjectionDatabase.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        var publication = "proj_load_pub_" + Guid.NewGuid().ToString("N");
        var activeSlot = "proj_load_a_" + Guid.NewGuid().ToString("N");
        var candidateSlot = "proj_load_b_" + Guid.NewGuid().ToString("N");
        await SqlAsync(db, $"""
            CREATE TABLE "{db.Schema}".customers(id text NOT NULL, tenant text NOT NULL, name text NOT NULL, PRIMARY KEY(tenant,id));
            CREATE TABLE "{db.Schema}".orders(id text NOT NULL, tenant text NOT NULL, customer text NOT NULL, amount text NOT NULL, PRIMARY KEY(tenant,id));
            ALTER TABLE "{db.Schema}".customers REPLICA IDENTITY FULL;
            ALTER TABLE "{db.Schema}".orders REPLICA IDENTITY FULL;
            INSERT INTO "{db.Schema}".customers VALUES('customer','first','Alice'),('customer','another','Private');
            INSERT INTO "{db.Schema}".orders SELECT lpad(i::text,4,'0'),tenant,'customer','1'
                FROM generate_series(1,64) i CROSS JOIN (VALUES('first'),('another')) tenants(tenant);
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
            var activeLineage = await PostgreSqlProjectionLineage.CaptureAsync(db.DataSource, activeSource, [publication], token);
            var candidateLineage = await PostgreSqlProjectionLineage.CaptureAsync(db.DataSource, candidateSource, [publication], token);
            var activeIdentity = new ProjectionIdentity("orders", 1, "bounded-load-v1", activeSource);
            var candidateIdentity = new ProjectionIdentity("orders", 2, "bounded-load-v2", candidateSource);
            await db.Store.RegisterWithLineageAsync(activeIdentity, activeLineage, token);
            await db.Store.RegisterWithLineageAsync(candidateIdentity, candidateLineage, token);
            var active = Assert.IsType<ProjectionLease>(await db.Store.AcquireAsync(activeIdentity, "active", TimeSpan.FromMinutes(2), token));
            var candidate = Assert.IsType<ProjectionLease>(await db.Store.AcquireAsync(candidateIdentity, "before-restart", TimeSpan.FromMinutes(2), token));
            var activeDefinition = new OrdersProjection(activeIdentity) { DependencyPageSize = 17 };
            var candidateDefinition = new OrdersProjection(candidateIdentity) { DependencyPageSize = 17 };
            var activeConsumer = new StreamsProjectionConsumer(db.Store, active, activeDefinition);
            var candidateConsumer = new StreamsProjectionConsumer(db.Store, candidate, candidateDefinition);
            await using var activeSnapshot = await Snapshot(db, activeLineage, publication).BeginAttemptAsync(null, token);
            await BuildAsync(activeSnapshot, activeConsumer, token);
            await db.Store.PromoteAsync(active, activeSnapshot.Epoch.ConsistentPosition, null, token);
            await using var candidateSnapshot = await Snapshot(db, candidateLineage, publication).BeginAttemptAsync(null, token);
            // Sixteen separate committed transactions remain behind the open candidate snapshot.
            for (var batch = 0; batch < 16; batch++)
            {
                var first = 65 + (batch * 4);
                await SqlAsync(db, $"INSERT INTO \"{db.Schema}\".orders SELECT lpad(i::text,4,'0'),'first','customer','2' FROM generate_series({first},{first + 3}) i", token);
            }
            await SqlAsync(db, $"""
                BEGIN;
                UPDATE "{db.Schema}".customers SET name='Bob' WHERE tenant='first';
                DELETE FROM "{db.Schema}".orders WHERE tenant='first' AND id<='0016';
                COMMIT;
                """, token);
            await BuildAsync(candidateSnapshot, candidateConsumer, token);
            Assert.Equal(64m, await db.AggregateAsync(2));
            var cutover = await PostgreSqlProjectionLineage.CaptureForCutoverAsync(db.DataSource, candidateSource, [publication], token);
            await using var activeWal = activeSnapshot.CreateChangeStream().ReadTransactionsAsync(token).GetAsyncEnumerator(token);
            await using var candidateWal = candidateSnapshot.CreateChangeStream().ReadTransactionsAsync(token).GetAsyncEnumerator(token);
            await CatchUpAsync(activeWal, activeConsumer, cutover.BarrierPosition, token);
            Assert.True(await candidateWal.MoveNextAsync());
            await using (var uncertainDelivery = candidateWal.Current)
            {
                // Destination commit followed by lost process/ack: the replacement replays that exact
                // source delivery after reacquiring ownership and must not repeat aggregate effects.
                Assert.True((await db.Store.ApplyAsync(candidate, candidateDefinition, uncertainDelivery.Transaction, token)).WasApplied);
                Assert.True(await db.Store.ReleaseAsync(candidate, token));
                var recovered = Assert.IsType<ProjectionLease>(await db.Store.AcquireAsync(candidateIdentity, "after-restart", TimeSpan.FromMinutes(2), token));
                Assert.True(recovered.FencingToken > candidate.FencingToken);
                candidate = recovered;
                candidateConsumer = new StreamsProjectionConsumer(db.Store, candidate, candidateDefinition);
                Assert.False((await db.Store.ApplyAsync(candidate, candidateDefinition, uncertainDelivery.Transaction, token)).WasApplied);
                await candidateConsumer.ConsumeTransactionAsync(uncertainDelivery, token);
            }
            await CatchUpAsync(candidateWal, candidateConsumer, cutover.BarrierPosition, token);
            Assert.Equal(176m, await db.AggregateAsync(2));
            Assert.Equal(64m, await db.AggregateAsync(2, "another"));
            await db.Store.PromoteWithLineageAsync(candidate, new(0), 1, cutover, token);
            var firstTenant = await ReadAllAsync(db, "first", token);
            Assert.Equal(112, firstTenant.Count);
            Assert.All(firstTenant, row => { Assert.Equal("Bob", row.CustomerName); Assert.Equal(2, row.Version); });
            Assert.DoesNotContain(firstTenant, static row => string.CompareOrdinal(row.Id, "0016") <= 0);
            Assert.Equal(176m, firstTenant.Sum(static row => row.Amount));
            var anotherTenant = await ReadAllAsync(db, "another", token);
            Assert.Equal(64, anotherTenant.Count);
            Assert.All(anotherTenant, row => Assert.Equal("Private", row.CustomerName));
            var publicationAfterCutover = await db.Store.ReadPublicationAsync("orders", token);
            var checkpointAfterCutover = (await db.Store.ReadStateAsync(candidateIdentity, token)).Checkpoint;
            await db.Store.RetireAsync(active, 2, token);
            var totalPruned = 0;
            ProjectionRetentionResult retention;
            do
            {
                retention = await db.Store.PruneRetiredAsync(activeIdentity, 37, token);
                Assert.InRange(retention.DeletedRows, 0, 37);
                totalPruned += retention.DeletedRows;
                Assert.InRange(totalPruned, 1, 2000);
            } while (retention.HasRemainingRows);
            Assert.True(totalPruned > 600);
            Assert.Equal(publicationAfterCutover, await db.Store.ReadPublicationAsync("orders", token));
            Assert.Equal(checkpointAfterCutover, (await db.Store.ReadStateAsync(candidateIdentity, token)).Checkpoint);
            Assert.Equal(176m, (await db.Store.ReadActiveAggregateAsync("orders", "first", "all", "total", token)).Value);
            await Assert.ThrowsAsync<ProjectionRetiredException>(async () => await db.Store.RegisterWithLineageAsync(activeIdentity, activeLineage, token));
            Assert.Equal(new ProjectionRetentionResult(0, false), await db.Store.PruneRetiredAsync(activeIdentity, 37, token));
        }
        finally
        {
            await SqlAsync(db, $"SELECT pg_drop_replication_slot(slot_name) FROM pg_replication_slots WHERE slot_name IN ('{activeSlot}','{candidateSlot}'); DROP PUBLICATION IF EXISTS \"{publication}\"", CancellationToken.None);
        }
    }

    private static PostgreSqlConsistentSnapshotSource Snapshot(ProjectionDatabase db, ProjectionSourceLineage lineage, string publication) =>
        new(db.DataSource, new PostgreSqlConsistentSnapshotOptions
        {
            Source = lineage.Source,
            PublicationNames = [publication],
            MaximumBatchRows = 17,
            MaximumParallelTables = 2,
            Tables = lineage.Tables.Select(static table => new PostgreSqlSnapshotTable(table, table.Columns.Where(static column => column.IsKey).Select(static column => column.Ordinal))).ToArray()
        });

    private static async Task BuildAsync(IConsistentSnapshotAttempt snapshot, StreamsProjectionConsumer consumer, CancellationToken token)
    {
        await consumer.StartSnapshotAsync(new(snapshot.Epoch, snapshot.Tables.Count), token);
        long count = 0;
        await foreach (var batch in snapshot.ReadSnapshotAsync(token))
        {
            Assert.InRange(batch.Rows.Count, 0, 17);
            count += batch.Rows.Count;
            await consumer.ConsumeSnapshotBatchAsync(batch, token);
        }
        await consumer.CompleteSnapshotAsync(new(snapshot.Epoch, count, snapshot.Tables.Count), token);
    }

    private static async Task CatchUpAsync(IAsyncEnumerator<ChangeTransactionDelivery> source, StreamsProjectionConsumer consumer,
        BlueTuskLogSequenceNumber through, CancellationToken token)
    {
        for (var count = 0; count < 100; count++)
        {
            Assert.True(await source.MoveNextAsync());
            await using var delivery = source.Current;
            await consumer.ConsumeTransactionAsync(delivery, token);
            if (delivery.Transaction.CommitEndPosition >= through) { return; }
        }
        Assert.Fail("Retained WAL did not reach the verified source barrier.");
    }

    private static async Task<List<OrderView>> ReadAllAsync(ProjectionDatabase db, string tenant, CancellationToken token)
    {
        var result = new List<OrderView>();
        ProjectionPageCursor? continuation = null;
        do
        {
            var page = await db.Store.ReadActivePageAsync("orders", tenant, 17, 65536, after: continuation, cancellationToken: token);
            Assert.InRange(page.Documents.Count, 0, 17);
            Assert.InRange(page.Documents.Sum(static document => document.Payload.Length), 0, 65536);
            foreach (var document in page.Documents) { result.Add(JsonSerializer.Deserialize(document.Payload.Span, ProjectionJson.Default.OrderView)!); }
            continuation = page.ContinueAfter;
        } while (continuation is not null);
        return result;
    }

    private static async Task SqlAsync(ProjectionDatabase db, string sql, CancellationToken token)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token);
    }
}
