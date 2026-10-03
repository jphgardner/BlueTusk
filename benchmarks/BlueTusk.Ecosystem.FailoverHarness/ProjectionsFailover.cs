using System.Globalization;
using System.Text;
using BlueTusk.Data;
using BlueTusk.Projections;
using BlueTusk.Replication;
using BlueTusk.Replication.PgOutput;
using BlueTusk.Streams;

namespace BlueTusk.Ecosystem.FailoverHarness;

/// <summary>
/// Projections delivers source WAL at least once and applies it exactly once: the derived model,
/// aggregates and checkpoint commit in one transaction and the checkpoint deduplicates redelivery.
/// Each disturbance interrupts a worker while it is inside the apply transaction of an
/// acknowledged source commit, after it already staged an aggregate delta. Recovery must roll
/// that partial apply back, fence the old lease owner, resume from the durable checkpoint and
/// converge every tenant's count and total aggregates to the exact source truth: a lost apply
/// lowers the count and a duplicated apply raises it. Physical promotion is qualified separately
/// by the dedicated BlueTusk.Projections.PhysicalRecoveryTests rehearsal.
/// </summary>
internal static class ProjectionsFailover
{
    private const string Application = "BlueTuskFailoverProjections";
    private const string ChildApplication = "BlueTuskFailoverProjectionsChild";
    private const string Semantics = "at-least-once-delivery; exactly-once-apply-by-durable-checkpoint; fenced-lease";
    private const string InFlightId = "inflight";
    private const int Rows = 16;
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Heartbeat = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Phase = TimeSpan.FromSeconds(180);
    private static readonly string[] Tenants = ["tenant-a", "tenant-b"];

    internal static async Task<IReadOnlyList<ScenarioResult>> RunAsync(FailoverFixture fixture, CancellationToken token) =>
    [
        await InProcessAsync(fixture, "backend-termination", "pg_terminate_backend of the worker session holding the in-flight apply transaction", crash: false, token),
        await HostProcessKillAsync(fixture, token),
        await InProcessAsync(fixture, "primary-crash-restart", "SIGKILL of the PostgreSQL primary during an in-flight apply, then restart of the same server", crash: true, token),
    ];

    private static async Task<ScenarioResult> InProcessAsync(FailoverFixture fixture, string name, string fault, bool crash, CancellationToken token)
    {
        var recorder = new ScenarioRecorder(name, fault, Semantics, Tenants.Length);
        await using var setup = await LedgerSetup.CreateAsync(fixture, recorder, token);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = new LedgerProjection(setup.Identity, async () => { blocked.TrySetResult(); await gate.Task; });
        var oldLease = await setup.Store.AcquireAsync(setup.Identity, "worker-before-fault", Lease, token);
        FailoverFixture.Check(oldLease is not null, "initial worker lease");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = LedgerWorker.RunAsync(setup, oldLease!, definition, ready, token);
        await ready.Task.WaitAsync(Phase, token);
        recorder.Acknowledged = await setup.CommitRowsAsync(token);
        await setup.WaitConvergedAsync(Rows, Phase, token);
        await setup.CommitInFlightAsync(token);
        recorder.Acknowledged++;
        await blocked.Task.WaitAsync(Phase, token);
        await fixture.WaitForIdleInTransactionAsync(Application, Phase, token);
        recorder.MarkFault();
        if (crash)
        {
            await FailoverFixture.KillPrimaryAsync(token);
        }
        else
        {
            recorder.Check(await fixture.TerminateIdleInTransactionAsync(Application, token) == 1, "in-flight apply backend terminated");
        }

        gate.TrySetResult();
        recorder.Check(await FailedAsync(worker), "interrupted worker stopped with a failure");
        if (crash)
        {
            await FailoverFixture.StartPrimaryAsync(token);
        }

        await setup.RequireRolledBackAsync(recorder, token);
        var replacementReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProjectionLease? newLease = null;
        Task? replacement = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        recorder.FirstSuccessMilliseconds = await FailoverFixture.FirstSuccessAsync(recorder.FaultTimestamp, async () =>
        {
            newLease ??= await setup.Store.AcquireAsync(setup.Identity, "worker-after-fault", Lease, token)
                ?? throw new InvalidOperationException("The previous lease has not expired yet.");
            replacement ??= LedgerWorker.RunAsync(setup, newLease, new LedgerProjection(setup.Identity, null), replacementReady, stop.Token);
            await setup.WaitConvergedAsync(Rows + 1, TimeSpan.FromSeconds(1), token);
        }, Phase, "replacement projection worker", token);
        if (crash)
        {
            await fixture.RequireSynchronousAsync(Phase, token);
            recorder.Check(true, "synchronous standby reattached after restart");
        }

        recorder.BeforeFence = oldLease!.FencingToken;
        recorder.AfterFence = newLease!.FencingToken;
        recorder.Check(recorder.AfterFence > recorder.BeforeFence, "replacement owner holds a newer fence");
        recorder.StaleOwnerRejected = !await setup.Store.RenewAsync(oldLease, Lease, token);
        recorder.Check(recorder.StaleOwnerRejected, "stale owner cannot renew");
        await setup.VerifyAsync(recorder, Rows + 1, token);
        await stop.CancelAsync();
        await StoppedAsync(replacement!);
        recorder.After = await fixture.IdentifyAsync(token);
        recorder.Check(recorder.After.SystemIdentifier == recorder.Before!.SystemIdentifier && recorder.After.Timeline == recorder.Before.Timeline, "same server and timeline");
        return recorder.Complete();
    }

