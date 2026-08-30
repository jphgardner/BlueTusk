using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.Sync.Testing;
using BlueTusk.TypeSystem;
using Parquet.Serialization;
using Xunit.Sdk;

namespace BlueTusk.Sync.S3.Tests;

public sealed class S3SyncDestinationTests
{
    [Fact]
    public async Task S3_passes_shared_destination_conformance_with_immutable_manifests()
    {
        var harness = new S3Harness();

        var result = await SyncDestinationConformanceSuite.VerifyAsync(harness);

        Assert.Equal("S3 Parquet lake", result.DestinationName);
        Assert.Equal(5, harness.Store.Manifests.Count);
        Assert.Equal(2, harness.Store.Data.Count);
        Assert.True(result.Capabilities.HasFlag(SyncDestinationCapabilities.TransactionalBatches));
    }

    [Fact]
    public async Task Parquet_contains_stable_ids_routing_and_opaque_content()
    {
        var transform = SyncTransformVersion.Create("orders", "v1");
        await using var delivery = ChangeDeliveryTestFactory.CreateCommitted(
            Source,
            42,
            new BlueTuskLogSequenceNumber(105));
        var batch = Transaction(transform, delivery.Transaction, "parquet");

        var bytes = await S3SyncParquetCodec.EncodeTransactionAsync(
            "delivery-1",
            batch,
            10,
            1024 * 1024,
            default);

        Assert.NotNull(bytes);
        await using var stream = new MemoryStream(bytes);
        var rows = await ParquetSerializer.DeserializeAsync<S3SyncParquetCodec.S3SyncParquetRow>(stream);
        var row = Assert.Single(rows.Data);
        Assert.Equal("delivery-1", rows.CustomMetadata["bluetusk.delivery-id"]);
        Assert.Equal("delivery-1", row.DeliveryId);
        Assert.Equal("orders", row.Collection);
        Assert.Equal("42", row.Key);
        Assert.Equal("application/json", row.ContentType);
        Assert.Contains(Source.Fingerprint, row.StableId, StringComparison.Ordinal);
        Assert.Equal("{\"stage\":\"parquet\"}", System.Text.Encoding.UTF8.GetString(row.Content));
    }

    [Fact]
    public async Task Crash_after_data_before_manifest_never_advances_and_retry_commits_orphan()
    {
        var store = new DurableStore { FailAfterData = true };
        var transform = SyncTransformVersion.Create("orders", "v1");
        var destination = CreateDestination(store);
        await destination.ProvisionAsync(new SyncProvisionRequest("orders", Source, transform));
        await using var delivery = ChangeDeliveryTestFactory.CreateCommitted(
            Source,
            42,
            new BlueTuskLogSequenceNumber(105));
        var batch = Transaction(transform, delivery.Transaction, "crash");

        await Assert.ThrowsAsync<S3SyncDeliveryException>(
            async () => await destination.ApplyTransactionAsync(batch));
        Assert.Single(store.Data);
        Assert.Empty(store.Manifests);

        var result = await destination.ApplyTransactionAsync(batch);
        Assert.Equal(SyncApplyStatus.Applied, result.Status);
        Assert.Single(store.Data);
        Assert.Single(store.Manifests);
        Assert.Equal(
            SyncApplyStatus.AlreadyApplied,
            (await destination.ApplyTransactionAsync(
                Transaction(transform, delivery.Transaction, "changed"))).Status);
    }

