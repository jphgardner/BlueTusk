using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.Sync.Testing;
using BlueTusk.TypeSystem;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Xunit.Sdk;

namespace BlueTusk.Sync.Kafka.Tests;

public sealed class KafkaSyncDestinationTests
{
    [Fact]
    public async Task Kafka_passes_shared_destination_conformance_with_restart_deduplication()
    {
        var harness = new KafkaHarness();

        var result = await SyncDestinationConformanceSuite.VerifyAsync(harness);

        Assert.Equal("Apache Kafka", result.DestinationName);
        Assert.False(result.QuarantineVerified);
        Assert.True(result.Capabilities.HasFlag(SyncDestinationCapabilities.TransactionalBatches));
        Assert.Equal(5, harness.Store.Messages.Count);
    }

    [Fact]
    public async Task Ambiguous_commit_never_advances_and_requires_reprovisioning()
    {
        var store = new DurableStore();
        var destination = CreateDestination(store);
        var transform = SyncTransformVersion.Create("orders", "v1");
        await destination.ProvisionAsync(new SyncProvisionRequest("orders", Source, transform));
        await using var delivery = ChangeDeliveryTestFactory.CreateCommitted(
            Source,
            42,
            new BlueTuskLogSequenceNumber(105));
        var batch = Transaction("orders", transform, delivery.Transaction, "correct");
        store.FailNextPublish = true;

        var error = await Assert.ThrowsAsync<KafkaSyncDeliveryException>(
            async () => await destination.ApplyTransactionAsync(batch));

        Assert.Contains("checkpoint was not advanced", error.Message, StringComparison.Ordinal);
        Assert.Empty(store.Messages);
        Assert.DoesNotContain("position", store.Checkpoints.Keys);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await destination.ApplyTransactionAsync(batch));