    private static async Task<ScenarioResult> HostProcessKillAsync(FailoverFixture fixture, CancellationToken token)
    {
        var recorder = new ScenarioRecorder("host-process-kill", "operating-system kill of the worker process while its apply transaction is open", Semantics, Tenants.Length);
        await using var setup = await LedgerSetup.CreateAsync(fixture, recorder, token);
        await using var child = ChildProcess.Start("Projections", "worker", setup.SourceSchema, setup.ProjectionSchema, setup.Publication, setup.Slot);
        string[] lease = (await child.ReadLineAsync(Phase, token)).Split(' ');
        FailoverFixture.Check(lease is ["LEASE", _, _], "the child reported its issued lease");
        var childLease = new ProjectionLease(setup.Identity, lease[1], long.Parse(lease[2], CultureInfo.InvariantCulture));
        await child.ExpectAsync("READY", Phase, token);
        recorder.Acknowledged = await setup.CommitRowsAsync(token);
        await setup.WaitConvergedAsync(Rows, Phase, token);
        await setup.CommitInFlightAsync(token);
        recorder.Acknowledged++;
        await child.ExpectAsync("BLOCKED", Phase, token);
        await fixture.WaitForIdleInTransactionAsync(ChildApplication, Phase, token);
        recorder.MarkFault();
        await child.KillAsync(token);
        recorder.Check(true, "worker process hard-killed inside its apply transaction");
        await fixture.WaitForNoSessionsAsync(ChildApplication, Phase, token);
        await setup.RequireRolledBackAsync(recorder, token);
        var replacementReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProjectionLease? newLease = null;
        Task? replacement = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        recorder.FirstSuccessMilliseconds = await FailoverFixture.FirstSuccessAsync(recorder.FaultTimestamp, async () =>
        {
            newLease ??= await setup.Store.AcquireAsync(setup.Identity, "worker-after-kill", Lease, token)
                ?? throw new InvalidOperationException("The killed owner's lease has not expired yet.");
            replacement ??= LedgerWorker.RunAsync(setup, newLease, new LedgerProjection(setup.Identity, null), replacementReady, stop.Token);
            await setup.WaitConvergedAsync(Rows + 1, TimeSpan.FromSeconds(1), token);
        }, Phase, "replacement projection worker", token);
        recorder.BeforeFence = childLease.FencingToken;
        recorder.AfterFence = newLease!.FencingToken;
        recorder.Check(recorder.AfterFence > recorder.BeforeFence, "replacement owner holds a newer fence");
        recorder.StaleOwnerRejected = !await setup.Store.RenewAsync(childLease, Lease, token);
        recorder.Check(recorder.StaleOwnerRejected, "killed owner's issued lease cannot renew");
        await setup.VerifyAsync(recorder, Rows + 1, token);
        await stop.CancelAsync();
        await StoppedAsync(replacement!);
        recorder.After = await fixture.IdentifyAsync(token);
        recorder.Check(recorder.After.SystemIdentifier == recorder.Before!.SystemIdentifier && recorder.After.Timeline == recorder.Before.Timeline, "same server and timeline");
        return recorder.Complete();
    }

