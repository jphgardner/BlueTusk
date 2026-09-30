using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BlueTusk.Events;
using BlueTusk.Projections.Live;
using BlueTusk.Replication;
using BlueTusk.Streams;

namespace BlueTusk.Projections.Tests;

public sealed class ProjectionsSustainedWorkloadTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RealWalTransactionalEventsOwnedLiveAndLeaseRecoveryMaintainExactStateDuringTimedWorkload()
    {
        var seconds = int.Parse(Environment.GetEnvironmentVariable("BLUETUSK_WORKLOAD_SECONDS") ?? "3", CultureInfo.InvariantCulture);
        Assert.InRange(seconds, 1, 120);
        await using var db = await ProjectionDatabase.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 45));
        var token = deadline.Token;
        var publication = "proj_steady_pub_" + Guid.NewGuid().ToString("N");
        var slot = "proj_steady_slot_" + Guid.NewGuid().ToString("N");
        var eventSchema = db.Schema + "_events";
        var events = new PostgreSqlEventStore(db.DataSource, new() { Schema = eventSchema, MaximumEventBytes = 128, MaximumAppendBytes = 512 });
        await events.InitializeAsync(token);
        await SqlAsync(db, $"""
            CREATE TABLE "{eventSchema}".effects(sequence bigint PRIMARY KEY);
            CREATE TABLE "{db.Schema}".customers(id text NOT NULL,tenant text NOT NULL,name text NOT NULL,PRIMARY KEY(tenant,id));
            CREATE TABLE "{db.Schema}".orders(id text NOT NULL,tenant text NOT NULL,customer text NOT NULL,amount text NOT NULL,PRIMARY KEY(tenant,id));
            ALTER TABLE "{db.Schema}".customers REPLICA IDENTITY FULL; ALTER TABLE "{db.Schema}".orders REPLICA IDENTITY FULL;
            INSERT INTO "{db.Schema}".customers VALUES('customer','first','Alice');
            INSERT INTO "{db.Schema}".orders VALUES('1','first','customer','0');
            CREATE PUBLICATION "{publication}" FOR TABLE "{db.Schema}".customers,"{db.Schema}".orders
            """, token);
        try
        {
            BlueTuskReplicationSystemIdentity system;
            await using (var identify = await BlueTuskLogicalReplicationConnection.OpenAsync(db.DataSource.CreateDedicatedSessionOptions(), token))
            { system = await identify.IdentifySystemAsync(token); }
            var source = new ChangeSourceIdentity(system.SystemIdentifier, system.DatabaseName!, slot, publication);
            var lineage = await PostgreSqlProjectionLineage.CaptureAsync(db.DataSource, source, [publication], token);
            var identity = new ProjectionIdentity("orders", 1, "steady-v1", source);
            await db.Store.RegisterWithLineageAsync(identity, lineage, token);
            var lease = Assert.IsType<ProjectionLease>(await db.Store.AcquireAsync(identity, "steady-before", TimeSpan.FromMinutes(5), token));
            var definition = new OrdersProjection(identity) { DependencyPageSize = 17 };
            var consumer = new StreamsProjectionConsumer(db.Store, lease, definition);
            var snapshotSource = new PostgreSqlConsistentSnapshotSource(db.DataSource, new()
            {
                Source = source,
                PublicationNames = [publication],
                MaximumBatchRows = 17,
                Tables = lineage.Tables.Select(static table => new PostgreSqlSnapshotTable(table, table.Columns.Where(static column => column.IsKey).Select(static column => column.Ordinal))).ToArray()
            });
            await using var snapshot = await snapshotSource.BeginAttemptAsync(null, token);
            await consumer.StartSnapshotAsync(new(snapshot.Epoch, 2), token);
            long rows = 0;
            await foreach (var batch in snapshot.ReadSnapshotAsync(token)) { rows += batch.Rows.Count; await consumer.ConsumeSnapshotBatchAsync(batch, token); }
            await consumer.CompleteSnapshotAsync(new(snapshot.Epoch, rows, 2), token);
            await db.Store.PromoteAsync(lease, snapshot.Epoch.ConsistentPosition, null, token);
            await using var wal = snapshot.CreateChangeStream().ReadTransactionsAsync(token).GetAsyncEnumerator(token);
            var stream = new EventStreamKey("first", "steady");
            var replayLease = Assert.IsType<EventReplayLease>(await events.AcquireReplayAsync("steady", stream, "steady-before", TimeSpan.FromMinutes(5), token));
            var query = new ProjectionLiveQuery<OrderView>(db.Store, "orders", "first", new("tenant:first", "v1"), "steady", "db", "v1", ProjectionJson.Default.OrderView);
            var ownedReplay = new PostgreSqlProjectionLiveReplayStore(db.DataSource, new() { Schema = db.Schema });
            await ownedReplay.InitializeAsync(token);
            await using var querySession = query.CreateSession();
            var publisher = Assert.IsType<ProjectionLivePublisher>(await ownedReplay.AcquireAsync(querySession.Identity, "steady-live", TimeSpan.FromSeconds(30), token));
            await using var live = new ProjectionLiveSubscription<OrderView>(query, publisher, ProjectionLiveJson.EventTypeInfo);
            await live.StartAsync(token);
            var connected = await live.ConnectAsync(0, token);
            await using var client = connected.Connection!.ReadAllAsync(token).GetAsyncEnumerator(token);
            var durations = new List<double>();
            var producerDurations = new List<double>();
            var projectionDurations = new List<double>();
            var eventDurations = new List<double>();
            var gc = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
            var allocated = GC.GetTotalAllocatedBytes(false);
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var maximumWorkingSet = process.WorkingSet64;
            var count = 0; var sourceDeliveries = 0; var recoveryCount = 0; var duplicateAppendCount = 0; var liveMessages = 0;
            long measuredReplayEffects = 0, measuredProjectionCommits = 0;
            using var listener = new System.Diagnostics.Metrics.MeterListener
            {
                InstrumentPublished = static (instrument, l) =>
                { if (instrument.Name is "bluetusk.events.replay.committed" or "bluetusk.projections.commits") { l.EnableMeasurementEvents(instrument); } }
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            {
                if (instrument.Name == "bluetusk.events.replay.committed") { Interlocked.Add(ref measuredReplayEffects, value); }
                else { Interlocked.Add(ref measuredProjectionCommits, value); }
            });
            listener.Start();
            var started = Stopwatch.GetTimestamp();
            async ValueTask HandleAsync(StoredEvent value, System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction, CancellationToken cancellationToken)
            {
                await using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = $"INSERT INTO \"{eventSchema}\".effects VALUES(@sequence)";
                var parameter = command.CreateParameter(); parameter.ParameterName = "sequence"; parameter.Value = value.Sequence; command.Parameters.Add(parameter);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            async Task<uint> CommitBusinessAsync(int amount, EventWrite value, bool duplicate)
            {
                await using var connection = await db.DataSource.OpenConnectionAsync(token);
                await using var transaction = await connection.BeginTransactionAsync(token);
                await using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = $"UPDATE \"{db.Schema}\".orders SET amount=@amount WHERE tenant='first' AND id='1'";
                var parameter = command.CreateParameter(); parameter.ParameterName = "amount"; parameter.Value = amount.ToString(CultureInfo.InvariantCulture); command.Parameters.Add(parameter);
                Assert.Equal(1, await command.ExecuteNonQueryAsync(token));
                Assert.Equal(duplicate, Assert.Single(await events.AppendAsync(connection, transaction, stream, [value], token)).WasAlreadyStored);
                command.CommandText = "SELECT pg_current_xact_id()::text";
                command.Parameters.Clear();
                var xid = unchecked((uint)ulong.Parse((string)(await command.ExecuteScalarAsync(token))!, CultureInfo.InvariantCulture));
                await transaction.CommitAsync(token);
                return xid;
            }
            async Task CoverBusinessAsync(uint xid, bool recover)
            {
                // Transactional logical messages are not publication-filtered. Other isolated tests
                // may emit known barriers on this same database; drain them rather than mistaking
                // the next delivered transaction for this business commit.
                for (var pending = 0; pending < 1024; pending++)
                {
                    Assert.True(await wal.MoveNextAsync());
                    await using var delivery = wal.Current;
                    await consumer.ConsumeTransactionAsync(delivery, token); sourceDeliveries++;
                    if (delivery.Transaction.TransactionId != xid) { continue; }
                    if (recover)
                    {
                        Assert.True(await db.Store.ReleaseAsync(lease, token));
                        lease = Assert.IsType<ProjectionLease>(await db.Store.AcquireAsync(identity, "steady-recovered-" + count.ToString(CultureInfo.InvariantCulture), TimeSpan.FromMinutes(5), token));
                        consumer = new(db.Store, lease, definition);
                        Assert.False((await db.Store.ApplyAsync(lease, definition, delivery.Transaction, token)).WasApplied);
                        recoveryCount++;
                    }
                    return;
                }
                Assert.Fail("Bounded retained source deliveries did not cover this committed business transaction.");
            }
            while (Stopwatch.GetElapsedTime(started).TotalSeconds < seconds && count < 10_000)
            {
                var iteration = Stopwatch.GetTimestamp(); count++;
                var value = new EventWrite(Guid.NewGuid(), "steady.order", 1, DateTimeOffset.UtcNow, Encoding.UTF8.GetBytes(count.ToString(CultureInfo.InvariantCulture)));
                var stage = Stopwatch.GetTimestamp();
                var xid = await CommitBusinessAsync(count, value, false);
                producerDurations.Add(Stopwatch.GetElapsedTime(stage).TotalMilliseconds);
                stage = Stopwatch.GetTimestamp();
                await CoverBusinessAsync(xid, count % 61 == 0);
                projectionDurations.Add(Stopwatch.GetElapsedTime(stage).TotalMilliseconds);
                if (count % 37 == 0)
                {
                    var duplicateXid = await CommitBusinessAsync(count, value, true); duplicateAppendCount++;
                    await CoverBusinessAsync(duplicateXid, false);
                    var previous = replayLease;
                    Assert.True(await events.ReleaseReplayAsync(previous, token));
                    replayLease = Assert.IsType<EventReplayLease>(await events.AcquireReplayAsync("steady", stream, "steady-recovered-" + count.ToString(CultureInfo.InvariantCulture), TimeSpan.FromMinutes(5), token));
                    await Assert.ThrowsAsync<EventReplayFencedException>(async () => await events.ReplayAsync(previous, HandleAsync, 16, 512, token));
                    recoveryCount++;
                }
                stage = Stopwatch.GetTimestamp();
                Assert.Equal(1, (await events.ReplayAsync(replayLease, HandleAsync, 16, 512, token)).HandledCount);
                eventDurations.Add(Stopwatch.GetElapsedTime(stage).TotalMilliseconds);
                if (count % 32 == 0)
                {
                    Assert.Equal(1, await live.RefreshAsync(token));
                    Assert.True(await client.MoveNextAsync()); liveMessages++;
                }
                durations.Add(Stopwatch.GetElapsedTime(iteration).TotalMilliseconds);
                process.Refresh(); maximumWorkingSet = Math.Max(maximumWorkingSet, process.WorkingSet64);
            }
            if (count % 32 != 0) { Assert.Equal(1, await live.RefreshAsync(token)); Assert.True(await client.MoveNextAsync()); liveMessages++; }
            var elapsed = Stopwatch.GetElapsedTime(started);
            Assert.True(count > 0);
            Assert.Equal(count, await events.ReadCheckpointAsync("steady", stream, token));
            Assert.Equal(0, (await events.ReplayAsync(replayLease, HandleAsync, 16, 512, token)).HandledCount);
            Assert.Equal(count, (await db.Store.ReadActiveAggregateAsync("orders", "first", "all", "total", token)).Value);
            Assert.Equal(count, (await db.ReadAsync())!.Amount);
            Assert.Equal(sourceDeliveries + 1, (await db.Store.ReadPublicationAsync("orders", token)).Revision);
            await using (var connection = await db.DataSource.OpenConnectionAsync(token))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT count(*),sum(sequence),min(sequence),max(sequence),current_setting('server_version') FROM \"{eventSchema}\".effects";
                await using var reader = await command.ExecuteReaderAsync(token); Assert.True(await reader.ReadAsync(token));
                Assert.Equal(count, reader.GetInt64(0)); Assert.Equal((decimal)count * (count + 1) / 2, reader.GetDecimal(1));
                Assert.Equal(1, reader.GetInt64(2)); Assert.Equal(count, reader.GetInt64(3));
                var report = new ArrayBufferWriter<byte>();
                using var writer = new Utf8JsonWriter(report, new() { Indented = true });
                writer.WriteStartObject(); writer.WriteString("postgresql", reader.GetString(4)); writer.WriteNumber("elapsedSeconds", elapsed.TotalSeconds);
                writer.WriteString("scope", "one bounded serial producer: real SQL/business+outbox commit, real pgoutput projection, transactional inbox, owned Live refresh every32commits; not a production capacity claim");
                writer.WriteNumber("businessTransactions", count); writer.WriteNumber("sourceDeliveries", sourceDeliveries); writer.WriteNumber("transactionsPerSecond", count / elapsed.TotalSeconds);
                writer.WriteNumber("leaseRecoveries", recoveryCount); writer.WriteNumber("duplicateAppends", duplicateAppendCount); writer.WriteNumber("liveMessages", liveMessages);
                WriteLatency(writer, "endToEnd", durations); WriteLatency(writer, "businessAndOutboxCommit", producerDurations); WriteLatency(writer, "walAndProjection", projectionDurations); WriteLatency(writer, "transactionalInbox", eventDurations);
                writer.WriteNumber("processAllocatedBytes", GC.GetTotalAllocatedBytes(false) - allocated); writer.WriteNumber("maximumWorkingSetBytes", maximumWorkingSet);
                writer.WriteNumber("processCpuMilliseconds", (process.TotalProcessorTime - cpu).TotalMilliseconds);
                for (var generation = 0; generation < 3; generation++) { writer.WriteNumber("gen" + generation.ToString(CultureInfo.InvariantCulture) + "Collections", GC.CollectionCount(generation) - gc[generation]); }
                writer.WriteNumber("processReplayCommittedMeasurements", Interlocked.Read(ref measuredReplayEffects)); writer.WriteNumber("processProjectionCommitMeasurements", Interlocked.Read(ref measuredProjectionCommits));
                writer.WriteString("invariants", "exact inbox count/sum/contiguous checkpoints; exact published aggregate/document/revision; bounded outstanding work; stale leases rejected; duplicate retries suppressed");
                writer.WriteEndObject(); writer.Flush();
                output.WriteLine(Encoding.UTF8.GetString(report.WrittenSpan));
                var reportPath = Environment.GetEnvironmentVariable("BLUETUSK_WORKLOAD_REPORT");
                if (!string.IsNullOrWhiteSpace(reportPath)) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!); await File.WriteAllBytesAsync(reportPath, report.WrittenMemory.ToArray(), token); }
            }
            await connected.Connection.DisposeAsync();
        }
        finally
        {
            await SqlAsync(db, $"SELECT pg_drop_replication_slot(slot_name) FROM pg_replication_slots WHERE slot_name='{slot}'; DROP PUBLICATION IF EXISTS \"{publication}\"; DROP SCHEMA IF EXISTS \"{eventSchema}\" CASCADE", CancellationToken.None);
        }
    }

    private static void WriteLatency(Utf8JsonWriter writer, string name, List<double> values)
    {
        values.Sort(); writer.WriteStartObject(name);
        writer.WriteNumber("p50Milliseconds", values[(int)Math.Ceiling(values.Count * .5) - 1]); writer.WriteNumber("p95Milliseconds", values[(int)Math.Ceiling(values.Count * .95) - 1]); writer.WriteNumber("p99Milliseconds", values[(int)Math.Ceiling(values.Count * .99) - 1]);
        writer.WriteNumber("maximumMilliseconds", values[^1]); writer.WriteEndObject();
    }
    private static async Task SqlAsync(ProjectionDatabase db, string sql, CancellationToken token)
    { await using var connection = await db.DataSource.OpenConnectionAsync(token); await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(token); }
}
