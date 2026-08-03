using System.Runtime.CompilerServices;
using BlueTusk.Data;
using BlueTusk.Streams;
using BlueTusk.Streams.Storage.PostgreSql;
using BlueTusk.Streams.Testing;
using BlueTusk.Sync;
using BlueTusk.Sync.DependencyInjection;
using BlueTusk.TypeSystem;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskSyncRelaySourceIntegrationTests
{
    private static readonly ChangeSourceIdentity Source =
        new("sync-relay-system", "sync-relay-db", "sync-relay-slot", "public:orders");

    private static readonly SyncTransformVersion Transform =
        SyncTransformVersion.Create("orders", "v1");

    [Fact]
    public async Task Relay_snapshot_run_progress_rejects_corruption()
    {
        var connectionString = GetConnectionString();
        var schema = "bluetusk_sync_progress_test_" + Guid.NewGuid().ToString("N");
        await using var dataSource = BlueTuskDataSource.Create(connectionString);
        var relay = new PostgreSqlDurableChangeRelay(
            new PostgreSqlStreamsStorageOptions
            {
                ControlDataSource = dataSource,
                ControlSchema = schema,
                MaxRelayStorageBytes = 1024 * 1024,
            });
        try
        {
            await relay.InitializeAsync();
            var registration = await relay.RegisterSourceAsync(Source);
            await using var session = await PostgreSqlRelayConsumerGroupSession.AcquireAsync(
                relay,
                registration,
                RelayOptions("integrity-worker"));
            _ = await relay.BeginSnapshotRunAsync(
                session.Lease,
                SnapshotEpoch.Create(Source, Lsn(10)),
                Transform.Fingerprint);
            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"UPDATE \"{schema}\".snapshot_runs SET progress = '\\x0102'";
                _ = await command.ExecuteNonQueryAsync();
            }

            await Assert.ThrowsAsync<ChangeRelaySnapshotRunException>(
                () => relay.GetLatestSnapshotRunAsync(
                    session.Lease,
                    Transform.Fingerprint).AsTask());
        }
        finally
        {
            await DropSchemaAsync(dataSource, schema);
        }
    }

    [Fact]
    public async Task Relay_replay_source_reads_only_the_exact_retained_transaction()
    {
        var connectionString = GetConnectionString();
        var schema = "bluetusk_sync_replay_source_" + Guid.NewGuid().ToString("N");
        await using var dataSource = BlueTuskDataSource.Create(connectionString);
        var relay = new PostgreSqlDurableChangeRelay(
            new PostgreSqlStreamsStorageOptions
            {
                ControlDataSource = dataSource,
                ControlSchema = schema,
                MaxRelayStorageBytes = 1024 * 1024,
            });
        try
        {
            await relay.InitializeAsync();
            var registration = await relay.RegisterSourceAsync(Source);
            var lease = Assert.IsType<ChangeStreamLease>(
                (await relay.AcquireSourceLeaseAsync(
                    registration,
                    "replay-source",
                    TimeSpan.FromMinutes(1))).Lease);
            await AppendAsync(relay, registration, lease, 42, Lsn(105));
            Assert.True(await relay.ReleaseSourceLeaseAsync(lease));
            var source = new PostgreSqlRelaySyncQuarantineReplaySource(relay);

            var transaction = await source.ReadTransactionAsync(
                new SyncQuarantineIdentity("orders", Source, 42, Lsn(105)));
            var missing = await source.ReadTransactionAsync(
                new SyncQuarantineIdentity("orders", Source, 43, Lsn(105)));

            Assert.NotNull(transaction);
            Assert.Equal(42U, transaction.TransactionId);
            Assert.Equal(Lsn(105), transaction.CommitEndPosition);
            Assert.Null(missing);
        }
        finally
        {
            await DropSchemaAsync(dataSource, schema);
        }
    }

    [Fact]
    public async Task Relay_source_restarts_snapshot_epochs_skips_baseline_and_resumes_without_resnapshot()
    {
        var connectionString = GetConnectionString();
        var schema = "bluetusk_sync_source_test_" + Guid.NewGuid().ToString("N");
        await using var dataSource = BlueTuskDataSource.Create(connectionString);
        var relay = new PostgreSqlDurableChangeRelay(
            new PostgreSqlStreamsStorageOptions
            {
                ControlDataSource = dataSource,
                ControlSchema = schema,
                MaxRelayStorageBytes = 1024 * 1024,
            });
        try
        {
            await relay.InitializeAsync();
            var registration = await relay.RegisterSourceAsync(Source);
            var crashedEpoch = SnapshotEpoch.Create(Source, Lsn(10));
            await using (var crashed = await PostgreSqlRelayConsumerGroupSession.AcquireAsync(
                             relay,
                             registration,
                             RelayOptions("crashed-worker")))
            {
                _ = await relay.BeginSnapshotRunAsync(
                    crashed.Lease,
                    crashedEpoch,
                    Transform.Fingerprint);
            }

            var sourceLease = Assert.IsType<ChangeStreamLease>(
                (await relay.AcquireSourceLeaseAsync(
                    registration,
                    "source-worker",
                    TimeSpan.FromMinutes(1))).Lease);
            await AppendAsync(relay, registration, sourceLease, 1, Lsn(21));
            await AppendAsync(relay, registration, sourceLease, 2, Lsn(41));
            Assert.True(await relay.ReleaseSourceLeaseAsync(sourceLease));

            var firstSnapshot = new EmptySnapshotSource(SnapshotEpoch.Create(Source, Lsn(30)));
            using var firstCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var firstConsumer = new RecordingConsumer(firstCancellation, transactionLimit: 1);
            var firstSource = new PostgreSqlRelaySyncPipelineSource(
                relay,
                Source,
                firstSnapshot,
                Transform,
                RelayOptions("worker-1"));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => firstSource.RunAsync(firstConsumer, firstCancellation.Token));

            Assert.Equal(1, firstSnapshot.BeginAttempts);
            Assert.Equal(crashedEpoch.Value, Assert.Single(firstConsumer.Resets).AbandonedEpoch);
            Assert.Equal(1, firstConsumer.StartCount);
            Assert.Equal(1, firstConsumer.CompleteCount);
            Assert.Equal([Lsn(41)], firstConsumer.Transactions);

            await using (var inspection = await PostgreSqlRelayConsumerGroupSession.AcquireAsync(
                             relay,
                             registration,
                             RelayOptions("inspector-1")))
            {
                var completed = Assert.IsType<ChangeRelaySnapshotRun>(
                    await relay.GetLatestSnapshotRunAsync(
                        inspection.Lease,
                        Transform.Fingerprint));
                Assert.Equal(ChangeRelaySnapshotRunState.Completed, completed.State);
                Assert.Equal(firstSnapshot.Epoch.Value, completed.SnapshotEpoch);
                Assert.Equal(Lsn(30), completed.ConsistentPosition);
            }

            sourceLease = Assert.IsType<ChangeStreamLease>(
                (await relay.AcquireSourceLeaseAsync(
                    registration,
                    "source-worker-2",
                    TimeSpan.FromMinutes(1))).Lease);
            await AppendAsync(relay, registration, sourceLease, 3, Lsn(51));
            Assert.True(await relay.ReleaseSourceLeaseAsync(sourceLease));

            var forbiddenSnapshot = new EmptySnapshotSource(
                SnapshotEpoch.Create(Source, Lsn(50)),
                failIfStarted: true);
            using var restartCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var restartConsumer = new RecordingConsumer(restartCancellation, transactionLimit: 1);
            var restartedSource = new PostgreSqlRelaySyncPipelineSource(
                relay,
                Source,
                forbiddenSnapshot,
                Transform,
                RelayOptions("worker-2"));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => restartedSource.RunAsync(restartConsumer, restartCancellation.Token));

            Assert.Equal(0, forbiddenSnapshot.BeginAttempts);
            Assert.Empty(restartConsumer.Resets);
            Assert.Equal([Lsn(51)], restartConsumer.Transactions);

            var incompatibleSource = new PostgreSqlRelaySyncPipelineSource(
                relay,
                Source,
                forbiddenSnapshot,
                SyncTransformVersion.Create("orders", "v2"),
                RelayOptions("worker-3"));
            await Assert.ThrowsAsync<ChangeRelaySnapshotCompatibilityException>(
                () => incompatibleSource.RunAsync(new RecordingConsumer(null, 1)));

            await using var finalInspection =
                await PostgreSqlRelayConsumerGroupSession.AcquireAsync(
                    relay,
                    registration,
                    RelayOptions("inspector-2"));
            var empty = await relay.ReadConsumerGroupAsync(
                finalInspection.Lease,
                maxTransactions: 1,
                maxBytes: 1024 * 1024);
            Assert.Empty(empty.Records);
            Assert.Equal(3, empty.Group.CheckpointSequence);
        }
        finally
        {
            await DropSchemaAsync(dataSource, schema);
        }
    }

    private static PostgreSqlRelayChangeStreamOptions RelayOptions(string ownerId) =>
        new()
        {
            ConsumerGroup = "sync-orders",
            OwnerId = ownerId,
            EmptyReadDelay = TimeSpan.FromMilliseconds(10),
            LeaseDuration = TimeSpan.FromSeconds(2),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(250),
            MaxTransactionsPerRead = 8,
            MaxBytesPerRead = 1024 * 1024,
        };

    private static async Task AppendAsync(
        PostgreSqlDurableChangeRelay relay,
        ChangeRelaySourceRegistration registration,
        ChangeStreamLease sourceLease,
        uint transactionId,
        BlueTuskLogSequenceNumber position)
    {
        await using var delivery = ChangeDeliveryTestFactory.CreateCommitted(
            Source,
            transactionId,
            position);
        _ = await relay.AppendAsync(
            registration,
            delivery.Transaction,
            sourceLease);
    }

    private static BlueTuskLogSequenceNumber Lsn(ulong value) => new(value);

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "BLUETUSK_TEST_CONNECTION_STRING");
        return string.IsNullOrWhiteSpace(connectionString)
            ? throw SkipException.ForSkip(
                "BLUETUSK_TEST_CONNECTION_STRING is not configured.")
            : connectionString;
    }

    private static async Task DropSchemaAsync(BlueTuskDataSource dataSource, string schema)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
        _ = await command.ExecuteNonQueryAsync();
    }

    private sealed class EmptySnapshotSource(
        SnapshotEpoch epoch,
        bool failIfStarted = false) : IConsistentSnapshotSource
    {
        public SnapshotEpoch Epoch { get; } = epoch;

        public int BeginAttempts { get; private set; }

        public ValueTask<IConsistentSnapshotAttempt> BeginAttemptAsync(
            Guid? abandonedEpoch,
            CancellationToken cancellationToken = default)
        {
            BeginAttempts++;
            if (failIfStarted)
            {
                throw new InvalidOperationException(
                    "A completed relay bootstrap must resume without exporting another snapshot.");
            }

            return ValueTask.FromResult<IConsistentSnapshotAttempt>(new Attempt(Epoch));
        }

        private sealed class Attempt(SnapshotEpoch epoch) : IConsistentSnapshotAttempt
        {
            public SnapshotEpoch Epoch { get; } = epoch;

            public IReadOnlyList<ChangeTable> Tables => [];

            public async IAsyncEnumerable<ChangeSnapshotBatch> ReadSnapshotAsync(
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                yield break;
            }

            public IChangeStream CreateChangeStream() =>
                throw new InvalidOperationException(
                    "Relay bootstrap must use the independently checkpointed relay group.");

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingConsumer(
        CancellationTokenSource? cancellation,
        int transactionLimit) : IChangeStreamConsumer
    {
        public List<SnapshotReset> Resets { get; } = [];

        public int StartCount { get; private set; }

        public int CompleteCount { get; private set; }

        public List<BlueTuskLogSequenceNumber> Transactions { get; } = [];

        public ValueTask ResetSnapshotAsync(
            SnapshotReset reset,
            CancellationToken cancellationToken = default)
        {
            Resets.Add(reset);
            return ValueTask.CompletedTask;
        }

        public ValueTask StartSnapshotAsync(
            SnapshotStart start,
            CancellationToken cancellationToken = default)
        {
            StartCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask ConsumeSnapshotBatchAsync(
            ChangeSnapshotBatch batch,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask CompleteSnapshotAsync(
            SnapshotComplete complete,
            CancellationToken cancellationToken = default)
        {
            CompleteCount++;
            return ValueTask.CompletedTask;
        }

        public async ValueTask ConsumeTransactionAsync(
            ChangeTransactionDelivery delivery,
            CancellationToken cancellationToken = default)
        {
            Transactions.Add(delivery.Transaction.CommitEndPosition);
            await delivery.AcknowledgeAsync(cancellationToken).ConfigureAwait(false);
            if (Transactions.Count >= transactionLimit)
            {
                cancellation?.Cancel();
            }
        }
    }
}