    internal static async Task RunChildAsync(string role, string[] arguments, CancellationToken token)
    {
        FailoverFixture.Check(role == "worker" && arguments.Length == 4 && arguments.All(static value => value.StartsWith("fo_projections_", StringComparison.Ordinal) &&
            value.Length <= 63 && value["fo_projections_".Length..].All(static character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '_')),
            "parent-generated Projections fixture names");
        await using var fixture = new FailoverFixture();
        await using var setup = await LedgerSetup.OpenAsync(fixture, ChildApplication, arguments[0], arguments[1], arguments[2], arguments[3], token);
        await setup.Store.InitializeAsync(token);
        string owner = "child-" + Guid.NewGuid().ToString("N");
        var lease = await setup.Store.AcquireAsync(setup.Identity, owner, Lease, token);
        FailoverFixture.Check(lease is not null, "child worker lease");
        Console.WriteLine("LEASE " + owner + " " + lease!.FencingToken.ToString(CultureInfo.InvariantCulture));
        await Console.Out.FlushAsync(token);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = new LedgerProjection(setup.Identity, async () =>
        {
            Console.WriteLine("BLOCKED");
            await Console.Out.FlushAsync(token);
            await Task.Delay(Timeout.Infinite, token);
        });
        var worker = LedgerWorker.RunAsync(setup, lease, definition, ready, token);
        await ready.Task.WaitAsync(Phase, token);
        Console.WriteLine("READY");
        await Console.Out.FlushAsync(token);
        await worker;
    }

    private static async Task<bool> FailedAsync(Task worker)
    {
        try
        {
            await worker.WaitAsync(Phase);
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception exception) when (exception is not HarnessCheckException)
        {
            return true;
        }
    }

    private static async Task StoppedAsync(Task worker)
    {
        try { await worker.WaitAsync(Phase); }
        catch (OperationCanceledException) { }
    }

    /// <summary>One owned source table, publication, logical slot and projection schema.</summary>
    private sealed class LedgerSetup : IAsyncDisposable
    {
        private readonly FailoverFixture _fixture;

        private LedgerSetup(FailoverFixture fixture, BlueTuskDataSource source, string sourceSchema, string projectionSchema,
            string publication, string slot, ProjectionIdentity identity, ProjectionSourceLineage lineage)
        {
            _fixture = fixture;
            Source = source;
            SourceSchema = sourceSchema;
            ProjectionSchema = projectionSchema;
            Publication = publication;
            Slot = slot;
            Identity = identity;
            Lineage = lineage;
            Store = new PostgreSqlProjectionStore(source, new() { Schema = projectionSchema });
        }

        internal BlueTuskDataSource Source { get; }
        internal string SourceSchema { get; }
        internal string ProjectionSchema { get; }
        internal string Publication { get; }
        internal string Slot { get; }
        internal ProjectionIdentity Identity { get; }
        internal ProjectionSourceLineage Lineage { get; }
        internal PostgreSqlProjectionStore Store { get; }

        internal static async Task<LedgerSetup> CreateAsync(FailoverFixture fixture, ScenarioRecorder recorder, CancellationToken token)
        {
            await fixture.RequireSynchronousAsync(Phase, token);
            recorder.Check(true, "remote_apply synchronous standby before fault");
            recorder.Before = await fixture.IdentifyAsync(token);
            string suffix = Guid.NewGuid().ToString("N")[..24];
            string sourceSchema = "fo_projections_src_" + suffix;
            string publication = "fo_projections_pub_" + suffix;
            await FailoverFixture.ExecuteAsync(fixture.Admin, $"""
                CREATE SCHEMA "{sourceSchema}";
                CREATE TABLE "{sourceSchema}".ledger(tenant text NOT NULL, id text NOT NULL, amount bigint NOT NULL, PRIMARY KEY(tenant, id));
                ALTER TABLE "{sourceSchema}".ledger REPLICA IDENTITY FULL;
                CREATE PUBLICATION "{publication}" FOR TABLE "{sourceSchema}".ledger;
                """, token);
            var setup = await OpenAsync(fixture, Application, sourceSchema, "fo_projections_dst_" + suffix, publication, "fo_projections_slot_" + suffix, token);
            await setup.Store.InitializeAsync(token);
            await setup.Store.RegisterWithLineageAsync(setup.Identity, setup.Lineage, token);
            return setup;
        }

