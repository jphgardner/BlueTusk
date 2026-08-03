using System.Security.Cryptography;
using System.Text;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.Sync.Testing;
using BlueTusk.TypeSystem;
using StackExchange.Redis;
using Xunit.Sdk;

namespace BlueTusk.Sync.Redis.Tests;

public sealed class RedisSyncReconciliationTests
{
    private static readonly ChangeSourceIdentity Source =
        new("reconcile-system", "reconcile-database", "reconcile-slot", "public:items");

    [Fact]
    public async Task Redis_partitioned_hash_reconciliation_repairs_atomically()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "BLUETUSK_TEST_REDIS_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "BLUETUSK_TEST_REDIS_CONNECTION_STRING is not configured.");
        }

        await using var connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var prefix = "bluetusk:sync:reconcile:" + Guid.NewGuid().ToString("N");
        var destination = new RedisSyncDestination(new RedisSyncOptions
        {
            Connection = connection,
            KeyPrefix = prefix,
            MaxDocumentBytes = 1024 * 1024,
            MaxTransactionBytes = 4 * 1024 * 1024,
        });
        var transform = SyncTransformVersion.Create("reconciliation", "v1");
        try
        {
            _ = await destination.ProvisionAsync(
                new SyncProvisionRequest("reconciliation", Source, transform));
            await using var delivery = ChangeDeliveryTestFactory.CreateCommitted(
                Source,
                42,
                new BlueTuskLogSequenceNumber(105));
            _ = await destination.ApplyTransactionAsync(new SyncTransactionBatch(
                "reconciliation",
                transform,
                delivery.Transaction,
                [
                    Mutation(0, "a", "{\"value\":\"same\"}"),
                    Mutation(1, "c", "{\"value\":\"stale\"}"),
                    Mutation(2, "d", "{\"value\":\"extra\"}"),
                ]));
            var authority = Authority();
            var request = new SyncReconciliationRequest
            {
                PipelineId = "reconciliation",
                Collection = "items",
                PartitionCount = 8,
                Repair = true,
                RepairBatchSize = 2,
            };

            var repaired = await SyncReconciler.ReconcileAsync(
                request,
                authority,
                destination);
            var verified = await SyncReconciler.ReconcileAsync(
                request with { Repair = false },
                authority,
                destination);

            Assert.Equal(3, repaired.RepairedDifferences);
            Assert.True(verified.IsMatch);
            Assert.Equal(3, verified.MatchedKeys);
            var pipelineHash = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes("reconciliation")));
            var checkpoint = await connection.GetDatabase().HashGetAsync(
                $"{prefix}:{{{pipelineHash}}}:state",
                "checkpoint");
            Assert.Equal("0000000000000069", checkpoint.ToString());
        }
        finally
        {
            var database = connection.GetDatabase();
            foreach (var endpoint in connection.GetEndPoints())
            {
                var server = connection.GetServer(endpoint);
                await foreach (var key in server.KeysAsync(pattern: prefix + ":*"))
                {
                    _ = await database.KeyDeleteAsync(key);
                }
            }
        }
    }

    private static SyncReconciliationTestReader Authority() =>
        new(
        [
            Document("a", "{\"value\":\"same\"}"),
            Document("b", "{\"value\":\"missing\"}"),
            Document("c", "{\"value\":\"current\"}"),
        ]);

    private static SyncReconciliationTestDocument Document(string key, string content) =>
        new(key, Encoding.UTF8.GetBytes(content));

    private static SyncMutation Mutation(int ordinal, string key, string content) =>
        new(
            new ChangeId(Source, new BlueTuskLogSequenceNumber(105), 42, ordinal),
            SyncMutationKind.Upsert,
            "items",
            key,
            Encoding.UTF8.GetBytes(content),
            "application/json");
}
