using System.Text;
using System.Text.Json;
using BlueTusk.Data;
using BlueTusk.Projections;
using BlueTusk.Projections.Tests;
using BlueTusk.Streams;
using BlueTusk.TypeSystem;
using BlueTusk.Live;
using BlueTusk.Projections.Live;
using System.Text.Json.Serialization;
using BlueTusk.Replication;

var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING") ??
    throw new InvalidOperationException("BLUETUSK_TEST_CONNECTION_STRING is required for the native projection smoke.");
await using var dataSource = BlueTuskDataSource.Create(connectionString);
var schema = "projections_aot_" + Guid.NewGuid().ToString("N");
var publication = "projections_aot_pub_" + Guid.NewGuid().ToString("N");
var store = new PostgreSqlProjectionStore(dataSource, new PostgreSqlProjectionsOptions { Schema = schema });
var recoverySlot = "projections_aot_recovery_" + Guid.NewGuid().ToString("N");
try
{
    await store.InitializeAsync();
    await using (var connection = await dataSource.OpenConnectionAsync())
    await using (var command = connection.CreateCommand())
    {
        command.CommandText = $"""
            CREATE TABLE "{schema}".customers(id text NOT NULL,tenant text NOT NULL,name text NOT NULL,PRIMARY KEY(tenant,id));
            CREATE TABLE "{schema}".orders(id text NOT NULL,tenant text NOT NULL,customer text NOT NULL,amount text NOT NULL,PRIMARY KEY(tenant,id));
            ALTER TABLE "{schema}".customers REPLICA IDENTITY FULL; ALTER TABLE "{schema}".orders REPLICA IDENTITY FULL;
            CREATE PUBLICATION "{publication}" FOR TABLE "{schema}".customers,"{schema}".orders
            """;
        await command.ExecuteNonQueryAsync();
    }
    BlueTuskReplicationSystemIdentity system;
    await using (var replication = await BlueTuskLogicalReplicationConnection.OpenAsync(dataSource.CreateDedicatedSessionOptions()))
    {
        system = await replication.IdentifySystemAsync();
    }
    var source = new ChangeSourceIdentity(system.SystemIdentifier, system.DatabaseName!, "aot-slot", publication);
    var lineage = await PostgreSqlProjectionLineage.CaptureAsync(dataSource, source, [publication]);
    var identity = new ProjectionIdentity("orders", 1, "aot-join-v1", source);
    await store.RegisterWithLineageAsync(identity, lineage);
    var lease = await store.AcquireAsync(identity, "worker", TimeSpan.FromMinutes(1)) ??
        throw new InvalidOperationException("Projection lease acquisition failed.");
    var definition = new OrdersProjection(identity);
    var epoch = SnapshotEpoch.Create(source, new BlueTuskLogSequenceNumber(100));
    await store.StartSnapshotAsync(lease, new SnapshotStart(epoch, 2));
    var orders = lineage.Tables.Single(static table => table.Name == "orders");
    var customers = lineage.Tables.Single(static table => table.Name == "customers");
    foreach (var row in new[] { Row(orders, "1", "tenant", "customer", "42.50"), Row(customers, "customer", "tenant", "Alice") })
    {
        var batch = new ChangeSnapshotBatch(epoch, row.Table, 0,
            [new ChangeSnapshotRow(SnapshotRowId.Create(epoch, row.Table, [row[0], row[1]]), row)], true);
        if (!await store.ApplySnapshotAsync(lease, definition, batch) || await store.ApplySnapshotAsync(lease, definition, batch))
        {
            throw new InvalidOperationException("Snapshot batch deduplication failed.");
        }
    }

    await store.CompleteSnapshotAsync(lease, new SnapshotComplete(epoch, 2, 2));
    await store.PromoteAsync(lease, epoch.ConsistentPosition, null);
    var document = await store.ReadActiveDocumentAsync("orders", "tenant", "1") ??
        throw new InvalidOperationException("The joined projection document was not published.");
    var value = JsonSerializer.Deserialize(document.Payload.Span, ProjectionJson.Default.OrderView);
    if (value?.CustomerName != "Alice" || value.Amount != 42.50m)
    {
        throw new InvalidOperationException("The source-generated joined read model did not match.");
    }

    var query = new ProjectionLiveQuery<OrderView>(store, "orders", "tenant", new("tenant:tenant", "v1"),
        "orders", "native-db", "v1", ProjectionJson.Default.OrderView);
    var metadata = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<LiveResultEvent<ProjectionLiveRow<OrderView>, string>>)
        NativeLiveJson.Default.GetTypeInfo(typeof(LiveResultEvent<ProjectionLiveRow<OrderView>, string>))!;
    var ownedReplay = new PostgreSqlProjectionLiveReplayStore(dataSource, new() { Schema = schema });
    await ownedReplay.InitializeAsync();
    await using var identitySession = query.CreateSession();
    var publisher = await ownedReplay.AcquireAsync(identitySession.Identity, "native-publisher", TimeSpan.FromMinutes(1)) ?? throw new InvalidOperationException("Native durable Live lease unavailable.");
    await using var live = new ProjectionLiveSubscription<OrderView>(query, publisher, metadata);
    await live.StartAsync();
    var connected = await live.ConnectAsync(0);
    if (connected.Connection?.Replay.Count != 1 || connected.Connection.Replay[0].Kind != LiveEventKind.InitialResult)
    {
        throw new InvalidOperationException("Native source-generated Live replay serialization failed.");
    }
    using var replayJson = JsonDocument.Parse(connected.Connection.Replay[0].Payload);
    if (replayJson.RootElement.GetProperty("rows")[0].GetProperty("Value").GetProperty("CustomerName").GetString() != "Alice")
    {
        throw new InvalidOperationException("Native Live joined result did not match.");
    }
    await connected.Connection.DisposeAsync();
    var cutoverEvidence = await PostgreSqlProjectionLineage.CaptureForCutoverAsync(dataSource, source, [publication]);
    if (cutoverEvidence.Lineage.Fingerprint != lineage.Fingerprint || cutoverEvidence.BarrierPosition == BlueTuskLogSequenceNumber.Zero)
    {
        throw new InvalidOperationException("Native actual publication lineage/barrier capture failed.");
    }
    await publisher.EnsureActiveAsync();
    await store.FenceForMaintenanceAsync("orders", 1, Guid.NewGuid(), "Native controlled source DDL rebuild");
    if (await store.RenewAsync(lease, TimeSpan.FromMinutes(1)) || await store.AcquireAsync(identity, "stale-native", TimeSpan.FromMinutes(1)) is not null)
    { throw new InvalidOperationException("Native maintenance fencing failed."); }
    await using (var connection = await dataSource.OpenConnectionAsync())
    await using (var command = connection.CreateCommand())
    {
        command.CommandText = $"ALTER TABLE \"{schema}\".orders ADD COLUMN note text NOT NULL DEFAULT 'native'";
        await command.ExecuteNonQueryAsync();
    }
    var recoverySource = new ChangeSourceIdentity(system.SystemIdentifier, system.DatabaseName!, recoverySlot, publication);
    var recoveryEvidence = await PostgreSqlProjectionLineage.CaptureForCutoverAsync(dataSource, recoverySource, [publication]);
    var recoveryIdentity = new ProjectionIdentity("orders", 2, "native-recovery-v2", recoverySource);
    await store.RegisterWithLineageAsync(recoveryIdentity, recoveryEvidence.Lineage);
    var recoveryId = Guid.NewGuid();
    await store.BeginRecoveryAsync(recoveryIdentity, 1, recoveryEvidence, recoveryId, "Native controlled source DDL rebuild");
    var recoveryLease = await store.AcquireAsync(recoveryIdentity, "native-rebuild", TimeSpan.FromMinutes(1)) ?? throw new InvalidOperationException("Native recovery lease unavailable.");
    var recoveryConsumer = new StreamsProjectionConsumer(store, recoveryLease, new OrdersProjection(recoveryIdentity));
    var recoverySnapshots = new PostgreSqlConsistentSnapshotSource(dataSource, new()
    {
        Source = recoverySource, PublicationNames = [publication], MaximumBatchRows = 17,
        Tables = recoveryEvidence.Lineage.Tables.Select(static table => new PostgreSqlSnapshotTable(table, table.Columns.Where(static column => column.IsKey).Select(static column => column.Ordinal))).ToArray()
    });
    await using var recoveryAttempt = await recoverySnapshots.BeginAttemptAsync(null);
    await recoveryConsumer.StartSnapshotAsync(new(recoveryAttempt.Epoch, 2));
    long recoveryRows = 0;
    await foreach (var batch in recoveryAttempt.ReadSnapshotAsync()) { recoveryRows += batch.Rows.Count; await recoveryConsumer.ConsumeSnapshotBatchAsync(batch); }
    await recoveryConsumer.CompleteSnapshotAsync(new(recoveryAttempt.Epoch, recoveryRows, 2));
    var finalRecoveryEvidence = await PostgreSqlProjectionLineage.CaptureForCutoverAsync(dataSource, recoverySource, [publication]);
    using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    await using var recoveryWal = recoveryAttempt.CreateChangeStream().ReadTransactionsAsync(recoveryTimeout.Token).GetAsyncEnumerator(recoveryTimeout.Token);
    for (var pending = 0; ; pending++)
    {
        if (pending == 128 || !await recoveryWal.MoveNextAsync()) { throw new InvalidOperationException("Native recovery failed to cover its bounded verified WAL barrier."); }
        await using var delivery = recoveryWal.Current;
        await recoveryConsumer.ConsumeTransactionAsync(delivery, recoveryTimeout.Token);
        if (delivery.Transaction.CommitEndPosition >= finalRecoveryEvidence.BarrierPosition) { break; }
    }
    await store.CompleteRecoveryAsync(recoveryLease, recoveryId, finalRecoveryEvidence);
    await store.CompleteRecoveryAsync(recoveryLease, recoveryId, finalRecoveryEvidence);
    if ((await store.ReadRecoveryAsync("orders", recoveryId))?.IsComplete != true || await store.ReadActiveVersionAsync("orders") != 2)
    { throw new InvalidOperationException("Native durable operator recovery/cutover failed."); }
    await live.RefreshAsync();
    Console.WriteLine("Projections native bounded reset/snapshot/join/bulk mirrors/aggregate/checkpoint/publish, owned durable Live JSON replay, actual lineage/barrier and fenced controlled-DDL fresh snapshot/WAL recovery passed.");
}
finally
{
    await using var connection = await dataSource.OpenConnectionAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = $"SELECT pg_drop_replication_slot(slot_name) FROM pg_replication_slots WHERE slot_name='{recoverySlot}'; DROP PUBLICATION IF EXISTS \"{publication}\"; DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
    await command.ExecuteNonQueryAsync();
}

static ChangeRow Row(ChangeTable table, params string[] values) => new(table,
    values.Select(static value => ChangeColumnValue.FromValue(Encoding.UTF8.GetBytes(value), ChangeValueEncoding.Text)));

[JsonSerializable(typeof(LiveResultEvent<ProjectionLiveRow<OrderView>, string>))]
internal sealed partial class NativeLiveJson : JsonSerializerContext;
