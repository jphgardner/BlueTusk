using System.Globalization;
using BlueTusk.Streams;
using Parquet;
using Parquet.Serialization;

namespace BlueTusk.Sync.S3;

internal static class S3SyncParquetCodec
{
    internal static ValueTask<byte[]?> EncodeTransactionAsync(
        string deliveryId,
        SyncTransactionBatch batch,
        int maximumMutationCount,
        int maximumBytes,
        CancellationToken cancellationToken) =>
        EncodeAsync(
            deliveryId,
            "transaction",
            batch.PipelineId,
            batch.Transform,
            batch.Mutations.Select((mutation, ordinal) => new S3SyncParquetRow
            {
                DeliveryId = deliveryId,
                Ordinal = ordinal,
                StableId = StableChangeId(mutation.ChangeId),
                Kind = mutation.Kind.ToString(),
                Collection = mutation.Collection,
                Key = mutation.Key,
                ContentType = mutation.ContentType,
                PartitionKey = mutation.PartitionKey,
                Content = mutation.Content.ToArray(),
            }),
            maximumMutationCount,
            maximumBytes,
            cancellationToken);

    internal static ValueTask<byte[]?> EncodeSnapshotBatchAsync(
        string deliveryId,
        SyncSnapshotBatch batch,
        int maximumMutationCount,
        int maximumBytes,
        CancellationToken cancellationToken) =>
        EncodeAsync(
            deliveryId,
            "snapshot.batch",
            batch.PipelineId,
            batch.Transform,
            batch.Mutations.Select((mutation, ordinal) => new S3SyncParquetRow
            {
                DeliveryId = deliveryId,
                Ordinal = ordinal,
                StableId = StableSnapshotId(mutation.RowId),
                Kind = SyncMutationKind.Upsert.ToString(),
                Collection = mutation.Collection,
                Key = mutation.Key,
                ContentType = mutation.ContentType,
                PartitionKey = mutation.PartitionKey,
                Content = mutation.Content.ToArray(),
            }),
            maximumMutationCount,
            maximumBytes,
            cancellationToken);

    private static async ValueTask<byte[]?> EncodeAsync(
        string deliveryId,
        string eventName,
        string pipelineId,
        SyncTransformVersion transform,
        IEnumerable<S3SyncParquetRow> source,
        int maximumMutationCount,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var rows = source.Take(maximumMutationCount + 1).ToArray();
        if (rows.Length > maximumMutationCount)
        {
            throw new S3SyncDeliveryException(
                $"The S3 delivery exceeds the configured {maximumMutationCount} mutation limit.",
                new ArgumentOutOfRangeException(nameof(maximumMutationCount)));
        }

        if (rows.Length == 0)
        {
            return null;
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bluetusk.format-version"] = "1",
            ["bluetusk.delivery-id"] = deliveryId,
            ["bluetusk.event"] = eventName,
            ["bluetusk.pipeline"] = pipelineId,
            ["bluetusk.transform"] = transform.Fingerprint,
        };
        await using var stream = new MemoryStream();
#if NET8_0_OR_GREATER
        await ParquetSerializer.SerializeAsync(
            rows,
            stream,
            new ParquetOptions { CompressionMethod = CompressionMethod.Zstd },
            metadata,
            cancellationToken).ConfigureAwait(false);
#else
        await ParquetSerializer.SerializeAsync(
            rows,
            stream,
            new ParquetSerializerOptions { CompressionMethod = CompressionMethod.Zstd },
            cancellationToken).ConfigureAwait(false);

        // Parquet 5's serializer has no metadata parameter. Reopen its existing
        // footer in append mode and update only the file metadata, retaining all
        // row groups and the same Zstandard-compressed column payloads.
        stream.Position = 0;
        Parquet.Schema.ParquetSchema schema;
        using (var reader = await ParquetReader.CreateAsync(
            stream, leaveStreamOpen: true, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            schema = reader.Schema;
        }

        await using (var writer = await ParquetWriter.CreateAsync(
            schema, stream, append: true, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            writer.CustomMetadata = metadata;
        }

        stream.SetLength(stream.Position);
#endif
        if (stream.Length > maximumBytes)
        {
            throw new S3SyncDeliveryException(
                $"The {stream.Length}-byte Parquet object exceeds the configured {maximumBytes}-byte limit.",
                new ArgumentOutOfRangeException(nameof(maximumBytes)));
        }

        return stream.ToArray();
    }

    private static string StableChangeId(ChangeId id) => BlueTuskRuntime.Format(
        CultureInfo.InvariantCulture,
        $"{id.Source.Fingerprint}:{id.CommitEndPosition.Value:x16}:{id.TransactionId:x8}:{id.Ordinal}");

    private static string StableSnapshotId(SnapshotRowId id) => BlueTuskRuntime.Format(
        CultureInfo.InvariantCulture,
        $"{id.Epoch:N}:{id.TableIdentity}:{id.KeyIdentity}");

    internal sealed class S3SyncParquetRow
    {
        public string DeliveryId { get; init; } = string.Empty;

        public int Ordinal { get; init; }

        public string StableId { get; init; } = string.Empty;

        public string Kind { get; init; } = string.Empty;

        public string Collection { get; init; } = string.Empty;

        public string? Key { get; init; }

        public string? ContentType { get; init; }

        public string? PartitionKey { get; init; }

        public byte[] Content { get; init; } = [];
    }
}
