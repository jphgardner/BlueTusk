using System.Security.Cryptography;
using System.Text;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;
using StackExchange.Redis;
using Xunit.Sdk;

namespace BlueTusk.Sync.Redis.Tests;

public sealed class RedisSyncDestinationTests
{
    private static readonly ChangeSourceIdentity Source =
        new("redis-system", "redis-database", "redis-slot", "public:orders");

    [Fact]
    public async Task Redis_atomically_materialises_checkpoints_snapshots_and_quarantine()
    {
        var connectionString = GetConnectionString();
        await using var connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var prefix = "bluetusk:sync:test:" + Guid.NewGuid().ToString("N");
        var options = new RedisSyncOptions
        {
            Connection = connection,
            KeyPrefix = prefix,
            MaxDocumentBytes = 1024 * 1024,
            MaxTransactionBytes = 16 * 1024 * 1024,
        };
        var destination = new RedisSyncDestination(options);
        var transform = SyncTransformVersion.Create("orders", "v1");
        var database = connection.GetDatabase();
        try
        {
            var provisioned = await destination.ProvisionAsync(
                new SyncProvisionRequest("orders", Source, transform));
            Assert.Equal(SyncProvisionStatus.Ready, provisioned.Status);

            await using var failingDelivery = ChangeDeliveryTestFactory.CreateCommitted(
                Source,
                40,
                Lsn(100));
            var failingBatch = new SyncTransactionBatch(
                "orders",
                transform,
                failingDelivery.Transaction,
                [
                    Mutation(40, 100, 0, "valid", "1", "{\"valid\":true}"),
                    Mutation(40, 100, 1, "wrong-type", "2", "{\"valid\":false}"),
                ]);
            var wrongTypeKey = CollectionKey(prefix, "orders", "wrong-type");
            _ = await database.StringSetAsync(wrongTypeKey, "not-a-hash");

            await Assert.ThrowsAsync<RedisSyncException>(
                () => destination.ApplyTransactionAsync(failingBatch).AsTask());
            Assert.Null(await destination.ReadDocumentAsync("orders", "valid", "1"));

            _ = await database.KeyDeleteAsync(wrongTypeKey);
            var recovered = await destination.ApplyTransactionAsync(failingBatch);
            Assert.Equal(SyncApplyStatus.Applied, recovered.Status);
            Assert.NotNull(await destination.ReadDocumentAsync("orders", "valid", "1"));

            await using var delivery = ChangeDeliveryTestFactory.CreateCommitted(
                Source,
                42,
                Lsn(105));
            var mutation = Mutation(42, 105, 0, "orders", "42", "{\"status\":\"new\"}");
            var batch = new SyncTransactionBatch(
                "orders",
                transform,
                delivery.Transaction,
                [mutation]);
            var applied = await destination.ApplyTransactionAsync(batch);
            Assert.Equal(SyncApplyStatus.Applied, applied.Status);
            Assert.Equal(Lsn(105), applied.DurablePosition);

            var changedDuplicate = new SyncTransactionBatch(
                "orders",
                transform,
                delivery.Transaction,
                [Mutation(42, 105, 0, "orders", "42", "{\"status\":\"wrong\"}")]);
            var duplicate = await destination.ApplyTransactionAsync(changedDuplicate);
            var document = await destination.ReadDocumentAsync("orders", "orders", "42");
            Assert.Equal(SyncApplyStatus.AlreadyApplied, duplicate.Status);
            Assert.NotNull(document);
            Assert.Equal(
                "{\"status\":\"new\"}",
                Encoding.UTF8.GetString(document.Content.Span));

            await using var bulkDelivery = ChangeDeliveryTestFactory.CreateCommitted(
                Source,
                43,
                Lsn(106));
            var bulk = new SyncTransactionBatch(
                "orders",
                transform,
                bulkDelivery.Transaction,
                [
                    Mutation(43, 106, 0, "orders", "42", "{\"status\":\"discarded\"}"),
                    new SyncMutation(
                        new ChangeId(Source, Lsn(106), 43, 1),
                        SyncMutationKind.DeleteCollection,
                        "orders",
                        null,
                        ReadOnlyMemory<byte>.Empty),
                    Mutation(43, 106, 2, "orders", "42", "{\"status\":\"intermediate\"}"),
                    Mutation(43, 106, 3, "orders", "42", "{\"status\":\"final\"}"),
                    Mutation(43, 106, 4, "orders", "43", "{}"),
                ]);
            _ = await destination.ApplyTransactionAsync(bulk);
            document = await destination.ReadDocumentAsync("orders", "orders", "42");
            Assert.Equal("{\"status\":\"final\"}", Encoding.UTF8.GetString(document!.Content.Span));

            var restarted = new RedisSyncDestination(options);
            var restartProvision = await restarted.ProvisionAsync(
                new SyncProvisionRequest("orders", Source, transform));
            var replay = await restarted.ApplyTransactionAsync(bulk);
            Assert.Equal(SyncProvisionStatus.Ready, restartProvision.Status);
            Assert.Equal(SyncApplyStatus.AlreadyApplied, replay.Status);

            var epoch = new SnapshotEpoch(Guid.NewGuid(), Source, Lsn(300), DateTimeOffset.UtcNow);
            var table = new ChangeTable(
                7,
                "public",
                "orders",
                'd',
                [new ChangeColumn(0, "id", 23, -1, true)]);
            var snapshotBatch = new SyncSnapshotBatch(
                "orders",
                transform,
                new ChangeSnapshotBatch(epoch, table, 0, [], true),
                [new SyncSnapshotMutation(
                    new SnapshotRowId(epoch.Value, "public.orders", "snapshot-50"),
                    "orders",
                    "50",
                    "{\"snapshot\":true}"u8.ToArray(),
                    "application/json")]);
            await restarted.ResetSnapshotAsync(
                "orders",
                new SnapshotReset(epoch, null, "initial bootstrap"));
            Assert.Null(await restarted.ReadDocumentAsync("orders", "orders", "42"));
            await restarted.StartSnapshotAsync("orders", new SnapshotStart(epoch, 1), transform);
            await restarted.ApplySnapshotBatchAsync(snapshotBatch);
            await restarted.CompleteSnapshotAsync(
                "orders",
                new SnapshotComplete(epoch, 1, 1),
                transform);
            Assert.NotNull(await restarted.ReadDocumentAsync("orders", "orders", "50"));
            await Assert.ThrowsAsync<RedisSyncSnapshotException>(
                () => restarted.ApplySnapshotBatchAsync(snapshotBatch).AsTask());

            var quarantine = new SyncQuarantineRecord(
                "orders",
                transform,
                Source,
                44,
                Lsn(301),
                "test",
                "poison",
                DateTimeOffset.UtcNow);
            Assert.True(await restarted.StoreAsync(quarantine));
            Assert.True(await restarted.StoreAsync(quarantine));
            Assert.Equal(1L, await database.HashLengthAsync(QuarantineKey(prefix, "orders")));
            var identity = SyncQuarantineIdentity.FromRecord(quarantine);
            Assert.NotNull(await restarted.ReadAsync(identity));
            await using var replayDelivery = ChangeDeliveryTestFactory.CreateCommitted(
                Source,
                44,
                Lsn(301));
            var replayBatch = new SyncTransactionBatch(
                "orders",
                transform,
                replayDelivery.Transaction,
                [Mutation(44, 301, 0, "orders", "replay", "{\"replayed\":true}")]);
            Assert.Equal(
                SyncQuarantineReplayApplyStatus.Applied,
                (await restarted.ReplayTransactionAsync(replayBatch, "replay-301")).Status);
            Assert.Equal(
                SyncQuarantineReplayApplyStatus.AlreadyApplied,
                (await restarted.ReplayTransactionAsync(replayBatch, "replay-301")).Status);
            Assert.Equal(
                SyncQuarantineResolutionStatus.Resolved,
                (await restarted.ResolveAsync(
                    identity,
                    transform.Fingerprint,
                    "replay-301",
                    DateTimeOffset.UtcNow)).Status);

            await using var laterDelivery = ChangeDeliveryTestFactory.CreateCommitted(
                Source,
                45,
                Lsn(302));
            _ = await restarted.ApplyTransactionAsync(new SyncTransactionBatch(
                "orders",
                transform,
                laterDelivery.Transaction,
                [Mutation(45, 302, 0, "orders", "later", "{}")]));
            Assert.Equal(
                SyncQuarantineReplayApplyStatus.CheckpointAdvanced,
                (await restarted.ReplayTransactionAsync(replayBatch, "replay-301")).Status);

            var changedTransform = SyncTransformVersion.Create("orders", "v2");
            var replacement = new RedisSyncDestination(options);
            var mismatch = await replacement.ProvisionAsync(
                new SyncProvisionRequest("orders", Source, changedTransform));
            Assert.Equal(SyncProvisionStatus.RebuildRequired, mismatch.Status);
            Assert.Equal(transform.Fingerprint, mismatch.ExistingTransformFingerprint);
        }
        finally
        {
            await DeleteKeysAsync(connection, prefix + ":*");
        }
    }