        internal static async Task<LedgerSetup> OpenAsync(FailoverFixture fixture, string application, string sourceSchema, string projectionSchema,
            string publication, string slot, CancellationToken token)
        {
            // Logical replication slots live on the primary; the worker uses the primary endpoint directly.
            var source = BlueTuskDataSource.Create(new BlueTuskConnectionStringBuilder(fixture.PrimaryConnection)
            { ApplicationName = application, MaximumPoolSize = 6, MinimumPoolSize = 0, Timeout = TimeSpan.FromSeconds(1) }.ConnectionString);
            ChangeSourceIdentity sourceIdentity;
            await using (var identify = await BlueTuskLogicalReplicationConnection.OpenAsync(source.CreateDedicatedSessionOptions(), token))
            {
                var system = await identify.IdentifySystemAsync(token);
                sourceIdentity = new(system.SystemIdentifier, system.DatabaseName!, slot, publication);
            }

            var identity = new ProjectionIdentity("ledger", 1, "failover-ledger-v1", sourceIdentity);
            var lineage = await PostgreSqlProjectionLineage.CaptureAsync(source, sourceIdentity, [publication], token);
            return new(fixture, source, sourceSchema, projectionSchema, publication, slot, identity, lineage);
        }

        internal async Task<int> CommitRowsAsync(CancellationToken token)
        {
            for (int row = 0; row < Rows; row++)
            {
                await FailoverFixture.ExecuteAsync(Source, $"INSERT INTO \"{SourceSchema}\".ledger VALUES('{Tenants[row % 2]}','row-{row.ToString("D2", CultureInfo.InvariantCulture)}',{(row + 1).ToString(CultureInfo.InvariantCulture)})", token);
            }

            return Rows;
        }

        internal Task CommitInFlightAsync(CancellationToken token) =>
            FailoverFixture.ExecuteAsync(Source, $"INSERT INTO \"{SourceSchema}\".ledger VALUES('{Tenants[0]}','{InFlightId}',1000)", token);

        internal async Task WaitConvergedAsync(int rows, TimeSpan deadline, CancellationToken token) =>
            await FailoverFixture.WaitUntilAsync(async () => await CountAsync(token) == rows, deadline, "projection converged to " + rows.ToString(CultureInfo.InvariantCulture) + " source rows", token);

        private async Task<decimal> CountAsync(CancellationToken token)
        {
            decimal count = 0;
            foreach (string tenant in Tenants) { count += (await Store.ReadActiveAggregateAsync("ledger", tenant, "all", "count", token)).Value; }
            return count;
        }

        /// <summary>The interrupted apply had already staged an aggregate delta; it must have rolled back whole.</summary>
        internal async Task RequireRolledBackAsync(ScenarioRecorder recorder, CancellationToken token)
        {
            await FailoverFixture.WaitUntilAsync(async () => await CountAsync(token) >= 0, Phase, "projection store readable after the fault", token);
            bool atomic = await CountAsync(token) == Rows && await Store.ReadActiveDocumentAsync("ledger", Tenants[0], InFlightId, token) is null;
            recorder.InFlightAtomic = atomic;
            recorder.Check(atomic, "interrupted apply rolled back whole, including its staged aggregate delta");
        }

        internal async Task VerifyAsync(ScenarioRecorder recorder, int expectedRows, CancellationToken token)
        {
            int verified = 0;
            bool isolated = true;
            long observed = 0;
            await using (var connection = await Source.OpenConnectionAsync(token))
            await using (var command = new BlueTuskCommand($"SELECT tenant, id, amount FROM \"{SourceSchema}\".ledger ORDER BY tenant, id", connection))
            await using (var reader = await command.ExecuteReaderAsync(token))
            {
                var rows = new List<(string Tenant, string Id, long Amount)>();
                while (await reader.ReadAsync(token)) { rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2))); }
                await reader.DisposeAsync();
                foreach (var (tenant, id, amount) in rows)
                {
                    var document = await Store.ReadActiveDocumentAsync("ledger", tenant, id, token);
                    if (document is not null && Encoding.UTF8.GetString(document.Payload.Span) == Payload(tenant, id, amount)) { verified++; }
                    isolated &= await Store.ReadActiveDocumentAsync("ledger", tenant == Tenants[0] ? Tenants[1] : Tenants[0], id, token) is null;
                }

                foreach (string tenant in Tenants)
                {
                    decimal total = (await Store.ReadActiveAggregateAsync("ledger", tenant, "all", "total", token)).Value;
                    decimal count = (await Store.ReadActiveAggregateAsync("ledger", tenant, "all", "count", token)).Value;
                    recorder.Check(total == rows.Where(row => row.Tenant == tenant).Sum(static row => row.Amount) &&
                        count == rows.Count(row => row.Tenant == tenant), "every tenant total and count equals the source exactly");
                    observed += (long)count;
                }

