using System.Buffers;
using System.Globalization;
using System.Text.Json;
using BlueTusk.Streams;

namespace BlueTusk.Sync.Kafka;

internal static class KafkaSyncEnvelopeCodec
{
    internal static byte[] EncodeTransaction(
        string deliveryId,
        SyncTransactionBatch batch,
        int maximumBytes) =>
        Encode(
            "transaction",
            deliveryId,
            batch.PipelineId,
            batch.Transaction.Source,
            batch.Transform,
            maximumBytes,
            writer =>
            {
                writer.WriteStartObject("transaction");
                writer.WriteNumber("transactionId", batch.Transaction.TransactionId);
                writer.WriteString(
                    "commitEndPosition",
                    batch.Transaction.CommitEndPosition.Value.ToString("x16", CultureInfo.InvariantCulture));
                writer.WriteString("commitTimestamp", batch.Transaction.CommitTimestamp);
                writer.WriteString("outcome", batch.Transaction.Outcome.ToString());
                if (batch.Transaction.GlobalTransactionId is not null)
                {
                    writer.WriteString("globalTransactionId", batch.Transaction.GlobalTransactionId);
                }

                writer.WriteEndObject();
                writer.WriteStartArray("mutations");
                foreach (var mutation in batch.Mutations)
                {
                    WriteMutation(
                        writer,
                        StableChangeId(mutation.ChangeId),
                        mutation.Kind,
                        mutation.Collection,
                        mutation.Key,
                        mutation.Content,
                        mutation.ContentType,
                        mutation.PartitionKey);
                }

                writer.WriteEndArray();
            });

    internal static byte[] EncodeSnapshotReset(
        string deliveryId,
        string pipelineId,
        SyncTransformVersion transform,
        SnapshotReset reset,
        int maximumBytes) =>
        EncodeSnapshot(
            "snapshot.reset",
            deliveryId,
            pipelineId,
            transform,
            reset.Epoch,
            maximumBytes,
            writer =>
            {
                if (reset.AbandonedEpoch.HasValue)
                {
                    writer.WriteString("abandonedEpoch", reset.AbandonedEpoch.Value);
                }

                writer.WriteString("reason", reset.Reason);
            });

    internal static byte[] EncodeSnapshotStart(
        string deliveryId,
        string pipelineId,
        SyncTransformVersion transform,
        SnapshotStart start,
        int maximumBytes) =>
        EncodeSnapshot(
            "snapshot.start",
            deliveryId,
            pipelineId,
            transform,
            start.Epoch,
            maximumBytes,
            writer => writer.WriteNumber("tableCount", start.TableCount));

    internal static byte[] EncodeSnapshotBatch(
        string deliveryId,
        SyncSnapshotBatch batch,
        int maximumBytes) =>
        EncodeSnapshot(
            "snapshot.batch",
            deliveryId,
            batch.PipelineId,
            batch.Transform,
            batch.SourceBatch.Epoch,
            maximumBytes,
            writer =>
            {
                writer.WriteString(
                    "table",
                    batch.SourceBatch.Table.Schema + "." + batch.SourceBatch.Table.Name);
                writer.WriteNumber("sequence", batch.SourceBatch.Sequence);
                writer.WriteBoolean("lastForTable", batch.SourceBatch.IsLastForTable);
                writer.WriteStartArray("mutations");
                foreach (var mutation in batch.Mutations)
                {
                    WriteMutation(
                        writer,
                        StableSnapshotId(mutation.RowId),
                        SyncMutationKind.Upsert,
                        mutation.Collection,
                        mutation.Key,
                        mutation.Content,
                        mutation.ContentType,
                        mutation.PartitionKey);
                }

                writer.WriteEndArray();
            });

