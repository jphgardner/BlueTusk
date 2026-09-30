using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueTusk.Data;
using BlueTusk.Events;
using BlueTusk.Events.Streams;
using BlueTusk.Live;
using BlueTusk.Projections.Live;
using BlueTusk.Replication;
using BlueTusk.Streams;
using BlueTusk.TypeSystem;

namespace BlueTusk.Projections.Tests;

public sealed class ProjectionPhysicalRecoveryTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };
    [Fact]
    public async Task SynchronousPhysicalPromotionPreservesAcknowledgedEffectsFencesOldOwnersAndRequiresFreshSnapshotRecoveryThenControlledDdlRebuild()
    {
        var primaryConnection = Environment.GetEnvironmentVariable("BLUETUSK_RECOVERY_PRIMARY");
        var standbyConnection = Environment.GetEnvironmentVariable("BLUETUSK_RECOVERY_STANDBY");
        if (string.IsNullOrWhiteSpace(primaryConnection) || string.IsNullOrWhiteSpace(standbyConnection))
        { throw new InvalidOperationException("Run the separately owned docs/projections/evidence/run-physical-recovery.ps1 fixture. This campaign cannot pass without real primary/standby configuration."); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = deadline.Token;
        var started = Stopwatch.GetTimestamp();
        await using var primary = BlueTuskDataSource.Create(primaryConnection);
        await using var promoted = BlueTuskDataSource.Create(standbyConnection);
        await using var routed = new RoutedDataSource(primary);
        var schema = "recovery_" + Guid.NewGuid().ToString("N");
        var eventSchema = schema + "_events";
        var publication = "recovery_pub_" + Guid.NewGuid().ToString("N");
        var eventPublication = "recovery_events_" + Guid.NewGuid().ToString("N");
        var slot = "recovery_slot_" + Guid.NewGuid().ToString("N");
        var eventSlot = "recovery_event_slot_" + Guid.NewGuid().ToString("N");
        var store = new PostgreSqlProjectionStore(routed, new() { Schema = schema, MaximumResetBatchRows = 17 });
        var events = new PostgreSqlEventStore(routed, new() { Schema = eventSchema, MaximumEventBytes = 128, MaximumAppendBytes = 512 });
        await store.InitializeAsync(token); await events.InitializeAsync(token);
        Assert.Equal("remote_apply", await ScalarAsync(primary, "SHOW synchronous_commit", token));
        Assert.Equal(1L, await ScalarAsync(primary, "SELECT count(*) FROM pg_stat_replication WHERE application_name='bluetusk_recovery_standby' AND sync_state='sync'", token));
        await SqlAsync(primary, $"""
            CREATE TABLE "{schema}".customers(id text NOT NULL,tenant text NOT NULL,name text NOT NULL,PRIMARY KEY(tenant,id));
            CREATE TABLE "{schema}".orders(id text NOT NULL,tenant text NOT NULL,customer text NOT NULL,amount text NOT NULL,PRIMARY KEY(tenant,id));
            ALTER TABLE "{schema}".customers REPLICA IDENTITY FULL; ALTER TABLE "{schema}".orders REPLICA IDENTITY FULL;
            INSERT INTO "{schema}".customers VALUES('customer','first','Alice'),('customer','another','Private');
            INSERT INTO "{schema}".orders VALUES('1','first','customer','0'),('private','another','customer','999');
            CREATE TABLE "{eventSchema}".effects(consumer text NOT NULL,tenant text NOT NULL,event_id uuid NOT NULL,sequence bigint NOT NULL,PRIMARY KEY(consumer,tenant,event_id));
            CREATE PUBLICATION "{publication}" FOR TABLE "{schema}".customers,"{schema}".orders;
            CREATE PUBLICATION "{eventPublication}" FOR TABLE "{eventSchema}".outbox
            """, token);
        var beforeSystem = await SystemAsync(primary, token);
        var source = new ChangeSourceIdentity(beforeSystem.SystemIdentifier, beforeSystem.DatabaseName!, slot, publication);
        var oldLineage = await PostgreSqlProjectionLineage.CaptureAsync(primary, source, [publication], token);
        var firstIdentity = new ProjectionIdentity("orders", 1, "recovery-v1", source);
        await store.RegisterWithLineageAsync(firstIdentity, oldLineage, token);
        var firstLease = Assert.IsType<ProjectionLease>(await store.AcquireAsync(firstIdentity, "original-worker", TimeSpan.FromMinutes(5), token));
        var consumer = new StreamsProjectionConsumer(store, firstLease, new OrdersProjection(firstIdentity));
        var snapshotSource = Snapshot(primary, oldLineage, publication);
        var firstAttempt = await snapshotSource.BeginAttemptAsync(null, token);
        await BuildAsync(firstAttempt, consumer, token);
        await store.PromoteAsync(firstLease, firstAttempt.Epoch.ConsistentPosition, null, token);
        var projectionWal = firstAttempt.CreateChangeStream().ReadTransactionsAsync(token).GetAsyncEnumerator(token);
        var eventSource = new ChangeSourceIdentity(beforeSystem.SystemIdentifier, beforeSystem.DatabaseName!, eventSlot, eventPublication);
        var eventSnapshotSource = new PostgreSqlConsistentSnapshotSource(primary, new()
        {
            Source = eventSource,
            PublicationNames = [eventPublication],
            MaximumBatchRows = 17,
            Tables = [new(OutboxTable(eventSchema), [0, 1, 2])]
        });
        var eventAttempt = await eventSnapshotSource.BeginAttemptAsync(null, token);
        await foreach (var batch in eventAttempt.ReadSnapshotAsync(token)) { Assert.Empty(batch.Rows); }
        var eventWal = eventAttempt.CreateChangeStream().ReadTransactionsAsync(token).GetAsyncEnumerator(token);
        var processor = new PostgreSqlEventDeliveryProcessor(routed, events, new(eventSchema, 128), "wal", eventSource);
        async ValueTask HandleAsync(StoredEvent value, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = $"INSERT INTO \"{eventSchema}\".effects VALUES('wal',@tenant,@id,@sequence)";
            Add(command, "tenant", value.Stream.TenantId); Add(command, "id", value.EventId); Add(command, "sequence", value.Sequence);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        var query = new ProjectionLiveQuery<OrderView>(store, "orders", "first", new("tenant:first", "v1"), "recovery", "db", "v1", ProjectionJson.Default.OrderView);
        await using var identitySession = query.CreateSession();
        var liveReplay = new PostgreSqlProjectionLiveReplayStore(routed, new() { Schema = schema });
        await liveReplay.InitializeAsync(token);
        var oldOwner = Assert.IsType<ProjectionLivePublisher>(await liveReplay.AcquireAsync(identitySession.Identity, "original-live", TimeSpan.FromSeconds(2), token));
        await using var oldLive = new ProjectionLiveSubscription<OrderView>(query, oldOwner, RecoveryLiveJson.EventTypeInfo);
        await oldLive.StartAsync(token);
        var protector = new LiveResumeTokenProtector([new("key", new byte[32], true)]);
        var eventStream = new EventStreamKey("first", "orders");
        var secondStream = new EventStreamKey("another", "orders");
        var writes = new List<EventWrite>();
        var lastXid = 0U;
        for (var amount = 1; amount <= 8; amount++)
        {
            var write = new EventWrite(Guid.NewGuid(), "order.changed", 1, DateTimeOffset.UtcNow, "{}"u8); writes.Add(write);
            await using (var connection = await primary.OpenConnectionAsync(token))
            await using (var transaction = await connection.BeginTransactionAsync(token))
            {
                await using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = $"UPDATE \"{schema}\".orders SET amount=@amount WHERE tenant='first' AND id='1'";
                Add(command, "amount", amount.ToString(CultureInfo.InvariantCulture)); await command.ExecuteNonQueryAsync(token);
                Assert.False(Assert.Single(await events.AppendAsync(connection, transaction, eventStream, [write], token)).WasAlreadyStored);
                Assert.False(Assert.Single(await events.AppendAsync(connection, transaction, secondStream, [write], token)).WasAlreadyStored);
                command.Parameters.Clear(); command.CommandText = "SELECT pg_current_xact_id()::text";
                lastXid = unchecked((uint)ulong.Parse((string)(await command.ExecuteScalarAsync(token))!, CultureInfo.InvariantCulture));
                await transaction.CommitAsync(token);
            }
            await CoverXidAsync(projectionWal, lastXid, async delivery => await consumer.ConsumeTransactionAsync(delivery, token), token);
            await CoverXidAsync(eventWal, lastXid, async delivery => { Assert.Equal(new(2, 2), await processor.ProcessAsync(delivery, HandleAsync, token)); }, token);
            await oldLive.RefreshAsync(token);
        }
        var liveHead = (await liveReplay.ReadAsync(oldLive.Identity, 0, 100, token)).LastSequence;
        var resume = protector.Protect(oldLive.Identity, liveHead, TimeSpan.FromMinutes(2));
        var oldReplayLease = Assert.IsType<EventReplayLease>(await events.AcquireReplayAsync("replay", eventStream, "original-replay", TimeSpan.FromMinutes(5), token));
        async ValueTask ReplayHandleAsync(StoredEvent value, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = $"INSERT INTO \"{eventSchema}\".effects VALUES('replay',@tenant,@id,@sequence)";
            Add(command, "tenant", value.Stream.TenantId); Add(command, "id", value.EventId); Add(command, "sequence", value.Sequence);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        Assert.Equal(3, (await events.ReplayAsync(oldReplayLease, ReplayHandleAsync, 3, 128, token)).Checkpoint);
        Assert.Equal(16L, await ScalarAsync(primary, $"SELECT count(*) FROM \"{eventSchema}\".effects WHERE consumer='wal'", token));
        var oldCheckpoint = (await store.ReadStateAsync(firstIdentity, token)).Checkpoint;
        await projectionWal.DisposeAsync(); await eventWal.DisposeAsync(); await firstAttempt.DisposeAsync(); await eventAttempt.DisposeAsync();
        // The script's synchronous remote_apply setting covers every acknowledged application,
        // inbox, checkpoint and owned Live commit above. Kill the primary before promotion to avoid split brain.
        var promotionStarted = Stopwatch.GetTimestamp();
        await FixtureDockerAsync("BLUETUSK_RECOVERY_PRIMARY_CONTAINER", ["kill", "--signal", "KILL"], token);
        await FixtureDockerAsync("BLUETUSK_RECOVERY_STANDBY_CONTAINER", ["exec"], token,
            ["gosu", "postgres", "pg_ctl", "-D", "/var/lib/postgresql/data/pgdata", "promote", "-w", "-t", "30"]);
        await SqlAsync(promoted, "ALTER SYSTEM SET synchronous_standby_names=''", token);
        await SqlAsync(promoted, "ALTER SYSTEM SET synchronous_commit='on'", token);
        await SqlAsync(promoted, "SELECT pg_reload_conf(); CHECKPOINT", token);
        routed.Target = promoted;
        var afterSystem = await SystemAsync(promoted, token);
        Assert.Equal(beforeSystem.SystemIdentifier, afterSystem.SystemIdentifier);
        Assert.True(afterSystem.Timeline > beforeSystem.Timeline);
        Assert.Equal(oldCheckpoint, (await store.ReadStateAsync(firstIdentity, token)).Checkpoint);
        Assert.Equal(16L, await ScalarAsync(promoted, $"SELECT count(*) FROM \"{eventSchema}\".effects WHERE consumer='wal'", token));
        Assert.Equal(3L, await ScalarAsync(promoted, $"SELECT count(*) FROM \"{eventSchema}\".effects WHERE consumer='replay'", token));
        Assert.Equal(8, (await events.ReadAsync(eventStream, maximumEvents: 17, maximumPayloadBytes: 128, cancellationToken: token)).Count);
        Assert.Equal(8, (await events.ReadAsync(secondStream, maximumEvents: 17, maximumPayloadBytes: 128, cancellationToken: token)).Count);
        Assert.Equal(liveHead, (await liveReplay.ReadAsync(oldLive.Identity, liveHead, 100, token)).LastSequence);
        Assert.Equal(0L, await ScalarAsync(promoted, $"SELECT count(*) FROM pg_replication_slots WHERE slot_name='{slot}'", token));
        var newLineage = await PostgreSqlProjectionLineage.CaptureAsync(promoted, source, [publication], token);
        Assert.NotEqual(oldLineage.Fingerprint, newLineage.Fingerprint);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.RegisterWithLineageAsync(firstIdentity, newLineage, token));
        // Probe the default policy before a recovery ticket exists, with exactly the same
        // Streams identity. The differing physical timeline alone must prevent ordinary cutover.
        var probeIdentity = new ProjectionIdentity("orders", 99, "timeline-policy-probe", source);
        await store.RegisterWithLineageAsync(probeIdentity, newLineage, token);
        var probeLease = Assert.IsType<ProjectionLease>(await store.AcquireAsync(probeIdentity, "timeline-probe", TimeSpan.FromMinutes(1), token));
        await using (var probeAttempt = await Snapshot(promoted, newLineage, publication).BeginAttemptAsync(null, token))
        {
            await BuildAsync(probeAttempt, new(store, probeLease, new OrdersProjection(probeIdentity)), token);
            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.PromoteAsync(probeLease, probeAttempt.Epoch.ConsistentPosition, 1, token));
            Assert.Contains("Strict cutover cannot change bound catalogue/timeline lineage", rejected.Message, StringComparison.Ordinal);
            await store.RetireAsync(probeLease, 1, token);
        }
        Assert.Equal(0L, await ScalarAsync(promoted, $"SELECT count(*) FROM pg_replication_slots WHERE slot_name='{slot}'", token));
        ProjectionLivePublisher? replacementOwner = null;
        for (var attempt = 0; attempt < 100 && replacementOwner is null; attempt++)
        {
            replacementOwner = await liveReplay.AcquireAsync(oldLive.Identity, "promoted-live", TimeSpan.FromMinutes(1), token);
            if (replacementOwner is null) { await Task.Delay(100, token); }
        }
        var newOwner = Assert.IsType<ProjectionLivePublisher>(replacementOwner);
        Assert.True(newOwner.Lease.FencingToken > oldOwner.Lease.FencingToken);
        await Assert.ThrowsAsync<ProjectionLivePublisherFencedException>(async () => await oldLive.RefreshAsync(token));
        await Assert.ThrowsAsync<ProjectionLivePublisherFencedException>(async () => await oldOwner.AppendAsync(new(oldLive.Identity, liveHead, [new(liveHead + 1, LiveEventKind.RowAdded, "json", "stale"u8)]), token));
        await using var replacementLive = new ProjectionLiveSubscription<OrderView>(query, newOwner, RecoveryLiveJson.EventTypeInfo);
        await replacementLive.StartAsync(token);
        var restart = await replacementLive.ConnectWithTokenAsync(resume, protector, token);
        Assert.Equal("ServerRestart", Initial(Assert.Single(restart.Connection!.Replay)).GetProperty("resetReason").GetString());
        await restart.Connection.DisposeAsync();
        Assert.True(await events.ReleaseReplayAsync(oldReplayLease, token));
        var newReplayLease = Assert.IsType<EventReplayLease>(await events.AcquireReplayAsync("replay", eventStream, "promoted-replay", TimeSpan.FromMinutes(1), token));
        await Assert.ThrowsAsync<EventReplayFencedException>(async () => await events.ReplayAsync(oldReplayLease, ReplayHandleAsync, 3, 128, token));
        var replayed = await events.ReplayAsync(newReplayLease, ReplayHandleAsync, 17, 128, token);
        Assert.Equal(8, replayed.Checkpoint); Assert.Equal(5, replayed.HandledCount);
        await using (var connection = await promoted.OpenConnectionAsync(token))
        await using (var transaction = await connection.BeginTransactionAsync(token))
        {
            Assert.All(await events.AppendAsync(connection, transaction, eventStream, writes, token), static value => Assert.True(value.WasAlreadyStored));
            foreach (var value in await events.ReadAsync(eventStream, maximumEvents: 17, maximumPayloadBytes: 128, cancellationToken: token))
            { Assert.False(await events.ProcessInboxAsync(connection, transaction, "wal", value, HandleAsync, token)); }
            await transaction.CommitAsync(token);
        }
        var secondIdentity = new ProjectionIdentity("orders", 2, "recovery-v2", source);
        await store.RegisterWithLineageAsync(secondIdentity, newLineage, token);
        var secondLease = Assert.IsType<ProjectionLease>(await store.AcquireAsync(secondIdentity, "rebuild", TimeSpan.FromMinutes(1), token));
        var initialEvidence = await PostgreSqlProjectionLineage.CaptureForCutoverAsync(promoted, source, [publication], token);
        var recoveryId = Guid.NewGuid();
        var ticket = await store.BeginRecoveryAsync(secondIdentity, 1, initialEvidence, recoveryId, "Synchronous physical promotion; rebuild authoritative promoted source", token);
        Assert.Equal(ticket.MinimumSnapshotPosition, (await store.BeginRecoveryAsync(secondIdentity, 1, initialEvidence, recoveryId, ticket.Reason, token)).MinimumSnapshotPosition);
        Assert.Equal(ticket.MinimumSnapshotPosition, (await new PostgreSqlProjectionStore(routed, new() { Schema = schema }).ReadRecoveryAsync("orders", recoveryId, token))!.MinimumSnapshotPosition);
        Assert.False(await store.RenewAsync(firstLease, TimeSpan.FromMinutes(1), token));
        Assert.Null(await store.AcquireAsync(firstIdentity, "stale-reacquire", TimeSpan.FromMinutes(1), token));
        Assert.Equal(1, await store.ReadActiveVersionAsync("orders", token));
        await using var secondAttempt = await Snapshot(promoted, newLineage, publication).BeginAttemptAsync(null, token);
        var secondConsumer = new StreamsProjectionConsumer(store, secondLease, new OrdersProjection(secondIdentity));
        await BuildAsync(secondAttempt, secondConsumer, token);
        await using var secondWal = secondAttempt.CreateChangeStream().ReadTransactionsAsync(token).GetAsyncEnumerator(token);
        await SqlAsync(promoted, $"UPDATE \"{schema}\".customers SET name='Bob' WHERE tenant='first'; UPDATE \"{schema}\".orders SET amount='9' WHERE tenant='first'", token);
        var finalEvidence = await PostgreSqlProjectionLineage.CaptureForCutoverAsync(promoted, source, [publication], token);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.CompleteRecoveryAsync(secondLease, recoveryId, finalEvidence, token));
        Assert.True(await secondWal.MoveNextAsync());
        await using (var firstNewDelivery = secondWal.Current)
        {
            await Assert.ThrowsAsync<ProjectionFencedException>(async () => await store.ApplyAsync(firstLease, new OrdersProjection(firstIdentity), firstNewDelivery.Transaction, token));
            await secondConsumer.ConsumeTransactionAsync(firstNewDelivery, token);
        }
        await CoverBarrierAsync(secondWal, secondConsumer, finalEvidence.BarrierPosition, token);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.PromoteAsync(secondLease, finalEvidence.BarrierPosition, 1, token));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.PromoteWithLineageAsync(secondLease, finalEvidence.BarrierPosition, 1, finalEvidence, token));
        await store.CompleteRecoveryAsync(secondLease, recoveryId, finalEvidence, token);
        await store.CompleteRecoveryAsync(secondLease, recoveryId, finalEvidence, token);
        Assert.True((await store.ReadRecoveryAsync("orders", recoveryId, token))!.IsComplete);
        Assert.Equal(9m, (await store.ReadActiveAggregateAsync("orders", "first", "all", "total", token)).Value);
        Assert.Equal(999m, (await store.ReadActiveAggregateAsync("orders", "another", "all", "total", token)).Value);
        await AssertResetAsync(replacementLive, resume, protector, 2, token);
        // Fence before the source's ACCESS EXCLUSIVE DDL transaction, then rebuild from the new
        // immutable contract. Never apply old-slot DDL/WAL history with the replacement definition.
        var maintenanceId = Guid.NewGuid();
        await store.FenceForMaintenanceAsync("orders", 2, maintenanceId, "Add orders note column", token);
        await store.FenceForMaintenanceAsync("orders", 2, maintenanceId, "Add orders note column", token);
        Assert.False(await store.RenewAsync(secondLease, TimeSpan.FromMinutes(1), token));
        Assert.Null(await store.AcquireAsync(secondIdentity, "ddl-stale", TimeSpan.FromMinutes(1), token));
        await SqlAsync(promoted, $"ALTER TABLE \"{schema}\".orders ADD COLUMN note text NOT NULL DEFAULT 'migration'", token);
        var thirdSlot = "recovery_ddl_" + Guid.NewGuid().ToString("N");
        var thirdSource = new ChangeSourceIdentity(afterSystem.SystemIdentifier, afterSystem.DatabaseName!, thirdSlot, publication);
        var ddlEvidence = await PostgreSqlProjectionLineage.CaptureForCutoverAsync(promoted, thirdSource, [publication], token);
        Assert.NotEqual(finalEvidence.Lineage.Fingerprint, ddlEvidence.Lineage.Fingerprint);
        var thirdIdentity = new ProjectionIdentity("orders", 3, "recovery-v3", thirdSource);
        await store.RegisterWithLineageAsync(thirdIdentity, ddlEvidence.Lineage, token);
        var thirdLease = Assert.IsType<ProjectionLease>(await store.AcquireAsync(thirdIdentity, "ddl-rebuild", TimeSpan.FromMinutes(1), token));
        var ddlRecovery = Guid.NewGuid();
        await store.BeginRecoveryAsync(thirdIdentity, 2, ddlEvidence, ddlRecovery, "Controlled source DDL after durable maintenance fence", token);
        await using var thirdAttempt = await Snapshot(promoted, ddlEvidence.Lineage, publication).BeginAttemptAsync(null, token);
        var thirdConsumer = new StreamsProjectionConsumer(store, thirdLease, new OrdersProjection(thirdIdentity));
        await BuildAsync(thirdAttempt, thirdConsumer, token);
        await using var thirdWal = thirdAttempt.CreateChangeStream().ReadTransactionsAsync(token).GetAsyncEnumerator(token);
        await SqlAsync(promoted, $"DELETE FROM \"{schema}\".orders WHERE tenant='first'; INSERT INTO \"{schema}\".orders VALUES('2','first','customer','10','after migration')", token);
        var ddlFinal = await PostgreSqlProjectionLineage.CaptureForCutoverAsync(promoted, thirdSource, [publication], token);
        await CoverBarrierAsync(thirdWal, thirdConsumer, ddlFinal.BarrierPosition, token);
        await store.CompleteRecoveryAsync(thirdLease, ddlRecovery, ddlFinal, token);
        Assert.Null(await store.ReadActiveDocumentAsync("orders", "first", "1", token));
        Assert.NotNull(await store.ReadActiveDocumentAsync("orders", "first", "2", token));
        Assert.Equal(10m, (await store.ReadActiveAggregateAsync("orders", "first", "all", "total", token)).Value);
        Assert.Equal(999m, (await store.ReadActiveAggregateAsync("orders", "another", "all", "total", token)).Value);
        await AssertResetAsync(replacementLive, resume, protector, 3, token);
        Assert.Equal(16L, await ScalarAsync(promoted, $"SELECT count(*) FROM \"{eventSchema}\".effects WHERE consumer='wal'", token));
        Assert.Equal(8L, await ScalarAsync(promoted, $"SELECT count(*) FROM \"{eventSchema}\".effects WHERE consumer='replay'", token));
        var evidence = new
        {
            Scenario = "synchronous-physical-standby-promotion-and-controlled-ddl-rebuild",
            PostgreSqlMajor = 18,
            PostgreSqlImage = Environment.GetEnvironmentVariable("BLUETUSK_RECOVERY_IMAGE"),
            BeforeTimeline = beforeSystem.Timeline,
            AfterTimeline = afterSystem.Timeline,
            SameSystemIdentifier = true,
            SourceCommitDurability = "remote_apply with verified synchronous physical standby; primary killed before promotion",
            AcknowledgedBusinessCommits = 8,
            PreservedOutboxEvents = 16,
            ExactlyOnceWalInboxEffects = 16,
            RecoveredReplayEffects = 8,
            OldLiveFence = oldOwner.Lease.FencingToken,
            NewLiveFence = newOwner.Lease.FencingToken,
            OldCheckpointPreserved = true,
            StaleLiveAppendRejected = true,
            StaleProjectionReacquisitionRejected = true,
            ChangedTimelineBindingRejected = true,
            RecoveryCutovers = 2,
            TenantFirstTotal = 10,
            TenantAnotherTotal = 999,
            LogicalSlotsCopied = false,
            RecoveryTransparent = false,
            ControlledDdlBeforeFreshSnapshot = true,
            PromotionAndRecoveryElapsedMilliseconds = Stopwatch.GetElapsedTime(promotionStarted).TotalMilliseconds,
            TotalElapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
        };
        var json = JsonSerializer.Serialize(evidence, ReportJson); output.WriteLine(json);
        var report = Environment.GetEnvironmentVariable("BLUETUSK_RECOVERY_REPORT");
        if (!string.IsNullOrWhiteSpace(report)) { await File.WriteAllTextAsync(report, json, token); }
    }

    private static async Task AssertResetAsync(ProjectionLiveSubscription<OrderView> live, string resume, LiveResumeTokenProtector protector, int version, CancellationToken token)
    {
        var result = await live.ConnectWithTokenAsync(resume, protector, token);
        var reset = result.Connection!.Replay[^1];
        Assert.Equal("SchemaChanged", Initial(reset).GetProperty("resetReason").GetString());
        var row = Assert.Single(Initial(reset).GetProperty("rows").EnumerateArray());
        Assert.Equal(version, row.GetProperty("PublishedVersion").GetInt32());
        Assert.Equal("Bob", row.GetProperty("Value").GetProperty("CustomerName").GetString());
        await result.Connection.DisposeAsync();
    }
    private static PostgreSqlConsistentSnapshotSource Snapshot(BlueTuskDataSource dataSource, ProjectionSourceLineage lineage, string publication) => new(dataSource, new()
    {
        Source = lineage.Source,
        PublicationNames = [publication],
        MaximumBatchRows = 17,
        MaximumParallelTables = 2,
        Tables = lineage.Tables.Select(static table => new PostgreSqlSnapshotTable(table, table.Columns.Where(static column => column.IsKey).Select(static column => column.Ordinal))).ToArray()
    });
    private static async Task BuildAsync(IConsistentSnapshotAttempt snapshot, StreamsProjectionConsumer consumer, CancellationToken token)
    {
        await consumer.StartSnapshotAsync(new(snapshot.Epoch, snapshot.Tables.Count), token); long rows = 0;
        await foreach (var batch in snapshot.ReadSnapshotAsync(token)) { rows += batch.Rows.Count; await consumer.ConsumeSnapshotBatchAsync(batch, token); }
        await consumer.CompleteSnapshotAsync(new(snapshot.Epoch, rows, snapshot.Tables.Count), token);
    }
    private static async Task CoverXidAsync(IAsyncEnumerator<ChangeTransactionDelivery> wal, uint xid, Func<ChangeTransactionDelivery, Task> handle, CancellationToken token)
    {
        for (var count = 0; count < 128; count++)
        {
            Assert.True(await wal.MoveNextAsync()); await using var delivery = wal.Current;
            await handle(delivery); if (delivery.Transaction.TransactionId == xid) { return; }
            token.ThrowIfCancellationRequested();
        }
        Assert.Fail("A bounded real WAL stream failed to cover its acknowledged business transaction.");
    }
    private static async Task CoverBarrierAsync(IAsyncEnumerator<ChangeTransactionDelivery> wal, StreamsProjectionConsumer consumer, BlueTuskLogSequenceNumber barrier, CancellationToken token)
    {
        for (var count = 0; count < 128; count++)
        {
            Assert.True(await wal.MoveNextAsync()); await using var delivery = wal.Current; await consumer.ConsumeTransactionAsync(delivery, token);
            if (delivery.Transaction.CommitEndPosition >= barrier) { return; }
        }
        Assert.Fail("A bounded retained WAL stream failed to cover the verified recovery barrier.");
    }
    private static async Task<BlueTuskReplicationSystemIdentity> SystemAsync(BlueTuskDataSource dataSource, CancellationToken token)
    { await using var connection = await BlueTuskLogicalReplicationConnection.OpenAsync(dataSource.CreateDedicatedSessionOptions(), token); return await connection.IdentifySystemAsync(token); }
    private static async Task SqlAsync(DbDataSource source, string sql, CancellationToken token)
    { await using var connection = await source.OpenConnectionAsync(token); await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(token); }
    private static async Task<object?> ScalarAsync(DbDataSource source, string sql, CancellationToken token)
    { await using var connection = await source.OpenConnectionAsync(token); await using var command = connection.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync(token); }
    private static void Add(DbCommand command, string name, object value)
    { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter); }
    private static JsonElement Initial(LiveReplayEvent value)
    { using var document = JsonDocument.Parse(value.Payload); return document.RootElement.Clone(); }
    private static ChangeTable OutboxTable(string schema) => new(0, schema, "outbox", 'd', [new(0, "tenant_id", 25, -1, true), new(1, "stream_id", 25, -1, true), new(2, "sequence", 20, -1, true), new(3, "event_id", 2950, -1, false), new(4, "event_type", 25, -1, false), new(5, "version", 23, -1, false), new(6, "occurred_at", 1184, -1, false), new(7, "payload", 17, -1, false)]);
    private static async Task FixtureDockerAsync(string containerVariable, IReadOnlyList<string> beforeContainer, CancellationToken token, IReadOnlyList<string>? afterContainer = null)
    {
        var name = Environment.GetEnvironmentVariable(containerVariable) ?? throw new InvalidOperationException("Owned recovery fixture container is absent.");
        var fixture = Environment.GetEnvironmentVariable("BLUETUSK_RECOVERY_FIXTURE") ?? throw new InvalidOperationException("Owned recovery fixture label is absent.");
        var inspect = await DockerAsync(["inspect", name, "--format", "{{index .Config.Labels \"bluetusk.owner\"}}|{{index .Config.Labels \"bluetusk.fixture\"}}"], token);
        Assert.Equal("bluetusk.projections.recovery|" + fixture, inspect.Trim());
        await DockerAsync([.. beforeContainer, name, .. afterContainer ?? []], token);
    }
    private static async Task<string> DockerAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("BLUETUSK_RECOVERY_DOCKER") ?? "docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Docker fixture command failed to start.");
        var stdout = process.StandardOutput.ReadToEndAsync(token); var stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token); var errors = await stderr; var result = await stdout;
        if (process.ExitCode != 0) { throw new InvalidOperationException("Owned Docker fixture command failed: " + errors); }
        return result;
    }
    private sealed class RoutedDataSource(BlueTuskDataSource initial) : DbDataSource
    {
        private BlueTuskDataSource _target = initial;
        public BlueTuskDataSource Target { set => Volatile.Write(ref _target, value); }
        public override string ConnectionString => "Owned recovery endpoint; credentials omitted";
        protected override DbConnection CreateDbConnection() => Volatile.Read(ref _target).CreateConnection();
    }
}

[JsonSerializable(typeof(LiveResultEvent<ProjectionLiveRow<OrderView>, string>))]
internal sealed partial class RecoveryLiveJson : JsonSerializerContext
{
    internal static System.Text.Json.Serialization.Metadata.JsonTypeInfo<LiveResultEvent<ProjectionLiveRow<OrderView>, string>> EventTypeInfo =>
        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<LiveResultEvent<ProjectionLiveRow<OrderView>, string>>)Default.GetTypeInfo(typeof(LiveResultEvent<ProjectionLiveRow<OrderView>, string>))!;
}
