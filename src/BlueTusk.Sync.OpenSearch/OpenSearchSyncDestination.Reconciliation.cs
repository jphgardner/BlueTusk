using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace BlueTusk.Sync.OpenSearch;

public sealed partial class OpenSearchSyncDestination
{
    /// <inheritdoc />
    public async ValueTask<long> CountAsync(
        string pipelineId,
        string collection,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        var runtime = RequirePipeline(pipelineId);
        await using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);
        var definition = await RequireCollectionAsync(runtime, collection, cancellationToken)
            .ConfigureAwait(false);
        var materializedCount = await CountDocumentsAsync(definition.Index, cancellationToken)
            .ConfigureAwait(false);
        var reconciliationCount = await CountDocumentsAsync(
            ReconciliationIndex(definition),
            cancellationToken).ConfigureAwait(false);
        if (materializedCount != reconciliationCount)
        {
            throw new SyncReconciliationException(
                $"OpenSearch collection '{collection}' contains {materializedCount} materialized documents but {reconciliationCount} reconciliation records. Replay the unacknowledged transaction or rebuild the generation before reconciliation.");
        }

        return materializedCount;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SyncReconciliationEntry> ReadPartitionAsync(
        string pipelineId,
        string collection,
        int partitionIndex,
        int partitionCount,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentOutOfRangeException.ThrowIfLessThan(partitionCount, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(partitionIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(partitionIndex, partitionCount);
        var runtime = RequirePipeline(pipelineId);
        await using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);
        var definition = await RequireCollectionAsync(runtime, collection, cancellationToken)
            .ConfigureAwait(false);
        var index = ReconciliationIndex(definition);
        var (minimum, maximum) = PartitionHashRange(partitionIndex, partitionCount);
        object[]? searchAfter = null;
        while (true)
        {
            var payload = BuildReconciliationSearchPayload(
                minimum,
                maximum,
                searchAfter,
                _options.ReconciliationPageSize);
            var response = await SendAsync(
                HttpMethod.Post,
                $"{index}/_search",
                payload,
                "application/json",
                cancellationToken,
                System.Net.HttpStatusCode.OK).ConfigureAwait(false);
            using var document = JsonDocument.Parse(response.Content);
            var hits = document.RootElement.GetProperty("hits").GetProperty("hits");
            if (hits.GetArrayLength() == 0)
            {
                yield break;
            }

            JsonElement lastHit = default;
            foreach (var hit in hits.EnumerateArray())
            {
                lastHit = hit;
                var source = hit.GetProperty("_source");
                ValidateReconciliationDocument(runtime, definition, source);
                yield return new SyncReconciliationEntry(
                    source.GetProperty("key").GetString()!,
                    source.GetProperty("contentHash").GetString()!);
            }

            if (hits.GetArrayLength() < _options.ReconciliationPageSize)
            {
                yield break;
            }

            var sort = lastHit.GetProperty("sort");
            searchAfter = [
                sort[0].GetUInt64(),
                sort[1].GetString()!,
            ];
        }
    }

    /// <inheritdoc />
    public async ValueTask ApplyRepairBatchAsync(
        SyncRepairBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ValidateLimits(
            batch.Mutations.Count,
            batch.Mutations
                .Where(static mutation => mutation.Document is not null)
                .Select(static mutation => mutation.Document!.Content));
        foreach (var mutation in batch.Mutations)
        {
            ValidateReconciliationKey(mutation.Key);
            if (mutation.Document is not null)
            {
                ValidateJsonDocument(mutation.Document.Content, mutation.Document.ContentType);
            }
        }

        var runtime = RequirePipeline(batch.PipelineId);
        await using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);
        var collection = await RequireCollectionAsync(
            runtime,
            batch.Collection,
            cancellationToken).ConfigureAwait(false);
        var operations = batch.Mutations
            .Select(mutation => new MaterializedOperation(
                mutation.Kind is SyncRepairMutationKind.Upsert
                    ? SyncMutationKind.Upsert
                    : SyncMutationKind.Delete,
                collection,
                mutation.Key,
                StableDocumentId(batch.Collection, mutation.Key),
                mutation.Document?.Content ?? ReadOnlyMemory<byte>.Empty,
                mutation.Document?.ContentType,
                mutation.Document?.PartitionKey))
            .ToArray();
        var checkpoint = await ReadCheckpointAsync(runtime, cancellationToken).ConfigureAwait(false);
        long? externalVersion = checkpoint is null
            ? null
            : ToExternalVersion(
                new BlueTusk.TypeSystem.BlueTuskLogSequenceNumber(checkpoint.Position));
        await ExecuteBulkAsync(operations, externalVersion, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<CollectionDocument> RequireCollectionAsync(
        PipelineRuntime runtime,
        string collection,
        CancellationToken cancellationToken)
    {
        var cached = runtime.GetCollection(collection);
        if (cached is not null)
        {
            return cached;
        }

        var document = await ReadControlDocumentAsync<CollectionDocument>(
            CollectionDocumentId(runtime, collection),
            cancellationToken).ConfigureAwait(false);
        var definition = document?.Source;
        if (definition is null ||
            definition.FormatVersion != CurrentFormatVersion ||
            !string.Equals(definition.RecordType, "collection", StringComparison.Ordinal) ||
            !string.Equals(definition.PipelineHash, runtime.PipelineHash, StringComparison.Ordinal) ||
            !string.Equals(definition.Generation, runtime.Generation, StringComparison.Ordinal) ||
            !string.Equals(definition.Collection, collection, StringComparison.Ordinal) ||
            !string.Equals(definition.Index, CollectionIndex(runtime, collection), StringComparison.Ordinal) ||
            !string.Equals(definition.Alias, CollectionAlias(runtime, collection), StringComparison.Ordinal))
        {
            throw new SyncReconciliationException(
                $"OpenSearch collection '{collection}' is not registered for pipeline '{runtime.PipelineId}' and generation '{runtime.Generation}'.");
        }

        runtime.CacheCollection(definition);
        return definition;
    }

    private static (ulong Minimum, ulong Maximum) PartitionHashRange(
        int partitionIndex,
        int partitionCount)
    {
        const ulong hashRange = 1UL << 32;
        var minimum = (hashRange * (ulong)partitionIndex) / (ulong)partitionCount;
        var exclusiveMaximum = (hashRange * (ulong)(partitionIndex + 1)) /
            (ulong)partitionCount;
        return (minimum, exclusiveMaximum - 1);
    }

    private static byte[] BuildReconciliationSearchPayload(
        ulong minimum,
        ulong maximum,
        object[]? searchAfter,
        int pageSize)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteNumber("size", pageSize);
        writer.WriteBoolean("track_total_hits", false);
        writer.WritePropertyName("_source");
        writer.WriteStartArray();
        writer.WriteStringValue("recordType");
        writer.WriteStringValue("formatVersion");
        writer.WriteStringValue("pipelineHash");
        writer.WriteStringValue("generation");
        writer.WriteStringValue("collection");
        writer.WriteStringValue("key");
        writer.WriteStringValue("keyHash");
        writer.WriteStringValue("contentHash");
        writer.WriteEndArray();
        writer.WritePropertyName("query");
        writer.WriteStartObject();
        writer.WritePropertyName("range");
        writer.WriteStartObject();
        writer.WritePropertyName("keyHash");
        writer.WriteStartObject();
        writer.WriteNumber("gte", minimum);
        writer.WriteNumber("lte", maximum);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WritePropertyName("sort");
        writer.WriteStartArray();
        WriteSort(writer, "keyHash");
        WriteSort(writer, "key");
        writer.WriteEndArray();
        if (searchAfter is not null)
        {
            writer.WritePropertyName("search_after");
            writer.WriteStartArray();
            writer.WriteNumberValue((ulong)searchAfter[0]);
            writer.WriteStringValue((string)searchAfter[1]);
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteSort(Utf8JsonWriter writer, string property)
    {
        writer.WriteStartObject();
        writer.WritePropertyName(property);
        writer.WriteStartObject();
        writer.WriteString("order", "asc");
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void ValidateReconciliationDocument(
        PipelineRuntime runtime,
        CollectionDocument collection,
        JsonElement source)
    {
        if (!source.TryGetProperty("recordType", out var recordType) ||
            !string.Equals(recordType.GetString(), "document", StringComparison.Ordinal) ||
            !source.TryGetProperty("formatVersion", out var formatVersion) ||
            formatVersion.GetInt32() != CurrentFormatVersion ||
            !source.TryGetProperty("pipelineHash", out var pipelineHash) ||
            !string.Equals(pipelineHash.GetString(), runtime.PipelineHash, StringComparison.Ordinal) ||
            !source.TryGetProperty("generation", out var generation) ||
            !string.Equals(generation.GetString(), runtime.Generation, StringComparison.Ordinal) ||
            !source.TryGetProperty("collection", out var collectionName) ||
            !string.Equals(collectionName.GetString(), collection.Collection, StringComparison.Ordinal) ||
            !source.TryGetProperty("key", out var key) ||
            string.IsNullOrWhiteSpace(key.GetString()) ||
            !source.TryGetProperty("keyHash", out var keyHash) ||
            keyHash.GetUInt64() != SyncReconciler.GetKeyHash(key.GetString()!) ||
            !source.TryGetProperty("contentHash", out var contentHash) ||
            contentHash.GetString() is not { Length: 64 })
        {
            throw new SyncReconciliationException(
                $"OpenSearch returned an incompatible reconciliation record for collection '{collection.Collection}'.");
        }
    }
}
