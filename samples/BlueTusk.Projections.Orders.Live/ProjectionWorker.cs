using BlueTusk.Data;
using BlueTusk.Replication;
using BlueTusk.Replication.PgOutput;
using BlueTusk.Streams;

namespace BlueTusk.Projections.Orders.Live.Sample;

internal sealed class SampleState
{
    public SampleState(BlueTuskDataSource dataSource, SampleOptions options)
    {
        DataSource = dataSource;
        Options = options;
        Store = new PostgreSqlProjectionStore(dataSource, new PostgreSqlProjectionsOptions { Schema = options.ProjectionSchema });
    }
    internal BlueTuskDataSource DataSource { get; }
    internal SampleOptions Options { get; }
    internal PostgreSqlProjectionStore Store { get; }
    internal ProjectionLease? Lease { get; set; }
    internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class ProjectionWorker(SampleState state) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await state.Store.InitializeAsync(stoppingToken);
        ChangeSourceIdentity source;
        await using (var identify = await BlueTuskLogicalReplicationConnection.OpenAsync(state.DataSource.CreateDedicatedSessionOptions(), stoppingToken))
        {
            var system = await identify.IdentifySystemAsync(stoppingToken);
            source = new(system.SystemIdentifier, system.DatabaseName!, state.Options.Slot, state.Options.Publication);
        }
        var identity = new ProjectionIdentity("orders", state.Options.Version, "orders-join-total-v" + state.Options.Version, source);
        var lineage = await PostgreSqlProjectionLineage.CaptureAsync(state.DataSource, source, [state.Options.Publication], stoppingToken);
        if (lineage.Tables.Count != 2 || lineage.Tables.Any(table => table.Schema != state.Options.SourceSchema || table.Name is not "customers" and not "orders"))
        {
            throw new InvalidOperationException("The sample publication must contain exactly its configured customer and order tables.");
        }
        await state.Store.RegisterWithLineageAsync(identity, lineage, stoppingToken);
        if (state.Options.RecoveryId is { } recoveryId)
        {
            var ticket = await state.Store.ReadRecoveryAsync("orders", recoveryId, stoppingToken);
            if (ticket is null)
            {
                var evidence = await PostgreSqlProjectionLineage.CaptureForCutoverAsync(state.DataSource, source, [state.Options.Publication], stoppingToken);
                ticket = await state.Store.BeginRecoveryAsync(identity, state.Options.RecoveryExpectedActiveVersion!.Value, evidence, recoveryId, state.Options.RecoveryReason!, stoppingToken);
            }
            if (ticket.CandidateVersion != identity.Version || ticket.TargetLineageFingerprint != lineage.Fingerprint || ticket.Reason != state.Options.RecoveryReason)
            { throw new InvalidOperationException("Configured recovery differs from its durable immutable operator intent."); }
        }
        var lease = await state.Store.AcquireAsync(identity, Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(1), stoppingToken)
            ?? throw new InvalidOperationException("Another projection worker holds this version lease.");
        state.Lease = lease;
        using var run = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var renew = RenewAsync(lease, run);
        try
        {
            var consumer = new StreamsProjectionConsumer(state.Store, lease, new OrdersProjection(identity));
            var checkpoint = await state.Store.ReadStateAsync(identity, run.Token);
            if (checkpoint.Phase != ProjectionBuildPhase.CatchingUp)
            {
                var tables = lineage.Tables;
                var snapshot = new PostgreSqlConsistentSnapshotSource(state.DataSource, new PostgreSqlConsistentSnapshotOptions
                {
                    Source = source, PublicationNames = [state.Options.Publication],
                    Tables = tables.Select(static table => new PostgreSqlSnapshotTable(table, table.Columns.Where(static column => column.IsKey).Select(static column => column.Ordinal))).ToArray(),
                    MaximumParallelTables = 2, MaximumBatchRows = 512,
                    ExistingSlotMode = checkpoint.Phase is ProjectionBuildPhase.Snapshot or ProjectionBuildPhase.Resetting ? PostgreSqlExistingSnapshotSlotMode.RestartSnapshot : PostgreSqlExistingSnapshotSlotMode.Fail
                }, connection => new LogicalFeedbackObserver(connection));
                await using var attempt = await snapshot.BeginAttemptAsync(checkpoint.SnapshotEpoch, run.Token);
                await consumer.StartSnapshotAsync(new(attempt.Epoch, tables.Count), run.Token);
                long rows = 0;
                await foreach (var batch in attempt.ReadSnapshotAsync(run.Token))
                {
                    await consumer.ConsumeSnapshotBatchAsync(batch, run.Token);
                    rows += batch.Rows.Count;
                }
                await consumer.CompleteSnapshotAsync(new(attempt.Epoch, rows, tables.Count), run.Token);
                await PublishInitialAsync(lease, run.Token);
                state.Ready.TrySetResult();
                await ConsumeAsync(attempt.CreateChangeStream(), consumer, run.Token);
            }
            else
            {
                await PublishInitialAsync(lease, run.Token);
                state.Ready.TrySetResult();
                await using var replication = await BlueTuskLogicalReplicationConnection.OpenAsync(state.DataSource.CreateDedicatedSessionOptions(), run.Token);
                var stream = new PgOutputChangeStream(replication.StartReplicationAsync(new BlueTuskPgOutputReplicationOptions
                {
                    SlotName = source.SlotName, PublicationNames = [state.Options.Publication], StartPosition = checkpoint.Checkpoint,
                    ProtocolVersion = 2, StreamingMode = BlueTuskLogicalStreamingMode.On, Messages = true
                }, run.Token).DecodePgOutputAsync(new BlueTuskPgOutputDecoderOptions { ProtocolVersion = 2, StreamingMode = BlueTuskPgOutputStreamingMode.On }, run.Token),
                    source, observer: new LogicalFeedbackObserver(replication));
                await ConsumeAsync(stream, consumer, run.Token);
            }
        }
        finally
        {
            await run.CancelAsync();
            try { await renew; } catch (OperationCanceledException) when (run.IsCancellationRequested) { }
            await state.Store.ReleaseAsync(lease, CancellationToken.None);
        }
    }

    private async Task RenewAsync(ProjectionLease lease, CancellationTokenSource run)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(run.Token))
        {
            if (!await state.Store.RenewAsync(lease, TimeSpan.FromMinutes(1), run.Token))
            {
                await run.CancelAsync();
                throw new ProjectionFencedException();
            }
        }
    }

    private async Task PublishInitialAsync(ProjectionLease lease, CancellationToken token)
    {
        if (await state.Store.ReadActiveVersionAsync("orders", token) is null)
        {
            await state.Store.PromoteAsync(lease, (await state.Store.ReadStateAsync(lease.Identity, token)).Checkpoint, null, token);
        }
    }

    private static async Task ConsumeAsync(IChangeStream stream, StreamsProjectionConsumer consumer, CancellationToken token)
    {
        await foreach (var delivery in stream.ReadTransactionsAsync(token))
        {
            await using (delivery) { await consumer.ConsumeTransactionAsync(delivery, token); }
        }
    }

    private sealed class LogicalFeedbackObserver(BlueTuskLogicalReplicationConnection connection) : IChangeDeliveryObserver
    {
        public ValueTask AcknowledgeAsync(ChangeTransaction transaction, CancellationToken cancellationToken = default) =>
            new LogicalReplicationFeedbackSender(connection).SendFeedbackAsync(transaction.CommitEndPosition, cancellationToken);
        public ValueTask NackAsync(ChangeTransaction transaction, Exception? failure, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