    internal static byte[] EncodeSnapshotComplete(
        string deliveryId,
        string pipelineId,
        SyncTransformVersion transform,
        SnapshotComplete complete,
        int maximumBytes) =>
        EncodeSnapshot(
            "snapshot.complete",
            deliveryId,
            pipelineId,
            transform,
            complete.Epoch,
            maximumBytes,
            writer =>
            {
                writer.WriteNumber("rowCount", complete.RowCount);
                writer.WriteNumber("tableCount", complete.TableCount);
            });

    private static byte[] EncodeSnapshot(
        string eventName,
        string deliveryId,
        string pipelineId,
        SyncTransformVersion transform,
        SnapshotEpoch epoch,
        int maximumBytes,
        Action<Utf8JsonWriter> writeSnapshot) =>
        Encode(
            eventName,
            deliveryId,
            pipelineId,
            epoch.Source,
            transform,
            maximumBytes,
            writer =>
            {
                writer.WriteStartObject("snapshot");
                writer.WriteString("epoch", epoch.Value);
                writer.WriteString(
                    "consistentPosition",
                    epoch.ConsistentPosition.Value.ToString("x16", CultureInfo.InvariantCulture));
                writer.WriteString("startedAt", epoch.StartedAt);
                writeSnapshot(writer);
                writer.WriteEndObject();
            });

    private static byte[] Encode(
        string eventName,
        string deliveryId,
        string pipelineId,
        ChangeSourceIdentity source,
        SyncTransformVersion transform,
        int maximumBytes,
        Action<Utf8JsonWriter> writeEvent)
    {
        var buffer = new ArrayBufferWriter<byte>(Math.Min(maximumBytes, 16 * 1024));
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", KafkaSyncProtocol.CurrentFormatVersion);
            writer.WriteString("event", eventName);
            writer.WriteString("deliveryId", deliveryId);
            writer.WriteString("pipelineId", pipelineId);
            writer.WriteStartObject("source");
            writer.WriteString("systemIdentifier", source.SystemIdentifier);
            writer.WriteString("databaseName", source.DatabaseName);
            writer.WriteString("slotName", source.SlotName);
            writer.WriteString("publicationFingerprint", source.PublicationFingerprint);
            writer.WriteString("fingerprint", source.Fingerprint);
            writer.WriteEndObject();
            writer.WriteStartObject("transform");
            writer.WriteString("name", transform.Name);
            writer.WriteString("fingerprint", transform.Fingerprint);
            writer.WriteEndObject();
            writeEvent(writer);
            writer.WriteEndObject();
        }

        if (buffer.WrittenCount > maximumBytes)
        {
            throw new KafkaSyncEnvelopeException(
                $"The {buffer.WrittenCount}-byte Kafka envelope exceeds the configured {maximumBytes}-byte limit.");
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteMutation(
        Utf8JsonWriter writer,
        string stableId,
        SyncMutationKind kind,
        string collection,
        string? key,
        ReadOnlyMemory<byte> content,
        string? contentType,
        string? partitionKey)
    {
        writer.WriteStartObject();
        writer.WriteString("stableId", stableId);
        writer.WriteString("kind", kind.ToString());
        writer.WriteString("collection", collection);
        if (key is not null)
        {
            writer.WriteString("key", key);
        }

        if (contentType is not null)
        {
            writer.WriteString("contentType", contentType);
        }

        if (partitionKey is not null)
        {
            writer.WriteString("partitionKey", partitionKey);
        }

        if (!content.IsEmpty)
        {
            writer.WriteBase64String("contentBase64", content.Span);
        }

        writer.WriteEndObject();
    }

    private static string StableChangeId(ChangeId id) => string.Create(
        CultureInfo.InvariantCulture,
        $"{id.Source.Fingerprint}:{id.CommitEndPosition.Value:x16}:{id.TransactionId:x8}:{id.Ordinal}");

    private static string StableSnapshotId(SnapshotRowId id) => string.Create(
        CultureInfo.InvariantCulture,
        $"{id.Epoch:N}:{id.TableIdentity}:{id.KeyIdentity}");
}
