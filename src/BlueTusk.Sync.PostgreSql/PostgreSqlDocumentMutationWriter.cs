using System.Data;
using System.Data.Common;
using System.Globalization;
using BlueTusk.Streams;

namespace BlueTusk.Sync.PostgreSql;

public sealed class PostgreSqlDocumentMutationWriter : IPostgreSqlSyncMutationWriter
{
    private const int MaximumRowsPerCommand = 512;
    private readonly string _documentsTable;

    public PostgreSqlDocumentMutationWriter(string controlSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlSchema);
        _documentsTable = QuoteIdentifier(controlSchema) + ".documents";
    }

    public async ValueTask ResetSnapshotAsync(
        DbConnection connection,
        DbTransaction transaction,
        string pipelineId,
        SnapshotReset reset,
        CancellationToken cancellationToken = default)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            $"DELETE FROM {_documentsTable} WHERE pipeline_id = @pipeline");
        AddParameter(command, "pipeline", pipelineId);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ApplySnapshotBatchAsync(
        DbConnection connection,
        DbTransaction transaction,
        SyncSnapshotBatch batch,
        CancellationToken cancellationToken = default)
    {
        var finalByKey = new Dictionary<(string Collection, string Key), UpsertDocument>();
        foreach (var mutation in batch.Mutations)
        {
            finalByKey[(mutation.Collection, mutation.Key)] = new UpsertDocument(
                mutation.Collection,
                mutation.Key,
                mutation.PartitionKey,
                mutation.ContentType,
                mutation.Content,
                FormatSnapshotId(mutation.RowId),
                mutation.RowId.Epoch);
        }

        await ApplyUpsertsAsync(
            connection,
            transaction,
            batch.PipelineId,
            finalByKey.Values,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ApplyTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        SyncTransactionBatch batch,
        CancellationToken cancellationToken = default)
    {
        var lastCollectionDelete = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < batch.Mutations.Count; index++)
        {
            var mutation = batch.Mutations[index];
            if (mutation.Kind is SyncMutationKind.DeleteCollection)
            {
                lastCollectionDelete[mutation.Collection] = index;
            }
        }

        var finalByKey = new Dictionary<(string Collection, string Key), SyncMutation>();
        for (var index = 0; index < batch.Mutations.Count; index++)
        {
            var mutation = batch.Mutations[index];
            if (mutation.Kind is SyncMutationKind.DeleteCollection ||
                lastCollectionDelete.TryGetValue(mutation.Collection, out var deleteIndex) &&
                index < deleteIndex)
            {
                continue;
            }

            finalByKey[(mutation.Collection, mutation.Key!)] = mutation;
        }

        await DeleteCollectionsAsync(
            connection,
            transaction,
            batch.PipelineId,
            lastCollectionDelete.Keys,
            cancellationToken).ConfigureAwait(false);
        await DeleteKeysAsync(
            connection,
            transaction,
            batch.PipelineId,
            finalByKey.Values.Where(mutation => mutation.Kind is SyncMutationKind.Delete),
            cancellationToken).ConfigureAwait(false);
        await ApplyUpsertsAsync(
            connection,
            transaction,
            batch.PipelineId,
            finalByKey.Values
                .Where(mutation => mutation.Kind is SyncMutationKind.Upsert)
                .Select(mutation => new UpsertDocument(
                    mutation.Collection,
                    mutation.Key!,
                    mutation.PartitionKey,
                    mutation.ContentType!,
                    mutation.Content,
                    FormatChangeId(mutation.ChangeId),
                    SnapshotEpoch: null)),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ApplyUpsertsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string pipelineId,
        IEnumerable<UpsertDocument> upserts,
        CancellationToken cancellationToken)
    {
        foreach (var chunk in upserts.Chunk(MaximumRowsPerCommand))
        {
            var sql = new System.Text.StringBuilder($"""
                INSERT INTO {_documentsTable} (
                    pipeline_id, collection_name, document_key, partition_key,
                    content_type, content, source_change_id, snapshot_epoch, updated_at)
                VALUES
                """);
            await using var command = CreateCommand(connection, transaction, string.Empty);
            AddParameter(command, "pipeline", pipelineId);
            for (var index = 0; index < chunk.Length; index++)
            {
                if (index != 0)
                {
                    sql.Append(',');
                }

                sql.AppendLine();
                sql.Append(CultureInfo.InvariantCulture, $"(@pipeline, @collection{index}, @key{index}, @partition{index}, @content_type{index}, @content{index}, @change_id{index}, @snapshot_epoch{index}, clock_timestamp())");
                var item = chunk[index];
                AddParameter(command, $"collection{index}", item.Collection);
                AddParameter(command, $"key{index}", item.Key);
                AddParameter(command, $"partition{index}", item.PartitionKey, DbType.String);
                AddParameter(command, $"content_type{index}", item.ContentType);
                AddParameter(command, $"content{index}", item.Content.ToArray());
                AddParameter(command, $"change_id{index}", item.ChangeId);
                AddParameter(command, $"snapshot_epoch{index}", item.SnapshotEpoch, DbType.Guid);
            }

            sql.AppendLine();
            sql.Append("""
                ON CONFLICT (pipeline_id, collection_name, document_key)
                DO UPDATE SET partition_key = EXCLUDED.partition_key,
                              content_type = EXCLUDED.content_type,
                              content = EXCLUDED.content,
                              source_change_id = EXCLUDED.source_change_id,
                              snapshot_epoch = EXCLUDED.snapshot_epoch,
                              updated_at = EXCLUDED.updated_at
                """);
            command.CommandText = sql.ToString();
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask DeleteCollectionsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string pipelineId,
        IEnumerable<string> collections,
        CancellationToken cancellationToken)
    {
        foreach (var chunk in collections.Chunk(MaximumRowsPerCommand))
        {
            var predicates = new string[chunk.Length];
            await using var command = CreateCommand(connection, transaction, string.Empty);
            AddParameter(command, "pipeline", pipelineId);
            for (var index = 0; index < chunk.Length; index++)
            {
                predicates[index] = $"@collection{index}";
                AddParameter(command, $"collection{index}", chunk[index]);
            }

            command.CommandText = $"DELETE FROM {_documentsTable} WHERE pipeline_id = @pipeline AND collection_name IN ({string.Join(',', predicates)})";
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask DeleteKeysAsync(
        DbConnection connection,
        DbTransaction transaction,
        string pipelineId,
        IEnumerable<SyncMutation> deletes,
        CancellationToken cancellationToken)
    {
        foreach (var chunk in deletes.Chunk(MaximumRowsPerCommand))
        {
            var predicates = new string[chunk.Length];
            await using var command = CreateCommand(connection, transaction, string.Empty);
            AddParameter(command, "pipeline", pipelineId);
            for (var index = 0; index < chunk.Length; index++)
            {
                predicates[index] = $"(collection_name = @collection{index} AND document_key = @key{index})";
                AddParameter(command, $"collection{index}", chunk[index].Collection);
                AddParameter(command, $"key{index}", chunk[index].Key!);
            }

            command.CommandText = $"DELETE FROM {_documentsTable} WHERE pipeline_id = @pipeline AND ({string.Join(" OR ", predicates)})";
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string FormatChangeId(ChangeId id) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{id.Source.Fingerprint}:{id.CommitEndPosition.Value:X16}:{id.TransactionId}:{id.Ordinal}");

    private static string FormatSnapshotId(SnapshotRowId id) =>
        string.Create(CultureInfo.InvariantCulture, $"{id.Epoch:N}:{id.TableIdentity}:{id.KeyIdentity}");

    private static DbCommand CreateCommand(
        DbConnection connection,
        DbTransaction transaction,
        string commandText)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        return command;
    }

    private static void AddParameter(
        DbCommand command,
        string name,
        object? value,
        DbType? dbType = null)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        if (dbType is not null)
        {
            parameter.DbType = dbType.Value;
        }

        command.Parameters.Add(parameter);
    }

    private static string QuoteIdentifier(string value) =>
        '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    private sealed record UpsertDocument(
        string Collection,
        string Key,
        string? PartitionKey,
        string ContentType,
        ReadOnlyMemory<byte> Content,
        string ChangeId,
        Guid? SnapshotEpoch);
}
