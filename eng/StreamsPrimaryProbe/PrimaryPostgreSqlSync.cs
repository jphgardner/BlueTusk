using System.Data.Common;
using System.Globalization;
using System.Text;
using BlueTusk.Data;
using BlueTusk.Streams;
using BlueTusk.Sync;
using BlueTusk.Sync.PostgreSql;
using BlueTusk.TypeSystem;

internal sealed class PrimaryPostgreSqlSync : IAsyncDisposable
{
    private const string PipelineId = "primary-sync";
    private const string Collection = "primary-results";
    private readonly BlueTuskDataSource _destination;
    private readonly SyncPipeline _pipeline;
    private readonly SingleMutationTransform _transform;

    private PrimaryPostgreSqlSync(BlueTuskDataSource destination, ChangeSourceIdentity source)
    {
        _destination = destination;
        _transform = new SingleMutationTransform();
        var target = new PostgreSqlSyncDestination(new()
        {
            DestinationDataSource = destination,
            ControlSchema = "sync_primary_storage",
        });
        _pipeline = new(new() { PipelineId = PipelineId }, source, _transform, target);
    }

    internal static async Task<PrimaryPostgreSqlSync> OpenAsync(BlueTuskConnection control,
        string sourceConnectionString, ChangeSourceIdentity source, CancellationToken cancellationToken)
    {
        await using (var command = control.CreateCommand())
        {
            command.CommandText = "DROP DATABASE IF EXISTS bluetusk_sync_primary";
            _ = await command.ExecuteNonQueryAsync(cancellationToken);
            command.CommandText = "CREATE DATABASE bluetusk_sync_primary";
            _ = await command.ExecuteNonQueryAsync(cancellationToken);
        }
        var settings = new DbConnectionStringBuilder { ConnectionString = sourceConnectionString };
        settings["Database"] = "bluetusk_sync_primary";
        var destination = new BlueTuskDataSourceBuilder(settings.ConnectionString).Build();
        var sync = new PrimaryPostgreSqlSync(destination, source);
        try
        {
            await sync._pipeline.ProvisionAsync(cancellationToken);
            return sync;
        }
        catch
        {
            await sync.DisposeAsync();
            throw;
        }
    }

    internal async Task ApplyAsync(ChangeTransactionDelivery delivery, long expectedKey, CancellationToken cancellationToken)
    {
        _transform.ExpectedKey = expectedKey;
        await _pipeline.ConsumeTransactionAsync(delivery, cancellationToken);
        if (delivery.State != ChangeDeliveryState.Acknowledged)
        {
            throw new InvalidDataException("Sync did not acknowledge its PostgreSQL commit.");
        }
    }

    internal async Task VerifyAsync(long total, BlueTuskLogSequenceNumber position, uint transactionId,
        CancellationToken cancellationToken)
    {
        if (_pipeline.Status is not { State: SyncPipelineState.Running, QuarantinedTransactions: 0, RetryAttempts: 0 } status ||
            status.AppliedTransactions != total)
        {
            throw new InvalidDataException("Sync did not apply each expected transaction exactly once on its first attempt.");
        }
        await using var connection = await _destination.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT checkpoint_position, checkpoint_transaction_id
            FROM sync_primary_storage.pipelines
            WHERE pipeline_id = 'primary-sync'
            """;
        await using (var checkpoint = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await checkpoint.ReadAsync(cancellationToken) ||
                checkpoint.GetDecimal(0) != position.Value || checkpoint.GetInt64(1) != transactionId ||
                await checkpoint.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException("The durable Sync checkpoint does not match the acknowledged source transaction.");
            }
        }

        command.CommandText = """
            SELECT document_key, content_type, content
            FROM sync_primary_storage.documents
            WHERE pipeline_id = 'primary-sync' AND collection_name = 'primary-results'
            ORDER BY document_key::bigint
            """;
        var nextKey = 1L;
        await using var documents = await command.ExecuteReaderAsync(cancellationToken);
        while (await documents.ReadAsync(cancellationToken))
        {
            var key = nextKey.ToString(CultureInfo.InvariantCulture);
            if (documents.GetString(0) != key || documents.GetString(1) != "application/json" ||
                !documents.GetFieldValue<byte[]>(2).AsSpan().SequenceEqual(Encode(key)))
            {
                throw new InvalidDataException("A committed Sync destination document is missing, duplicated, or malformed.");
            }
            nextKey++;
        }
        if (nextKey != total + 1)
        {
            throw new InvalidDataException("The destination did not retain every expected PostgreSQL mutation.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _pipeline.DisposeAsync(); }
        finally { await _destination.DisposeAsync(); }
    }

    private static byte[] Encode(string key) => Encoding.UTF8.GetBytes($"{{\"id\":{key}}}");

    private sealed class SingleMutationTransform : ISyncTransform
    {
        public SyncTransformVersion Version { get; } = SyncTransformVersion.Create("sync-primary", "1");

        public long ExpectedKey { get; set; }

        public async ValueTask<IReadOnlyList<SyncMutation>> TransformTransactionAsync(ChangeTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            await foreach (var change in transaction.Changes.WithCancellation(cancellationToken))
            {
                var key = ExpectedKey.ToString(CultureInfo.InvariantCulture);
                if (transaction.Changes.Count != 1 || !StreamsPrimaryChange.IsExpectedInsert(change, ExpectedKey))
                {
                    throw new InvalidDataException("The Sync transform did not receive its expected single source mutation.");
                }
                return [new(change.Id, SyncMutationKind.Upsert, Collection, key, Encode(key), "application/json")];
            }
            throw new InvalidDataException("The Sync transform received an empty transaction.");
        }

        public ValueTask<IReadOnlyList<SyncSnapshotMutation>> TransformSnapshotBatchAsync(ChangeSnapshotBatch batch,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The approved Sync workload uses committed changes.");
    }
}