        await destination.ProvisionAsync(new SyncProvisionRequest("orders", Source, transform));
        Assert.Equal(SyncApplyStatus.Applied, (await destination.ApplyTransactionAsync(batch)).Status);
        Assert.Single(store.Messages);
    }

    [Fact]
    public void Options_reject_unordered_or_invalid_topic_configuration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new KafkaSyncDestination(new KafkaSyncOptions
        {
            BootstrapServers = "kafka:9092",
            TopicPrefix = "orders",
            TransactionalId = "orders-1",
            PartitionCount = 2,
        }));
        Assert.Throws<ArgumentException>(() => new KafkaSyncDestination(new KafkaSyncOptions
        {
            BootstrapServers = "kafka:9092",
            TopicPrefix = "orders *",
            TransactionalId = "orders-1",
        }));
    }

    [Fact]
    public async Task Live_Kafka_persists_atomic_event_and_checkpoint_across_restart()
    {
        var bootstrapServers = Environment.GetEnvironmentVariable("BLUETUSK_KAFKA_BOOTSTRAP_SERVERS");
        if (string.IsNullOrWhiteSpace(bootstrapServers))
        {
            throw SkipException.ForSkip(
                "BLUETUSK_KAFKA_BOOTSTRAP_SERVERS is not configured.");
        }

        var suffix = Guid.NewGuid().ToString("N");
        var topicPrefix = "bluetusk.sync.test." + suffix;
        var options = new KafkaSyncOptions
        {
            BootstrapServers = bootstrapServers,
            TopicPrefix = topicPrefix,
            TransactionalId = "bluetusk-sync-test-" + suffix,
            ClientId = "bluetusk-sync-tests",
            ReplicationFactor = 1,
            InitializationTimeout = TimeSpan.FromSeconds(20),
        };
        var transform = SyncTransformVersion.Create("orders", "v1");
        KafkaSyncDestination? destination = null;
        try
        {
            destination = new KafkaSyncDestination(options);
            Assert.Equal(
                SyncProvisionStatus.Ready,
                (await destination.ProvisionAsync(
                    new SyncProvisionRequest("orders", Source, transform))).Status);
            await using var delivery = ChangeDeliveryTestFactory.CreateCommitted(
                Source,
                42,
                new BlueTuskLogSequenceNumber(105));
            var batch = Transaction("orders", transform, delivery.Transaction, "live");
            Assert.Equal(SyncApplyStatus.Applied, (await destination.ApplyTransactionAsync(batch)).Status);
            Assert.Equal(
                SyncApplyStatus.AlreadyApplied,
                (await destination.ApplyTransactionAsync(batch)).Status);
            await destination.DisposeAsync();
            destination = null;

            destination = new KafkaSyncDestination(options);
            Assert.Equal(
                SyncProvisionStatus.Ready,
                (await destination.ProvisionAsync(
                    new SyncProvisionRequest("orders", Source, transform))).Status);
            Assert.Equal(
                SyncApplyStatus.AlreadyApplied,
                (await destination.ApplyTransactionAsync(batch)).Status);

            var drifted = new KafkaSyncDestination(options with
            {
                TransactionalId = options.TransactionalId + "-drift",
            });
            await using (drifted)
            {
                var mismatch = await drifted.ProvisionAsync(new SyncProvisionRequest(
                    "orders",
                    Source,
                    SyncTransformVersion.Create("orders", "v2")));
                Assert.Equal(SyncProvisionStatus.RebuildRequired, mismatch.Status);
                Assert.Equal(transform.Fingerprint, mismatch.ExistingTransformFingerprint);
            }
        }
        finally
        {
            if (destination is not null)
            {
                await destination.DisposeAsync();
            }

            using var admin = new AdminClientBuilder(new AdminClientConfig
            {
                BootstrapServers = bootstrapServers,
                ClientId = "bluetusk-sync-tests-cleanup",
            }).Build();
            try
            {
                await admin.DeleteTopicsAsync([topicPrefix + ".events", topicPrefix + ".state"]);
            }
            catch (DeleteTopicsException)
            {
                // A failed test may have stopped before either topic was created.
            }
        }
    }

    private static readonly ChangeSourceIdentity Source =
        new("kafka-system", "kafka-database", "kafka-slot", "public:orders");

    private static KafkaSyncDestination CreateDestination(DurableStore store) => new(new KafkaSyncOptions
    {
        BootstrapServers = "not-used:9092",
        TopicPrefix = "bluetusk.orders",
        TransactionalId = "orders-writer",
        ReplicationFactor = 1,
        TransportFactory = _ => new FakeTransport(store),
    });

    private static SyncTransactionBatch Transaction(
        string pipelineId,
        SyncTransformVersion transform,
        ChangeTransaction transaction,
        string stage) => new(
            pipelineId,
            transform,
            transaction,
            [new SyncMutation(
                new ChangeId(
                    transaction.Source,
                    transaction.CommitEndPosition,
                    transaction.TransactionId,
                    0),
                SyncMutationKind.Upsert,
                "orders",
                "42",
                System.Text.Encoding.UTF8.GetBytes($"{{\"stage\":\"{stage}\"}}"),
                "application/json")]);

    private sealed class KafkaHarness : ISyncDestinationConformanceHarness
    {
        internal DurableStore Store { get; } = new();

        public string PipelineId => "conformance";

        public ChangeSourceIdentity Source { get; } = new(
            "conformance-system",
            "conformance-database",
            "conformance-slot",
            "public:conformance");

        public ValueTask<ISyncDestination> CreateDestinationAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ISyncDestination>(CreateDestination(Store));
        }

        public ValueTask VerifyDurableStateAsync(
            SyncDestinationConformanceStage stage,
            ISyncDestination destination,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.IsType<KafkaSyncDestination>(destination);
            var expected = stage is SyncDestinationConformanceStage.SnapshotApplied or
                SyncDestinationConformanceStage.SnapshotRestart
                ? 4
                : 5;
            Assert.Equal(expected, Store.Messages.Count);
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class DurableStore
    {
        internal string? PipelineId { get; set; }

        internal string? SourceFingerprint { get; set; }

        internal string? TransformFingerprint { get; set; }

        internal Dictionary<string, string> Checkpoints { get; } = new(StringComparer.Ordinal);

        internal List<KafkaSyncMessage> Messages { get; } = [];

        internal bool FailNextPublish { get; set; }
    }

    private sealed class FakeTransport(DurableStore store) : IKafkaSyncTransport
    {
        public ValueTask<KafkaSyncLoadedState> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new KafkaSyncLoadedState(
                store.PipelineId,
                store.SourceFingerprint,
                store.TransformFingerprint,
                new Dictionary<string, string>(store.Checkpoints, StringComparer.Ordinal)));
        }

        public ValueTask InitializeAsync(
            SyncProvisionRequest request,
            bool writeConfiguration,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (writeConfiguration)
            {
                store.PipelineId = request.PipelineId;
                store.SourceFingerprint = request.Source.Fingerprint;
                store.TransformFingerprint = request.Transform.Fingerprint;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask PublishAsync(
            KafkaSyncMessage message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (store.FailNextPublish)
            {
                store.FailNextPublish = false;
                throw new KafkaSyncDeliveryException(
                    "Kafka checkpoint was not advanced.",
                    new TimeoutException());
            }

            store.Messages.Add(message);
            foreach (var key in message.TombstoneKeys)
            {
                _ = store.Checkpoints.Remove(key);
            }

            store.Checkpoints[message.CheckpointKey] = message.CheckpointValue;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