                recorder.Check(rows.Count == expectedRows, "every acknowledged source commit is present");
            }

            recorder.Verified = verified;
            recorder.Check(verified == expectedRows, "every acknowledged source row is projected with its exact value");
            recorder.InFlightCommitted = await Store.ReadActiveDocumentAsync("ledger", Tenants[0], InFlightId, token) is not null;
            recorder.Check(recorder.InFlightCommitted, "the interrupted source commit was applied after recovery");
            recorder.NoCrossTenantReads = isolated;
            recorder.Check(isolated, "no cross-tenant reads");
            recorder.ExpectedEffects = expectedRows;
            recorder.ObservedEffects = observed;
            recorder.Check(observed == expectedRows, "no lost or duplicated applies");
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await FailoverFixture.WaitUntilAsync(async () => await FailoverFixture.ScalarAsync<long>(_fixture.Admin,
                    $"SELECT count(*) FROM pg_replication_slots WHERE slot_name='{Slot}' AND active", CancellationToken.None) == 0,
                    TimeSpan.FromSeconds(30), "projection slot released", CancellationToken.None);
                await FailoverFixture.ExecuteAsync(_fixture.Admin,
                    $"SELECT pg_drop_replication_slot(slot_name) FROM pg_replication_slots WHERE slot_name='{Slot}'", CancellationToken.None);
            }
            catch (Exception exception) when (exception is BlueTuskException or HarnessCheckException or IOException or InvalidOperationException)
            {
                // A destroyed fixture drops its slots with its volume; the runner removes every owned volume.
            }

            await Source.DisposeAsync();
        }
    }

    internal static string Payload(string tenant, string id, long amount) =>
        "{\"tenant\":\"" + tenant + "\",\"id\":\"" + id + "\",\"amount\":" + amount.ToString(CultureInfo.InvariantCulture) + "}";

    /// <summary>A ledger whose count and total aggregates are additive, so any lost or repeated apply is visible.</summary>
    private sealed class LedgerProjection(ProjectionIdentity identity, Func<Task>? interrupt) : IProjectionDefinition
    {
        public ProjectionIdentity Identity { get; } = identity;

        public async ValueTask ApplySnapshotAsync(ChangeSnapshotBatch batch, ProjectionWriteContext context, CancellationToken cancellationToken)
        {
            foreach (var value in batch.Rows) { await ApplyRowAsync(value.Row, context, cancellationToken); }
        }

        public async ValueTask ApplyTransactionAsync(ChangeTransaction transaction, ProjectionWriteContext context, CancellationToken cancellationToken)
        {
            await foreach (var change in transaction.Changes.WithCancellation(cancellationToken))
            {
                switch (change)
                {
                    case InsertChange insert: await ApplyRowAsync(insert.NewRow, context, cancellationToken); break;
                    case LogicalMessageChange: break;
                    default: throw new InvalidOperationException("The failover ledger is insert-only.");
                }
            }
        }

        private async ValueTask ApplyRowAsync(ChangeRow row, ProjectionWriteContext context, CancellationToken cancellationToken)
        {
            string tenant = Text(row, "tenant");
            string id = Text(row, "id");
            long amount = long.Parse(Text(row, "amount"), CultureInfo.InvariantCulture);
            await context.AddAggregateAsync(tenant, "all", "count", 1, cancellationToken);
            await context.AddAggregateAsync(tenant, "all", "total", amount, cancellationToken);
            if (id == InFlightId && interrupt is not null) { await interrupt(); }
            await context.UpsertAsync(tenant, id, Encoding.UTF8.GetBytes(Payload(tenant, id, amount)), [], cancellationToken);
        }

        private static string Text(ChangeRow row, string column)
        {
            var value = row[column];
            FailoverFixture.Check(value.State == ChangeColumnState.Value, "full published source values");
            return Encoding.UTF8.GetString(value.Data.Span);
        }
    }

    /// <summary>The documented worker loop: initial snapshot and publication, or resume from the durable checkpoint.</summary>
    private static class LedgerWorker
    {
        internal static async Task RunAsync(LedgerSetup setup, ProjectionLease lease, IProjectionDefinition definition,
            TaskCompletionSource ready, CancellationToken token)
        {
            await Task.Yield();
            using var run = CancellationTokenSource.CreateLinkedTokenSource(token);
            var renew = RenewAsync(setup.Store, lease, run);
            try
            {
                var consumer = new StreamsProjectionConsumer(setup.Store, lease, definition);
                var state = await setup.Store.ReadStateAsync(setup.Identity, run.Token);
                if (state.Phase != ProjectionBuildPhase.CatchingUp)
                {
                    var snapshot = new PostgreSqlConsistentSnapshotSource(setup.Source, new PostgreSqlConsistentSnapshotOptions
                    {
                        Source = setup.Identity.Source,
                        PublicationNames = [setup.Publication],
                        Tables = setup.Lineage.Tables.Select(static table => new PostgreSqlSnapshotTable(table, table.Columns.Where(static column => column.IsKey).Select(static column => column.Ordinal))).ToArray(),
                        MaximumParallelTables = 1,
                        MaximumBatchRows = 64,
                        ExistingSlotMode = PostgreSqlExistingSnapshotSlotMode.Fail,
                    }, connection => new FeedbackObserver(connection));
                    await using var attempt = await snapshot.BeginAttemptAsync(state.SnapshotEpoch, run.Token);
                    await consumer.StartSnapshotAsync(new(attempt.Epoch, setup.Lineage.Tables.Count), run.Token);
                    long rows = 0;
                    await foreach (var batch in attempt.ReadSnapshotAsync(run.Token))
                    {
                        await consumer.ConsumeSnapshotBatchAsync(batch, run.Token);
                        rows += batch.Rows.Count;
                    }

                    await consumer.CompleteSnapshotAsync(new(attempt.Epoch, rows, setup.Lineage.Tables.Count), run.Token);
                    if (await setup.Store.ReadActiveVersionAsync("ledger", run.Token) is null)
                    {
                        await setup.Store.PromoteAsync(lease, (await setup.Store.ReadStateAsync(setup.Identity, run.Token)).Checkpoint, null, run.Token);
                    }

                    ready.TrySetResult();
                    await ConsumeAsync(attempt.CreateChangeStream(), consumer, run.Token);
                }
                else
                {
                    ready.TrySetResult();
                    await using var replication = await BlueTuskLogicalReplicationConnection.OpenAsync(setup.Source.CreateDedicatedSessionOptions(), run.Token);
                    var stream = new PgOutputChangeStream(replication.StartReplicationAsync(new BlueTuskPgOutputReplicationOptions
                    {
                        SlotName = setup.Slot,
                        PublicationNames = [setup.Publication],
                        StartPosition = state.Checkpoint,
                        ProtocolVersion = 2,
                        StreamingMode = BlueTuskLogicalStreamingMode.On,
                        Messages = true,
                    }, run.Token).DecodePgOutputAsync(new BlueTuskPgOutputDecoderOptions { ProtocolVersion = 2, StreamingMode = BlueTuskPgOutputStreamingMode.On }, run.Token),
                        setup.Identity.Source, observer: new FeedbackObserver(replication));
                    await ConsumeAsync(stream, consumer, run.Token);
                }
            }
            finally
            {
                await run.CancelAsync();
                try { await renew; }
                catch (OperationCanceledException) { }
                catch (Exception exception) when (exception is BlueTuskException or IOException or ProjectionFencedException) { }
            }
        }

        private static async Task RenewAsync(PostgreSqlProjectionStore store, ProjectionLease lease, CancellationTokenSource run)
        {
            using var timer = new PeriodicTimer(Heartbeat);
            while (await timer.WaitForNextTickAsync(run.Token))
            {
                bool renewed;
                try { renewed = await store.RenewAsync(lease, Lease, run.Token); }
                catch (Exception exception) when (exception is BlueTuskException or IOException) { continue; }
                if (!renewed)
                {
                    await run.CancelAsync();
                    throw new ProjectionFencedException();
                }
            }
        }

        private static async Task ConsumeAsync(IChangeStream stream, StreamsProjectionConsumer consumer, CancellationToken token)
        {
            await foreach (var delivery in stream.ReadTransactionsAsync(token))
            {
                await using (delivery) { await consumer.ConsumeTransactionAsync(delivery, token); }
            }
        }

        private sealed class FeedbackObserver(BlueTuskLogicalReplicationConnection connection) : IChangeDeliveryObserver
        {
            public ValueTask AcknowledgeAsync(ChangeTransaction transaction, CancellationToken cancellationToken = default) =>
                new LogicalReplicationFeedbackSender(connection).SendFeedbackAsync(transaction.CommitEndPosition, cancellationToken);
            public ValueTask NackAsync(ChangeTransaction transaction, Exception? failure, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        }
    }
}
