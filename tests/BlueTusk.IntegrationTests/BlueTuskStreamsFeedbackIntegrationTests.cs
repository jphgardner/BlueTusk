using System.Threading.Channels;
using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Replication;
using BlueTusk.Replication.PgOutput;
using BlueTusk.Streams;
using BlueTusk.Sync;
using BlueTusk.TypeSystem;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

/// <summary>
/// A consumer without a delivery observer must still report completed work to PostgreSQL;
/// otherwise the slot's confirmed_flush_lsn never moves and the server retains all WAL.
/// </summary>
public sealed class BlueTuskStreamsFeedbackIntegrationTests
{
    private static readonly TimeSpan SlotProgressTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Snapshot_consumer_without_observer_confirms_acknowledged_work_and_releases_wal()
    {
        var connectionString = GetConnectionString();
        await using var fixture = await FeedbackFixture.CreateAsync(connectionString, "consumer");
        var source = new PostgreSqlConsistentSnapshotSource(
            fixture.DataSource,
            fixture.SnapshotOptions());
        var consumer = new AcknowledgingConsumer();
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var run = new SnapshotThenStreamCoordinator(source).RunAsync(consumer, stop.Token);
        try
        {
            await consumer.SnapshotCompleted.Task.WaitAsync(SlotProgressTimeout, stop.Token);
            var initial = await fixture.ReadSlotAsync();

            var first = await fixture.InsertAndAwaitAsync(consumer, 1, stop.Token);
            var confirmedFirst = await fixture.WaitForSlotAsync(
                slot => slot.ConfirmedFlush >= first,
                $"confirmed_flush_lsn to reach the acknowledged commit {first}",
                stop.Token);
            Assert.True(confirmedFirst.ConfirmedFlush > initial.ConfirmedFlush);

            // A checkpoint logs a running-transactions record. Once a later commit is confirmed,
            // PostgreSQL moves restart_lsn past it and stops retaining the earlier WAL.
            await fixture.ExecuteAsync("CHECKPOINT");
            var second = await fixture.InsertAndAwaitAsync(consumer, 2, stop.Token);
            var released = await fixture.WaitForSlotAsync(
                slot => slot.ConfirmedFlush >= second && slot.Restart > initial.Restart,
                $"confirmed_flush_lsn to reach {second} and restart_lsn to move past {initial.Restart}",
                stop.Token);
            Assert.True(released.Restart <= released.ConfirmedFlush);
        }
        finally
        {
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            await fixture.DropSlotAsync();
        }
    }

    [Fact]
    public async Task Direct_stream_without_observer_confirms_only_acknowledged_transactions()
    {
        var connectionString = GetConnectionString();
        await using var fixture = await FeedbackFixture.CreateAsync(connectionString, "direct");
        await using var replication =
            await BlueTuskLogicalReplicationConnection.OpenAsync(connectionString);
        var system = await replication.IdentifySystemAsync();
        _ = await replication.CreateReplicationSlotAsync(fixture.SlotName, temporary: true);
        var created = await fixture.ReadSlotAsync();
        var stream = new PgOutputChangeStream(
            replication.StartReplicationAsync(fixture.SlotName, fixture.PublicationName)
                .DecodePgOutputAsync(),
            new ChangeSourceIdentity(
                system.SystemIdentifier,
                system.DatabaseName!,
                fixture.SlotName,
                fixture.PublicationName));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var deliveries = stream.ReadTransactionsAsync(timeout.Token)
            .GetAsyncEnumerator(timeout.Token);

        var firstMove = deliveries.MoveNextAsync().AsTask();
        await fixture.ExecuteAsync($"INSERT INTO {fixture.QuotedTable} VALUES (1)");
        Assert.True(await firstMove.WaitAsync(SlotProgressTimeout, timeout.Token));
        var first = deliveries.Current;

        // Delivered but not yet acknowledged: the position must not be confirmed.
        await fixture.AssertSlotStaysBelowAsync(
            first.Transaction.CommitEndPosition,
            timeout.Token);
        Assert.Equal(created.ConfirmedFlush, (await fixture.ReadSlotAsync()).ConfirmedFlush);

        await first.AcknowledgeAsync(timeout.Token);
        await fixture.WaitForSlotAsync(
            slot => slot.ConfirmedFlush >= first.Transaction.CommitEndPosition,
            $"confirmed_flush_lsn to reach {first.Transaction.CommitEndPosition}",
            timeout.Token);

        var secondMove = deliveries.MoveNextAsync().AsTask();
        await fixture.ExecuteAsync($"INSERT INTO {fixture.QuotedTable} VALUES (2)");
        Assert.True(await secondMove.WaitAsync(SlotProgressTimeout, timeout.Token));
        var second = deliveries.Current;
        await second.NackAsync(new InvalidOperationException("Destination unavailable."), timeout.Token);

        // A rejected transaction is never confirmed, so PostgreSQL redelivers it.
        await fixture.AssertSlotStaysBelowAsync(
            second.Transaction.CommitEndPosition,
            timeout.Token);
        await Assert.ThrowsAsync<ChangeDeliveryNotAcknowledgedException>(
            () => deliveries.MoveNextAsync().AsTask());
    }

