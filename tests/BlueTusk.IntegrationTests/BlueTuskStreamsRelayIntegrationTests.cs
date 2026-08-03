using System.Runtime.CompilerServices;
using BlueTusk.Data;
using BlueTusk.Replication;
using BlueTusk.Replication.PgOutput;
using BlueTusk.Streams;
using BlueTusk.Streams.Storage.PostgreSql;
using BlueTusk.TypeSystem;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskStreamsRelayIntegrationTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PostgreSql_relay_fans_out_replays_fences_and_retains_safely()
    {
        var connectionString = GetConnectionString();
        var schema = "bluetusk_relay_test_" + Guid.NewGuid().ToString("N");
        await using var dataSource = BlueTuskDataSource.Create(connectionString);
        var relay = new PostgreSqlDurableChangeRelay(
            new PostgreSqlStreamsStorageOptions
            {
                ControlDataSource = dataSource,
                ControlSchema = schema,
                ResumeRetentionWindow = TimeSpan.Zero,
                MaxRelayStorageBytes = 1024 * 1024,
                MaxWalLagBytes = 1,
            });
        try
        {
            await relay.InitializeAsync();
            var source = SourceIdentity();
            var feedback = new RecordingFeedbackSender { FailuresRemaining = 1 };
            await using var observer = await PostgreSqlRelayChangeDeliveryObserver.AcquireAsync(
                relay,
                source,
                "source-worker",
                TimeSpan.FromMinutes(1),
                feedback);
            var stream = CreateStream(source, observer);
            await using var enumerator = stream.ReadTransactionsAsync().GetAsyncEnumerator();
            Assert.True(await enumerator.MoveNextAsync());
            var delivery = enumerator.Current;

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => delivery.AcknowledgeAsync().AsTask());
            await delivery.AcknowledgeAsync();

            Assert.Equal(2, feedback.AttemptCount);
            Assert.Equal(Lsn(21), Assert.Single(feedback.Positions));
            var firstGroup = await relay.CreateConsumerGroupAsync(observer.Source, "sync-a");
            var secondGroup = await relay.CreateConsumerGroupAsync(observer.Source, "live-b");
            var firstLease = Assert.IsType<ChangeRelayGroupLease>(
                await relay.AcquireConsumerGroupAsync(
                    firstGroup,
                    "consumer-a",
                    TimeSpan.FromMinutes(1)));
            var secondLease = Assert.IsType<ChangeRelayGroupLease>(
                await relay.AcquireConsumerGroupAsync(
                    secondGroup,
                    "consumer-b",
                    TimeSpan.FromMinutes(1)));

            var firstBatch = await relay.ReadConsumerGroupAsync(firstLease, 10, 1024 * 1024);
            var secondBatch = await relay.ReadConsumerGroupAsync(secondLease, 10, 1024 * 1024);
            var firstRecord = Assert.Single(firstBatch.Records);
            var secondRecord = Assert.Single(secondBatch.Records);
            Assert.Equal(firstRecord.Sequence, secondRecord.Sequence);
            Assert.Equal(1U, firstRecord.Transaction.TransactionId);
            Assert.IsType<InsertChange>(
                Assert.Single(await firstRecord.Transaction.Changes.MaterializeAsync()));
            var health = await relay.GetHealthAsync(observer.Source, Lsn(500));
            Assert.True(health.IsWalRetentionDanger);
            Assert.True(health.WalLagBytes > 0);

            var firstAck = await relay.AcknowledgeConsumerGroupAsync(
                firstLease,
                firstBatch.Group.StoreGeneration,
                firstRecord.Sequence);
            Assert.Equal(ChangeRelayAcknowledgeStatus.Stored, firstAck.Status);
            var retainedForSlowGroup = await relay.ApplyRetentionAsync(observer.Source);
            Assert.Equal(0, retainedForSlowGroup.DeletedTransactions);

            var secondAck = await relay.AcknowledgeConsumerGroupAsync(
                secondLease,
                secondBatch.Group.StoreGeneration,
                secondRecord.Sequence);
            Assert.Equal(ChangeRelayAcknowledgeStatus.Stored, secondAck.Status);

            Assert.True(await relay.ReleaseConsumerGroupAsync(firstLease));
            var replacementLease = Assert.IsType<ChangeRelayGroupLease>(
                await relay.AcquireConsumerGroupAsync(
                    firstGroup,
                    "consumer-c",
                    TimeSpan.FromMinutes(1)));
            Assert.True(replacementLease.FencingToken > firstLease.FencingToken);
            var fenced = await relay.AcknowledgeConsumerGroupAsync(
                firstLease,
                firstAck.Current.StoreGeneration,
                firstRecord.Sequence);
            Assert.Equal(ChangeRelayAcknowledgeStatus.Fenced, fenced.Status);

            var retention = await relay.ApplyRetentionAsync(observer.Source);
            Assert.Equal(1, retention.DeletedTransactions);
            Assert.True(retention.DeletedBytes > 0);
            var metrics = await relay.GetMetricsAsync(observer.Source);
            Assert.Equal(0, metrics.TransactionCount);
            Assert.Equal(0, metrics.StorageBytes);

            var empty = await relay.ReadConsumerGroupAsync(replacementLease, 10, 1024 * 1024);
            Assert.Empty(empty.Records);
        }
        finally
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
            _ = await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task PostgreSql_relay_rejects_append_before_exceeding_storage_bound()
    {
        var connectionString = GetConnectionString();
        var schema = "bluetusk_relay_limit_test_" + Guid.NewGuid().ToString("N");
        await using var dataSource = BlueTuskDataSource.Create(connectionString);
        var relay = new PostgreSqlDurableChangeRelay(
            new PostgreSqlStreamsStorageOptions
            {
                ControlDataSource = dataSource,
                ControlSchema = schema,
                MaxRelayStorageBytes = 1,
            });
        try
        {
            await relay.InitializeAsync();
            var source = await relay.RegisterSourceAsync(SourceIdentity());
            var lease = Assert.IsType<ChangeStreamLease>(
                (await relay.AcquireSourceLeaseAsync(
                    source,
                    "source-worker",
                    TimeSpan.FromMinutes(1))).Lease);
            var stream = CreateStream(source.Source);
            await using var enumerator = stream.ReadTransactionsAsync().GetAsyncEnumerator();
            Assert.True(await enumerator.MoveNextAsync());

            await Assert.ThrowsAsync<ChangeRelayStorageExhaustedException>(
                () => relay.AppendAsync(source, enumerator.Current.Transaction, lease).AsTask());

            var metrics = await relay.GetMetricsAsync(source);
            Assert.Equal(0, metrics.TransactionCount);
            Assert.Equal(0, metrics.StorageBytes);
        }
        finally
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
            _ = await command.ExecuteNonQueryAsync();
        }
    }

    private static PgOutputChangeStream CreateStream(
        ChangeSourceIdentity source,
        IChangeDeliveryObserver? observer = null) =>
        new(
            Messages(
                Envelope(new BlueTuskPgOutputRelation(
                    null,
                    7,
                    "public",
                    "orders",
                    'd',
                    [new BlueTuskPgOutputRelationColumn(
                        BlueTuskPgOutputRelationColumnOptions.Key,
                        "id",
                        23,
                        -1)])),
                Envelope(new BlueTuskPgOutputBegin(Lsn(10), Timestamp, 1)),
                Envelope(new BlueTuskPgOutputInsert(
                    null,
                    7,
                    new BlueTuskPgOutputTuple(
                        [new BlueTuskPgOutputTupleValue(
                            BlueTuskPgOutputTupleValueKind.Text,
                            "1"u8.ToArray())]))),
                Envelope(new BlueTuskPgOutputCommit(Lsn(20), Lsn(21), Timestamp))),
            source,
            observer: observer);

    private static ChangeSourceIdentity SourceIdentity() =>
        new("739463", "app", "orders_slot", "public:orders");

    private static BlueTuskPgOutputEnvelope Envelope(BlueTuskPgOutputMessage message) =>
        new(
            new BlueTuskXLogData(
                Lsn(1),
                Lsn(500),
                Timestamp,
                ReadOnlyMemory<byte>.Empty),
            message);

    private static BlueTuskLogSequenceNumber Lsn(ulong value) => new(value);

    private static async IAsyncEnumerable<BlueTuskPgOutputEnvelope> Messages(
        IEnumerable<BlueTuskPgOutputEnvelope> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return message;
        }
    }

    private static IAsyncEnumerable<BlueTuskPgOutputEnvelope> Messages(
        params BlueTuskPgOutputEnvelope[] messages) =>
        Messages((IEnumerable<BlueTuskPgOutputEnvelope>)messages);

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "BLUETUSK_TEST_CONNECTION_STRING");
        return string.IsNullOrWhiteSpace(connectionString)
            ? throw SkipException.ForSkip(
                "BLUETUSK_TEST_CONNECTION_STRING is not configured.")
            : connectionString;
    }

    private sealed class RecordingFeedbackSender : IReplicationFeedbackSender
    {
        public int AttemptCount { get; private set; }

        public int FailuresRemaining { get; set; }

        public List<BlueTuskLogSequenceNumber> Positions { get; } = [];

        public ValueTask SendFeedbackAsync(
            BlueTuskLogSequenceNumber position,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AttemptCount++;
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("Injected feedback failure.");
            }

            Positions.Add(position);
            return ValueTask.CompletedTask;
        }
    }
}
