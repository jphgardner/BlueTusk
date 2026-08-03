using System.Data.Common;
using System.Text;
using BlueTusk.Data;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.Sync.PostgreSql;
using BlueTusk.TypeSystem;
using Xunit.Sdk;

namespace BlueTusk.Sync.Tests;

public sealed class PostgreSqlSyncDestinationTests
{
    private static readonly ChangeSourceIdentity Source =
        new("sync-pg-system", "sync-db", "sync-slot", "public:orders");

    [Fact]
    public async Task PostgreSql_destination_migrates_version_one_quarantine_storage()
    {
        var connectionString = GetConnectionString();
        var schema = "bluetusk_sync_migration_" + Guid.NewGuid().ToString("N");
        await using var dataSource = BlueTuskDataSource.Create(connectionString);
        try
        {
            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    CREATE SCHEMA "{schema}";
                    CREATE TABLE "{schema}".storage_metadata (
                        singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
                        schema_version integer NOT NULL CHECK (schema_version > 0),
                        updated_at timestamptz NOT NULL DEFAULT clock_timestamp());
                    INSERT INTO "{schema}".storage_metadata (singleton, schema_version)
                    VALUES (true, 1);
                    CREATE TABLE "{schema}".quarantine (
                        pipeline_id text NOT NULL,
                        source_fingerprint text NOT NULL,
                        transaction_id bigint NOT NULL CHECK (transaction_id >= 0),
                        commit_position numeric(20, 0) NOT NULL CHECK (commit_position >= 0),
                        transform_fingerprint char(64) NOT NULL,
                        error_type text NOT NULL,
                        error_message text NOT NULL,
                        recorded_at timestamptz NOT NULL,
                        PRIMARY KEY (pipeline_id, source_fingerprint, commit_position, transaction_id));
                    """;
                _ = await command.ExecuteNonQueryAsync();
            }

            var destination = new PostgreSqlSyncDestination(Options(dataSource, schema));
            await destination.InitializeAsync();

            await using var inspection = await dataSource.OpenConnectionAsync();
            await using var version = inspection.CreateCommand();
            version.CommandText = $"SELECT schema_version FROM \"{schema}\".storage_metadata WHERE singleton";
            Assert.Equal(
                PostgreSqlSyncDestination.CurrentSchemaVersion,
                Convert.ToInt32(await version.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
            await using var columns = inspection.CreateCommand();
            columns.CommandText = """
                SELECT count(*) FROM information_schema.columns
                WHERE table_schema = @schema AND table_name = 'quarantine'
                  AND column_name IN ('resolved_operation_id', 'resolved_at')
                """;
            var parameter = columns.CreateParameter();
            parameter.ParameterName = "schema";
            parameter.Value = schema;
            columns.Parameters.Add(parameter);
            Assert.Equal(
                2,
                Convert.ToInt32(await columns.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            await DropSchemaAsync(dataSource, schema);
        }
    }

    [Fact]
    public async Task PostgreSql_destination_atomically_applies_deduplicates_and_quarantines()
    {
        var connectionString = GetConnectionString();
        var schema = "bluetusk_sync_test_" + Guid.NewGuid().ToString("N");
        await using var dataSource = BlueTuskDataSource.Create(connectionString);
        var options = Options(dataSource, schema);
        var destination = new PostgreSqlSyncDestination(options);
        var transform = SyncTransformVersion.Create("orders", "v1");
        try
        {
            var provisioned = await destination.ProvisionAsync(
                new SyncProvisionRequest("orders", Source, transform));
            Assert.Equal(SyncProvisionStatus.Ready, provisioned.Status);
            await using var delivery = ChangeDeliveryTestFactory.CreateCommitted(
                Source,
                42,
                Lsn(105));
            var mutation = new SyncMutation(
                new ChangeId(Source, Lsn(105), 42, 0),
                SyncMutationKind.Upsert,
                "orders",
                "42",
                "{\"status\":\"new\"}"u8.ToArray(),
                "application/json");
            var batch = new SyncTransactionBatch(
                "orders",
                transform,
                delivery.Transaction,
                [mutation]);

            var applied = await destination.ApplyTransactionAsync(batch);
            Assert.Equal(SyncApplyStatus.Applied, applied.Status);
            Assert.Equal(Lsn(105), applied.DurablePosition);
            Assert.Equal("{\"status\":\"new\"}", await ReadDocumentAsync(dataSource, schema));
            Assert.Equal(105m, await ReadCheckpointAsync(dataSource, schema));

            var changedDuplicate = new SyncTransactionBatch(
                "orders",
                transform,
                delivery.Transaction,
                [new SyncMutation(
                    mutation.ChangeId,
                    mutation.Kind,
                    mutation.Collection,
                    mutation.Key,
                    "{\"status\":\"wrong\"}"u8.ToArray(),
                    mutation.ContentType,
                    mutation.PartitionKey)]);
            var duplicate = await destination.ApplyTransactionAsync(changedDuplicate);
            Assert.Equal(SyncApplyStatus.AlreadyApplied, duplicate.Status);
            Assert.Equal("{\"status\":\"new\"}", await ReadDocumentAsync(dataSource, schema));

            await using var bulkDelivery = ChangeDeliveryTestFactory.CreateCommitted(
                Source,
                43,
                Lsn(106));
            var bulkMutations = new List<SyncMutation>
            {
                Mutation(43, 106, 0, SyncMutationKind.Upsert, "orders", "42", "{\"status\":\"discarded\"}"),
                Mutation(43, 106, 1, SyncMutationKind.DeleteCollection, "orders", null, null),
                Mutation(43, 106, 2, SyncMutationKind.Upsert, "orders", "42", "{\"status\":\"intermediate\"}"),
                Mutation(43, 106, 3, SyncMutationKind.Upsert, "orders", "42", "{\"status\":\"final\"}"),
            };
            for (var index = 0; index < 600; index++)
            {
                bulkMutations.Add(Mutation(
                    43,
                    106,
                    index + 4,
                    SyncMutationKind.Upsert,
                    "orders",
                    "bulk-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "{}"));
            }

            var bulk = await destination.ApplyTransactionAsync(
                new SyncTransactionBatch(
                    "orders",
                    transform,
                    bulkDelivery.Transaction,
                    bulkMutations));
            Assert.Equal(SyncApplyStatus.Applied, bulk.Status);
            Assert.Equal("{\"status\":\"final\"}", await ReadDocumentAsync(dataSource, schema));
            Assert.Equal(601L, await ReadCountAsync(dataSource, schema, "documents"));

            var changedTransform = SyncTransformVersion.Create("orders", "v2");
            var mismatch = await destination.ApplyTransactionAsync(
                new SyncTransactionBatch(
                    "orders",
                    changedTransform,
                    delivery.Transaction,
                    [mutation]));
            Assert.Equal(SyncApplyStatus.TransformVersionMismatch, mismatch.Status);
            Assert.Equal(transform.Fingerprint, mismatch.Detail);

            var quarantine = new SyncQuarantineRecord(
                "orders",
                transform,
                Source,
                44,
                Lsn(107),
                "test",
                "poison",
                DateTimeOffset.UtcNow);
            Assert.True(await destination.StoreAsync(quarantine));
            Assert.True(await destination.StoreAsync(quarantine));
            Assert.Equal(1L, await ReadCountAsync(dataSource, schema, "quarantine"));
            var identity = SyncQuarantineIdentity.FromRecord(quarantine);
            Assert.NotNull(await destination.ReadAsync(identity));
            await using var replayDelivery = ChangeDeliveryTestFactory.CreateCommitted(
                Source,
                44,
                Lsn(107));
            var replayBatch = new SyncTransactionBatch(
                "orders",
                transform,
                replayDelivery.Transaction,
                [Mutation(44, 107, 0, SyncMutationKind.Upsert, "orders", "replay", "{\"replayed\":true}")]);
            Assert.Equal(
                SyncQuarantineReplayApplyStatus.Applied,
                (await destination.ReplayTransactionAsync(replayBatch, "replay-107")).Status);
            Assert.Equal(
                SyncQuarantineReplayApplyStatus.AlreadyApplied,
                (await destination.ReplayTransactionAsync(replayBatch, "replay-107")).Status);
            Assert.Equal(
                SyncQuarantineResolutionStatus.Resolved,
                (await destination.ResolveAsync(
                    identity,
                    transform.Fingerprint,
                    "replay-107",
                    DateTimeOffset.UtcNow)).Status);

            await using var laterDelivery = ChangeDeliveryTestFactory.CreateCommitted(
                Source,
                45,
                Lsn(108));
            _ = await destination.ApplyTransactionAsync(new SyncTransactionBatch(
                "orders",
                transform,
                laterDelivery.Transaction,
                [Mutation(45, 108, 0, SyncMutationKind.Upsert, "orders", "later", "{}")]));
            Assert.Equal(
                SyncQuarantineReplayApplyStatus.CheckpointAdvanced,
                (await destination.ReplayTransactionAsync(replayBatch, "replay-107")).Status);
        }
        finally
        {
            await DropSchemaAsync(dataSource, schema);
        }
    }

    [Fact]
    public async Task PostgreSql_destination_rolls_back_mutations_when_checkpoint_cannot_commit()
    {
        var connectionString = GetConnectionString();
        var schema = "bluetusk_sync_rollback_" + Guid.NewGuid().ToString("N");
        await using var dataSource = BlueTuskDataSource.Create(connectionString);
        var failingWriter = new FailingWriter(new PostgreSqlDocumentMutationWriter(schema));
        var destination = new PostgreSqlSyncDestination(
            Options(dataSource, schema) with { MutationWriter = failingWriter });
        Assert.False(
            destination.Capabilities.HasFlag(SyncDestinationCapabilities.Reconciliation));
        var transform = SyncTransformVersion.Create("orders", "v1");
        try
        {
            _ = await destination.ProvisionAsync(
                new SyncProvisionRequest("orders", Source, transform));
            await using var delivery = ChangeDeliveryTestFactory.CreateCommitted(
                Source,
                50,
                Lsn(200));
            var batch = new SyncTransactionBatch(
                "orders",
                transform,
                delivery.Transaction,
                [new SyncMutation(
                    new ChangeId(Source, Lsn(200), 50, 0),
                    SyncMutationKind.Upsert,
                    "orders",
                    "50",
                    "{}"u8.ToArray(),
                    "application/json")]);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => destination.ApplyTransactionAsync(batch).AsTask());

            Assert.Equal(0L, await ReadCountAsync(dataSource, schema, "documents"));
            Assert.Null(await ReadCheckpointAsync(dataSource, schema));
        }
        finally
        {
            await DropSchemaAsync(dataSource, schema);
        }
    }

    [Fact]
    public async Task PostgreSql_destination_guards_snapshot_epoch_and_completion()
    {
        var connectionString = GetConnectionString();
        var schema = "bluetusk_sync_snapshot_" + Guid.NewGuid().ToString("N");
        await using var dataSource = BlueTuskDataSource.Create(connectionString);
        var destination = new PostgreSqlSyncDestination(Options(dataSource, schema));
        var transform = SyncTransformVersion.Create("orders", "v1");
        var epoch = SnapshotEpoch.Create(Source, Lsn(300));
        var table = new ChangeTable(
            7,
            "public",
            "orders",
            'd',
            [new ChangeColumn(0, "id", 23, -1, true)]);
        var sourceBatch = new ChangeSnapshotBatch(epoch, table, 0, [], true);
        var batch = new SyncSnapshotBatch(
            "orders",
            transform,
            sourceBatch,
            [new SyncSnapshotMutation(
                new SnapshotRowId(epoch.Value, "public.orders", "42"),
                "orders",
                "42",
                "{\"snapshot\":true}"u8.ToArray(),
                "application/json")]);
        try
        {
            _ = await destination.ProvisionAsync(
                new SyncProvisionRequest("orders", Source, transform));
            await destination.ResetSnapshotAsync(
                "orders",
                new SnapshotReset(epoch, null, "initial"));
            await destination.StartSnapshotAsync(
                "orders",
                new SnapshotStart(epoch, 1),
                transform);
            await destination.ApplySnapshotBatchAsync(batch);
            await destination.CompleteSnapshotAsync(
                "orders",
                new SnapshotComplete(epoch, 1, 1),
                transform);

            Assert.Equal(1L, await ReadCountAsync(dataSource, schema, "documents"));
            await Assert.ThrowsAsync<PostgreSqlSyncSnapshotException>(
                () => destination.ApplySnapshotBatchAsync(batch).AsTask());
        }
        finally
        {
            await DropSchemaAsync(dataSource, schema);
        }
    }

    private static PostgreSqlSyncOptions Options(DbDataSource dataSource, string schema) =>
        new()
        {
            DestinationDataSource = dataSource,
            ControlSchema = schema,
            MaxDocumentBytes = 1024 * 1024,
            MaxTransactionBytes = 4 * 1024 * 1024,
        };

    private static SyncMutation Mutation(
        uint transactionId,
        ulong position,
        int ordinal,
        SyncMutationKind kind,
        string collection,
        string? key,
        string? json) =>
        new(
            new ChangeId(Source, Lsn(position), transactionId, ordinal),
            kind,
            collection,
            key,
            json is null ? ReadOnlyMemory<byte>.Empty : Encoding.UTF8.GetBytes(json),
            json is null ? null : "application/json");

    private static async ValueTask<string?> ReadDocumentAsync(DbDataSource dataSource, string schema)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT content FROM \"{schema}\".documents WHERE pipeline_id = 'orders' AND document_key = '42'";
        var value = await command.ExecuteScalarAsync();
        return value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : null;
    }

    private static async ValueTask<decimal?> ReadCheckpointAsync(DbDataSource dataSource, string schema)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT checkpoint_position FROM \"{schema}\".pipelines WHERE pipeline_id = 'orders'";
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() && !reader.IsDBNull(0) ? reader.GetDecimal(0) : null;
    }

    private static async ValueTask<long> ReadCountAsync(
        DbDataSource dataSource,
        string schema,
        string table)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM \"{schema}\".\"{table}\"";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async ValueTask DropSchemaAsync(DbDataSource dataSource, string schema)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
        _ = await command.ExecuteNonQueryAsync();
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        return string.IsNullOrWhiteSpace(connectionString)
            ? throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.")
            : connectionString;
    }

    private static BlueTuskLogSequenceNumber Lsn(ulong value) => new(value);

    private sealed class FailingWriter(IPostgreSqlSyncMutationWriter inner)
        : IPostgreSqlSyncMutationWriter
    {
        public ValueTask ResetSnapshotAsync(
            DbConnection connection,
            DbTransaction transaction,
            string pipelineId,
            SnapshotReset reset,
            CancellationToken cancellationToken = default) =>
            inner.ResetSnapshotAsync(connection, transaction, pipelineId, reset, cancellationToken);

        public ValueTask ApplySnapshotBatchAsync(
            DbConnection connection,
            DbTransaction transaction,
            SyncSnapshotBatch batch,
            CancellationToken cancellationToken = default) =>
            inner.ApplySnapshotBatchAsync(connection, transaction, batch, cancellationToken);

        public async ValueTask ApplyTransactionAsync(
            DbConnection connection,
            DbTransaction transaction,
            SyncTransactionBatch batch,
            CancellationToken cancellationToken = default)
        {
            await inner.ApplyTransactionAsync(connection, transaction, batch, cancellationToken);
            throw new InvalidOperationException("Injected checkpoint-boundary failure.");
        }
    }
}
