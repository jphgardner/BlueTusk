using BlueTusk.Live;
using BlueTusk.Live.Testing;
using BlueTusk.Projections.Live;
using BlueTusk.Replication;
using BlueTusk.Streams;

namespace BlueTusk.Projections.Tests;

public sealed class ConsistentProjectionIntegrationTests
{
    [Fact]
    public async Task ExportedSnapshotPlusRealWalProjectsConcurrentTwoTableTransactionWithoutGap()
    {
        await using var fixture = await ProjectionDatabase.CreateAsync();
        var publication = "proj_pub_" + Guid.NewGuid().ToString("N");
        var slot = "proj_slot_" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var administration = await fixture.DataSource.OpenConnectionAsync(token);
        await ExecuteAsync(administration, $"""
            CREATE TABLE "{fixture.Schema}".customers(id text NOT NULL, tenant text NOT NULL, name text NOT NULL, PRIMARY KEY(tenant,id));
            CREATE TABLE "{fixture.Schema}".orders(id text NOT NULL, tenant text NOT NULL, customer text NOT NULL, amount text NOT NULL, PRIMARY KEY(tenant,id));
            ALTER TABLE "{fixture.Schema}".customers REPLICA IDENTITY FULL;
            ALTER TABLE "{fixture.Schema}".orders REPLICA IDENTITY FULL;
            INSERT INTO "{fixture.Schema}".customers VALUES('customer','first','Alice');
            INSERT INTO "{fixture.Schema}".orders VALUES('1','first','customer','10');
            CREATE PUBLICATION "{publication}" FOR TABLE "{fixture.Schema}".customers, "{fixture.Schema}".orders
            """, token);
        var slotCreated = false;
        try
        {
            ChangeSourceIdentity sourceIdentity;
            await using (var identityConnection = await BlueTuskLogicalReplicationConnection.OpenAsync(fixture.DataSource.CreateDedicatedSessionOptions(), token))
            {
                var system = await identityConnection.IdentifySystemAsync(token);
                sourceIdentity = new ChangeSourceIdentity(system.SystemIdentifier, system.DatabaseName!, slot, publication);
            }

            var projectionIdentity = new ProjectionIdentity("orders", 1, "live-two-table-join-v1", sourceIdentity);
            await fixture.Store.RegisterAsync(projectionIdentity, token);
            var lease = Assert.IsType<ProjectionLease>(await fixture.Store.AcquireAsync(projectionIdentity, "live-worker", TimeSpan.FromMinutes(2), token));
            var definition = new OrdersProjection(projectionIdentity);
            var consumer = new StreamsProjectionConsumer(fixture.Store, lease, definition);
            var tables = new[]
            {
                LiveTable(fixture.Schema, "customers", "id", "tenant", "name"),
                LiveTable(fixture.Schema, "orders", "id", "tenant", "customer", "amount")
            };
            var source = new PostgreSqlConsistentSnapshotSource(fixture.DataSource, new PostgreSqlConsistentSnapshotOptions
            {
                Source = sourceIdentity,
                PublicationNames = [publication],
                Tables = tables.Select(static table => new PostgreSqlSnapshotTable(table, [0, 1])).ToArray(),
                CopyPageRows = 1,
                MaximumBatchRows = 1,
                MaximumParallelTables = 2
            });
            await using (var attempt = await source.BeginAttemptAsync(null, token))
            {
                slotCreated = true;
                await consumer.StartSnapshotAsync(new SnapshotStart(attempt.Epoch, 2), token);
                // Commit both source tables after the slot's exported snapshot has been established,
                // before reading either snapshot table. Both changes must arrive only through WAL.
                await ExecuteAsync(administration, $"""
                    BEGIN;
                    UPDATE "{fixture.Schema}".customers SET name='Bob' WHERE id='customer' AND tenant='first';
                    INSERT INTO "{fixture.Schema}".orders VALUES('2','first','customer','20');
                    COMMIT
                    """, token);
                long rows = 0;
                await foreach (var batch in attempt.ReadSnapshotAsync(token))
                {
                    rows += batch.Rows.Count;
                    await consumer.ConsumeSnapshotBatchAsync(batch, token);
                }

                Assert.Equal(2, rows);
                await consumer.CompleteSnapshotAsync(new SnapshotComplete(attempt.Epoch, rows, 2), token);
                await fixture.Store.PromoteAsync(lease, attempt.Epoch.ConsistentPosition, null, token);
                Assert.Equal("Alice", (await fixture.ReadAsync())!.CustomerName);
                Assert.Null(await fixture.ReadAsync(id: "2"));
                Assert.Equal(10m, await fixture.AggregateAsync());
                var query = new ProjectionLiveQuery<OrderView>(fixture.Store, "orders", "first", new("tenant:first", "v1"),
                    "orders", "live-db", "v1", ProjectionJson.Default.OrderView);
                await using var live = new ProjectionLiveSubscription<OrderView>(query, new InMemoryLiveReplayStore(), ProjectionLiveJson.EventTypeInfo);
                await live.StartAsync(token);
                var beforeWal = await live.ConnectAsync(0, token);
                Assert.Equal("Alice", ProjectionLiveTests.Initial(beforeWal.Connection!.Replay[0]).GetProperty("rows")[0].GetProperty("Value").GetProperty("CustomerName").GetString());
                await beforeWal.Connection.DisposeAsync();
                await using var changes = attempt.CreateChangeStream().ReadTransactionsAsync(token).GetAsyncEnumerator(token);
                var sawSourceChanges = false;
                for (var i = 0; i < 100 && !sawSourceChanges; i++)
                {
                    Assert.True(await changes.MoveNextAsync());
                    await using var delivery = changes.Current;
                    sawSourceChanges = delivery.Transaction.Changes.Count > 0;
                    await consumer.ConsumeTransactionAsync(delivery, token);
                }

                Assert.True(sawSourceChanges);
                Assert.Equal("Bob", (await fixture.ReadAsync())!.CustomerName);
                Assert.Equal(new OrderView("2", "Bob", 20m, 1), await fixture.ReadAsync(id: "2"));
                Assert.Equal(30m, await fixture.AggregateAsync());
                Assert.True((await fixture.Store.ReadStateAsync(projectionIdentity, token)).Checkpoint > attempt.Epoch.ConsistentPosition);
                var afterWal = await live.ConnectAsync(1, token);
                Assert.Contains(afterWal.Connection!.Replay, item => item.Kind == LiveEventKind.RowUpdated);
                Assert.Contains(afterWal.Connection.Replay, item => item.Kind == LiveEventKind.RowAdded);
                Assert.Equal(2, live.Status.QuerySession.ResultCount);
                await afterWal.Connection.DisposeAsync();
            }
        }
        finally
        {
            if (slotCreated)
            {
                await using var cleanup = await BlueTuskLogicalReplicationConnection.OpenAsync(fixture.DataSource.CreateDedicatedSessionOptions());
                await cleanup.DropReplicationSlotAsync(slot, wait: true);
            }

            await ExecuteAsync(administration, $"DROP PUBLICATION IF EXISTS \"{publication}\"", CancellationToken.None);
        }
    }

    private static ChangeTable LiveTable(string schema, string name, params string[] columns) => new(0, schema, name, 'f',
        columns.Select((column, ordinal) => new ChangeColumn(ordinal, column, 25, -1, ordinal < 2)));

    private static async ValueTask ExecuteAsync(System.Data.Common.DbConnection connection, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token);
    }
}