    [Fact]
    public async Task Live_S3_compatible_store_persists_immutable_data_then_manifest()
    {
        var endpoint = Environment.GetEnvironmentVariable("BLUETUSK_S3_ENDPOINT");
        var accessKey = Environment.GetEnvironmentVariable("BLUETUSK_S3_ACCESS_KEY");
        var secretKey = Environment.GetEnvironmentVariable("BLUETUSK_S3_SECRET_KEY");
        if (string.IsNullOrWhiteSpace(endpoint) ||
            string.IsNullOrWhiteSpace(accessKey) ||
            string.IsNullOrWhiteSpace(secretKey))
        {
            throw SkipException.ForSkip(
                "BLUETUSK_S3_ENDPOINT, BLUETUSK_S3_ACCESS_KEY, and BLUETUSK_S3_SECRET_KEY are not configured.");
        }

        using var client = new AmazonS3Client(
            new BasicAWSCredentials(accessKey, secretKey),
            new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true });
        var suffix = Guid.NewGuid().ToString("N");
        var bucket = "bluetusk-sync-" + suffix;
        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        try
        {
            var options = new S3SyncOptions
            {
                Client = client,
                BucketName = bucket,
                Prefix = "pipelines/orders",
                ServerSideEncryption = ServerSideEncryptionMethod.None,
            };
            var transform = SyncTransformVersion.Create("orders", "v1");
            var destination = new S3SyncDestination(options);
            Assert.Equal(
                SyncProvisionStatus.Ready,
                (await destination.ProvisionAsync(
                    new SyncProvisionRequest("orders", Source, transform))).Status);
            await using var delivery = ChangeDeliveryTestFactory.CreateCommitted(
                Source,
                42,
                new BlueTuskLogSequenceNumber(105));
            var batch = Transaction(transform, delivery.Transaction, "live");
            Assert.Equal(SyncApplyStatus.Applied, (await destination.ApplyTransactionAsync(batch)).Status);
            Assert.Equal(
                SyncApplyStatus.AlreadyApplied,
                (await destination.ApplyTransactionAsync(batch)).Status);

            var restarted = new S3SyncDestination(options);
            Assert.Equal(
                SyncProvisionStatus.Ready,
                (await restarted.ProvisionAsync(
                    new SyncProvisionRequest("orders", Source, transform))).Status);
            Assert.Equal(
                SyncApplyStatus.AlreadyApplied,
                (await restarted.ApplyTransactionAsync(batch)).Status);

            var objects = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = "pipelines/orders/",
            });
            Assert.Contains(objects.S3Objects, item => item.Key.Contains("/data/", StringComparison.Ordinal));
            Assert.Contains(objects.S3Objects, item => item.Key.Contains("/commits/", StringComparison.Ordinal));
        }
        finally
        {
            var objects = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
            });
            if (objects.S3Objects.Count > 0)
            {
                _ = await client.DeleteObjectsAsync(new DeleteObjectsRequest
                {
                    BucketName = bucket,
                    Objects = objects.S3Objects.Select(item => new KeyVersion { Key = item.Key }).ToList(),
                });
            }

            _ = await client.DeleteBucketAsync(bucket);
        }
    }

    private static readonly ChangeSourceIdentity Source =
        new("s3-system", "s3-database", "s3-slot", "public:orders");

    private static S3SyncDestination CreateDestination(DurableStore store) => new(new S3SyncOptions
    {
        Client = TestClient,
        BucketName = "bluetusk-tests",
        Prefix = "pipelines/orders",
        ObjectStoreFactory = _ => new FakeStore(store),
    });

    private static readonly IAmazonS3 TestClient = new AmazonS3Client(
        new AnonymousAWSCredentials(),
        new AmazonS3Config
        {
            ServiceURL = "http://127.0.0.1:1",
            ForcePathStyle = true,
        });

    private static SyncTransactionBatch Transaction(
        SyncTransformVersion transform,
        ChangeTransaction transaction,
        string stage) => new(
            "orders",
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

    private sealed class S3Harness : ISyncDestinationConformanceHarness
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
            Assert.IsType<S3SyncDestination>(destination);
            var expected = stage is SyncDestinationConformanceStage.SnapshotApplied or
                SyncDestinationConformanceStage.SnapshotRestart
                ? 4
                : 5;
            Assert.Equal(expected, Store.Manifests.Count);
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class DurableStore
    {
        internal S3SyncConfiguration? Configuration { get; set; }

        internal Dictionary<string, byte[]> Data { get; } = new(StringComparer.Ordinal);

        internal Dictionary<string, byte[]> Manifests { get; } = new(StringComparer.Ordinal);

        internal bool FailAfterData { get; set; }
    }

    private sealed class FakeStore(DurableStore store) : IS3SyncObjectStore
    {
        public ValueTask<S3SyncConfiguration?> LoadConfigurationAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(store.Configuration);
        }

        public ValueTask WriteConfigurationAsync(
            S3SyncConfiguration configuration,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            store.Configuration ??= configuration;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> CommitExistsAsync(
            string manifestKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(store.Manifests.ContainsKey(manifestKey));
        }

        public ValueTask CommitAsync(
            string? dataKey,
            byte[]? parquet,
            string manifestKey,
            byte[] manifest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (dataKey is not null && parquet is not null)
            {
                if (store.Data.TryGetValue(dataKey, out var existing) &&
                    !existing.AsSpan().SequenceEqual(parquet))
                {
                    throw new S3SyncObjectConflictException("Immutable test data changed.");
                }

                store.Data[dataKey] = parquet;
            }

            if (store.FailAfterData)
            {
                store.FailAfterData = false;
                throw new S3SyncDeliveryException(
                    "Simulated failure before commit marker.",
                    new IOException());
            }

            if (store.Manifests.TryGetValue(manifestKey, out var existingManifest) &&
                !existingManifest.AsSpan().SequenceEqual(manifest))
            {
                throw new S3SyncObjectConflictException("Immutable test manifest changed.");
            }

            store.Manifests[manifestKey] = manifest;
            return ValueTask.CompletedTask;
        }
    }
}
