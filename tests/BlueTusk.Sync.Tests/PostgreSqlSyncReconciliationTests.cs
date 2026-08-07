using System.Text;
using BlueTusk.Data;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.Sync.PostgreSql;
using BlueTusk.Sync.Testing;
using BlueTusk.TypeSystem;
using Xunit.Sdk;

namespace BlueTusk.Sync.Tests;

public sealed class PostgreSqlSyncReconciliationTests
{
    private static readonly ChangeSourceIdentity Source =
        new("reconcile-system", "reconcile-database", "reconcile-slot", "public:items");

    [Fact]
    public async Task PostgreSql_partitioned_hash_reconciliation_repairs_without_advancing_checkpoint()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var schema = "bluetusk_sync_reconcile_" + Guid.NewGuid().ToString("N");
        await using var dataSource = BlueTuskDataSource.Create(connectionString);
        var destination = new PostgreSqlSyncDestination(new PostgreSqlSyncOptions
        {
            DestinationDataSource = dataSource,
            ControlSchema = schema,
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
            Assert.True(repaired.RequiresVerification);
            Assert.True(verified.IsMatch);
            Assert.Equal(3, verified.MatchedKeys);
            Assert.Equal(105m, await ReadCheckpointAsync(dataSource, schema));
        }
        finally
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
            _ = await command.ExecuteNonQueryAsync();
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

    private static async ValueTask<decimal?> ReadCheckpointAsync(
        System.Data.Common.DbDataSource dataSource,
        string schema)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT checkpoint_position FROM \"{schema}\".pipelines WHERE pipeline_id = 'reconciliation'";
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() && !reader.IsDBNull(0) ? reader.GetDecimal(0) : null;
    }
}