    private static SyncMutation Mutation(
        uint transactionId,
        ulong position,
        int ordinal,
        string collection,
        string key,
        string content) =>
        new(
            new ChangeId(Source, Lsn(position), transactionId, ordinal),
            SyncMutationKind.Upsert,
            collection,
            key,
            Encoding.UTF8.GetBytes(content),
            "application/json");

    private static RedisKey CollectionKey(string prefix, string pipeline, string collection) =>
        Root(prefix, pipeline) + ":collection:" + Fingerprint(collection);

    private static RedisKey QuarantineKey(string prefix, string pipeline) =>
        Root(prefix, pipeline) + ":quarantine";

    private static string Root(string prefix, string pipeline) =>
        prefix + ":{" + Fingerprint(pipeline) + "}";

    private static string Fingerprint(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static async Task DeleteKeysAsync(ConnectionMultiplexer connection, string pattern)
    {
        var database = connection.GetDatabase();
        foreach (var endpoint in connection.GetEndPoints())
        {
            var server = connection.GetServer(endpoint);
            await foreach (var key in server.KeysAsync(pattern: pattern))
            {
                _ = await database.KeyDeleteAsync(key);
            }
        }
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "BLUETUSK_TEST_REDIS_CONNECTION_STRING");
        return string.IsNullOrWhiteSpace(connectionString)
            ? throw SkipException.ForSkip(
                "BLUETUSK_TEST_REDIS_CONNECTION_STRING is not configured.")
            : connectionString;
    }

    private static BlueTuskLogSequenceNumber Lsn(ulong value) => new(value);
}