    [Fact]
    public async Task Direct_sync_pipeline_without_observer_confirms_applied_transactions()
    {
        var connectionString = GetConnectionString();
        await using var fixture = await FeedbackFixture.CreateAsync(connectionString, "sync");
        var options = fixture.SnapshotOptions();
        var destination = new RecordingDestination();
        await using var pipeline = new SyncPipeline(
            new SyncPipelineOptions { PipelineId = "feedback-" + fixture.Suffix },
            options.Source,
            new EmptyTransform(),
            destination);
        await pipeline.ProvisionAsync();
        var source = new ConsistentSnapshotSyncPipelineSource(
            new PostgreSqlConsistentSnapshotSource(fixture.DataSource, options));
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var run = source.RunAsync(pipeline, stop.Token);
        try
        {
            await destination.SnapshotCompleted.Task.WaitAsync(SlotProgressTimeout, stop.Token);
            var initial = await fixture.ReadSlotAsync();

            await fixture.ExecuteAsync($"INSERT INTO {fixture.QuotedTable} VALUES (1)");
            var applied = await destination.Applied.Reader.ReadAsync(stop.Token)
                .AsTask().WaitAsync(SlotProgressTimeout, stop.Token);

            var confirmed = await fixture.WaitForSlotAsync(
                slot => slot.ConfirmedFlush >= applied,
                $"confirmed_flush_lsn to reach the applied commit {applied}",
                stop.Token);
            Assert.True(confirmed.ConfirmedFlush > initial.ConfirmedFlush);
            Assert.True(pipeline.Status.AppliedTransactions >= 1);
        }
        finally
        {
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            await fixture.DropSlotAsync();
        }
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var settings = new BlueTuskConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        };
        return settings.ConnectionString;
    }

    private readonly record struct SlotState(
        BlueTuskLogSequenceNumber ConfirmedFlush,
        BlueTuskLogSequenceNumber Restart);

    private sealed class FeedbackFixture : IAsyncDisposable
    {
        private readonly BlueTuskConnection _administration;
        private readonly string _connectionString;

        private FeedbackFixture(
            string connectionString,
            BlueTuskConnection administration,
            BlueTuskDataSource dataSource,
            string suffix,
            BlueTuskReplicationSystemIdentity system)
        {
            _connectionString = connectionString;
            _administration = administration;
            DataSource = dataSource;
            Suffix = suffix;
            System = system;
            TableName = $"bluetusk_feedback_{suffix}";
            PublicationName = $"bluetusk_feedback_publication_{suffix}";
            SlotName = $"bluetusk_feedback_slot_{suffix}";
            QuotedTable = BlueTuskSql.QuoteIdentifier(TableName);
        }

        public BlueTuskDataSource DataSource { get; }

        public string Suffix { get; }

        public BlueTuskReplicationSystemIdentity System { get; }

        public string TableName { get; }

        public string PublicationName { get; }

        public string SlotName { get; }

        public string QuotedTable { get; }

        public static async Task<FeedbackFixture> CreateAsync(string connectionString, string purpose)
        {
            var suffix = purpose + "_" + Guid.NewGuid().ToString("N");
            var administration = new BlueTuskConnection(connectionString);
            await administration.OpenAsync(CancellationToken.None);
            BlueTuskReplicationSystemIdentity system;
            await using (var identity = await BlueTuskLogicalReplicationConnection.OpenAsync(connectionString))
            {
                system = await identity.IdentifySystemAsync();
            }

            var fixture = new FeedbackFixture(
                connectionString,
                administration,
                BlueTuskDataSource.Create(connectionString),
                suffix,
                system);
            await fixture.ExecuteAsync($"CREATE TABLE {fixture.QuotedTable} (id integer PRIMARY KEY)");
            await fixture.ExecuteAsync(
                $"CREATE PUBLICATION {BlueTuskSql.QuoteIdentifier(fixture.PublicationName)} " +
                $"FOR TABLE {fixture.QuotedTable}");
            return fixture;
        }

        public PostgreSqlConsistentSnapshotOptions SnapshotOptions() =>
            new()
            {
                Source = new ChangeSourceIdentity(
                    System.SystemIdentifier,
                    System.DatabaseName!,
                    SlotName,
                    PublicationName),
                PublicationNames = [PublicationName],
                Tables =
                [
                    new PostgreSqlSnapshotTable(
                        new ChangeTable(
                            relationId: 0,
                            schema: "public",
                            name: TableName,
                            replicaIdentity: 'd',
                            [new ChangeColumn(0, "id", 23, -1, true)]),
                        [0]),
                ],
            };

        public async Task<BlueTuskLogSequenceNumber> InsertAndAwaitAsync(
            AcknowledgingConsumer consumer,
            int id,
            CancellationToken cancellationToken)
        {
            await ExecuteAsync($"INSERT INTO {QuotedTable} VALUES ({id})");
            while (true)
            {
                var acknowledged = await consumer.Acknowledged.Reader.ReadAsync(cancellationToken)
                    .AsTask().WaitAsync(SlotProgressTimeout, cancellationToken);
                if (acknowledged.Changes > 0)
                {
                    return acknowledged.CommitEnd;
                }
            }
        }

        public async Task<SlotState> ReadSlotAsync()
        {
            await using var command = new BlueTuskCommand(
                "SELECT confirmed_flush_lsn::text, restart_lsn::text " +
                $"FROM pg_catalog.pg_replication_slots WHERE slot_name = {BlueTuskSql.QuoteLiteral(SlotName)}",
                _administration);
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            if (!await reader.ReadAsync(CancellationToken.None))
            {
                throw new XunitException($"Replication slot {SlotName} does not exist.");
            }

            return new SlotState(
                BlueTuskLogSequenceNumber.Parse(reader.GetString(0)),
                BlueTuskLogSequenceNumber.Parse(reader.GetString(1)));
        }

        public async Task<SlotState> WaitForSlotAsync(
            Func<SlotState, bool> predicate,
            string expectation,
            CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SlotProgressTimeout);
            var last = await ReadSlotAsync();
            while (!predicate(last))
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new XunitException(
                        $"Timed out waiting for {expectation}; slot has confirmed_flush_lsn " +
                        $"{last.ConfirmedFlush} and restart_lsn {last.Restart}.");
                }

                last = await ReadSlotAsync();
            }

            return last;
        }

        public async Task AssertSlotStaysBelowAsync(
            BlueTuskLogSequenceNumber position,
            CancellationToken cancellationToken)
        {
            // Long enough for a standby status update to reach the WAL sender and be applied.
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var slot = await ReadSlotAsync();
                Assert.True(
                    slot.ConfirmedFlush < position,
                    $"confirmed_flush_lsn {slot.ConfirmedFlush} confirmed unfinished work at {position}.");
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var command = new BlueTuskCommand(sql, _administration);
            _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        public async Task DropSlotAsync()
        {
            await using var cleanup = await BlueTuskLogicalReplicationConnection.OpenAsync(_connectionString);
            var slots = await cleanup.GetReplicationSlotsAsync();
            if (slots.Any(slot => slot.SlotName == SlotName))
            {
                await cleanup.DropReplicationSlotAsync(SlotName, wait: true);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await ExecuteAsync(
                    $"DROP PUBLICATION IF EXISTS {BlueTuskSql.QuoteIdentifier(PublicationName)}");
                await ExecuteAsync($"DROP TABLE IF EXISTS {QuotedTable}");
            }
            finally
            {
                await DataSource.DisposeAsync();
                await _administration.DisposeAsync();
            }
        }
    }

    private sealed class AcknowledgingConsumer : IChangeStreamConsumer
    {
        public TaskCompletionSource SnapshotCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Channel<(BlueTuskLogSequenceNumber CommitEnd, int Changes)> Acknowledged { get; } =
            Channel.CreateUnbounded<(BlueTuskLogSequenceNumber, int)>();

        public ValueTask ResetSnapshotAsync(SnapshotReset reset, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask StartSnapshotAsync(SnapshotStart start, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask ConsumeSnapshotBatchAsync(
            ChangeSnapshotBatch batch,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask CompleteSnapshotAsync(
            SnapshotComplete complete,
            CancellationToken cancellationToken = default)
        {
            SnapshotCompleted.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public async ValueTask ConsumeTransactionAsync(
            ChangeTransactionDelivery delivery,
            CancellationToken cancellationToken = default)
        {
            var changes = await delivery.Transaction.Changes.MaterializeAsync(cancellationToken);
            await delivery.AcknowledgeAsync(cancellationToken);
            Acknowledged.Writer.TryWrite((delivery.Transaction.CommitEndPosition, changes.Count));
        }
    }

    private sealed class EmptyTransform : ISyncTransform
    {
        public SyncTransformVersion Version { get; } = SyncTransformVersion.Create("feedback", "v1");

        public ValueTask<IReadOnlyList<SyncMutation>> TransformTransactionAsync(
            ChangeTransaction transaction,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<SyncMutation>>([]);

        public ValueTask<IReadOnlyList<SyncSnapshotMutation>> TransformSnapshotBatchAsync(
            ChangeSnapshotBatch batch,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<SyncSnapshotMutation>>([]);
    }

    private sealed class RecordingDestination : ISyncDestination
    {
        public TaskCompletionSource SnapshotCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Channel<BlueTuskLogSequenceNumber> Applied { get; } =
            Channel.CreateUnbounded<BlueTuskLogSequenceNumber>();

        public string Name => "recording";

        public SyncDestinationCapabilities Capabilities => SyncDestinationCapabilities.IdempotentUpserts;

        public ValueTask<SyncProvisionResult> ProvisionAsync(
            SyncProvisionRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SyncProvisionResult(SyncProvisionStatus.Ready));

        public ValueTask ResetSnapshotAsync(
            string pipelineId,
            SnapshotReset reset,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask StartSnapshotAsync(
            string pipelineId,
            SnapshotStart start,
            SyncTransformVersion transform,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask ApplySnapshotBatchAsync(
            SyncSnapshotBatch batch,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask CompleteSnapshotAsync(
            string pipelineId,
            SnapshotComplete complete,
            SyncTransformVersion transform,
            CancellationToken cancellationToken = default)
        {
            SnapshotCompleted.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask<SyncApplyResult> ApplyTransactionAsync(
            SyncTransactionBatch batch,
            CancellationToken cancellationToken = default)
        {
            Applied.Writer.TryWrite(batch.Transaction.CommitEndPosition);
            return ValueTask.FromResult(SyncApplyResult.Applied(batch.Transaction.CommitEndPosition));
        }
    }
}
