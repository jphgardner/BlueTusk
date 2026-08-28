using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace BlueTusk.Sync.OpenSearch;

public sealed partial class OpenSearchSyncDestination
{
    private static void EnsurePipelineDocument(
        ControlDocument<PipelineDocument> document,
        string pipelineId,
        string expectedPipelineHash,
        BlueTusk.Streams.ChangeSourceIdentity source)
    {
        if (document.Source.FormatVersion != CurrentFormatVersion ||
            !string.Equals(document.Source.RecordType, "pipeline", StringComparison.Ordinal) ||
            !string.Equals(
                document.Source.PipelineHash,
                expectedPipelineHash,
                StringComparison.Ordinal))
        {
            throw new OpenSearchSyncException(
                $"OpenSearch Sync control metadata for pipeline '{pipelineId}' has an incompatible format or identity.");
        }

        if (!string.Equals(
                document.Source.SourceFingerprint,
                source.Fingerprint,
                StringComparison.Ordinal))
        {
            throw new OpenSearchSyncSourceMismatchException(
                $"OpenSearch Sync pipeline '{pipelineId}' belongs to source '{document.Source.SourceFingerprint}', not '{source.Fingerprint}'.");
        }
    }

    private async ValueTask<CollectionDocument> EnsureCollectionAsync(
        PipelineRuntime runtime,
        string collection,
        CancellationToken cancellationToken)
    {
        var cached = runtime.GetCollection(collection);
        if (cached is not null)
        {
            return cached;
        }

        var definition = new CollectionDocument(
            "collection",
            CurrentFormatVersion,
            runtime.PipelineHash,
            runtime.Generation,
            collection,
            CollectionIndex(runtime, collection),
            CollectionAlias(runtime, collection));
        var createResponse = await SendAsync(
            HttpMethod.Put,
            definition.Index,
            BuildIndexDefinition(runtime, definition),
            "application/json",
            cancellationToken,
            HttpStatusCode.OK,
            HttpStatusCode.BadRequest).ConfigureAwait(false);
        if (createResponse.StatusCode is HttpStatusCode.BadRequest &&
            !createResponse.Text.Contains("resource_already_exists_exception", StringComparison.Ordinal))
        {
            throw CreateResponseException(HttpMethod.Put, definition.Index, createResponse);
        }

        if (createResponse.StatusCode is HttpStatusCode.BadRequest)
        {
            await ValidateCollectionIndexAsync(runtime, definition, cancellationToken)
                .ConfigureAwait(false);
        }

        await EnsureReconciliationIndexAsync(runtime, definition, cancellationToken)
            .ConfigureAwait(false);

        _ = await SendJsonAsync(
            HttpMethod.Put,
            $"{_controlIndex}/_doc/{CollectionDocumentId(runtime, collection)}?refresh=wait_for",
            definition,
            cancellationToken,
            HttpStatusCode.OK,
            HttpStatusCode.Created).ConfigureAwait(false);
        runtime.CacheCollection(definition);
        return definition;
    }

    private async ValueTask ValidateControlIndexAsync(CancellationToken cancellationToken)
    {
        var mappingResponse = await SendAsync(
            HttpMethod.Get,
            $"{_controlIndex}/_mapping",
            null,
            null,
            cancellationToken,
            HttpStatusCode.OK).ConfigureAwait(false);
        using (var mapping = JsonDocument.Parse(mappingResponse.Content))
        {
            var metadata = mapping.RootElement
                .GetProperty(_controlIndex)
                .GetProperty("mappings")
                .GetProperty("_meta");
            if (!metadata.TryGetProperty("product", out var product) ||
                !string.Equals(product.GetString(), "BlueTusk Sync", StringComparison.Ordinal) ||
                !metadata.TryGetProperty("formatVersion", out var format) ||
                format.GetInt32() != CurrentFormatVersion)
            {
                throw new OpenSearchSyncException(
                    $"OpenSearch index '{_controlIndex}' is not a compatible BlueTusk Sync control index.");
            }
        }

        var settingsResponse = await SendAsync(
            HttpMethod.Get,
            $"{_controlIndex}/_settings",
            null,
            null,
            cancellationToken,
            HttpStatusCode.OK).ConfigureAwait(false);
        using var settings = JsonDocument.Parse(settingsResponse.Content);
        var indexSettings = settings.RootElement
            .GetProperty(_controlIndex)
            .GetProperty("settings")
            .GetProperty("index");
        var shards = int.Parse(
            indexSettings.GetProperty("number_of_shards").GetString()!,
            CultureInfo.InvariantCulture);
        var replicas = int.Parse(
            indexSettings.GetProperty("number_of_replicas").GetString()!,
            CultureInfo.InvariantCulture);
        if (shards != 1 || replicas != _options.NumberOfReplicas)
        {
            throw new OpenSearchSyncException(
                $"OpenSearch control index '{_controlIndex}' has {shards} shards and {replicas} replicas; this destination requires 1 shard and {_options.NumberOfReplicas} replicas.");
        }
    }

    private async ValueTask ValidateCollectionIndexAsync(
        PipelineRuntime runtime,
        CollectionDocument collection,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            HttpMethod.Get,
            $"{collection.Index}/_mapping",
            null,
            null,
            cancellationToken,
            HttpStatusCode.OK).ConfigureAwait(false);
        using var document = JsonDocument.Parse(response.Content);
        var metadata = document.RootElement
            .GetProperty(collection.Index)
            .GetProperty("mappings")
            .GetProperty("_meta");
        if (!metadata.TryGetProperty("product", out var product) ||
            !string.Equals(product.GetString(), "BlueTusk Sync", StringComparison.Ordinal) ||
            !metadata.TryGetProperty("formatVersion", out var format) ||
            format.GetInt32() != CurrentFormatVersion ||
            !metadata.TryGetProperty("pipelineHash", out var pipelineHash) ||
            !string.Equals(pipelineHash.GetString(), runtime.PipelineHash, StringComparison.Ordinal) ||
            !metadata.TryGetProperty("transformFingerprint", out var transform) ||
            !string.Equals(transform.GetString(), runtime.Transform.Fingerprint, StringComparison.Ordinal) ||
            !metadata.TryGetProperty("generation", out var generation) ||
            !string.Equals(generation.GetString(), runtime.Generation, StringComparison.Ordinal) ||
            !metadata.TryGetProperty("collection", out var collectionName) ||
            !string.Equals(collectionName.GetString(), collection.Collection, StringComparison.Ordinal))
        {
            throw new OpenSearchSyncException(
                $"OpenSearch index '{collection.Index}' is not owned by the expected pipeline, transform generation, and collection.");
        }
    }

    private byte[] BuildIndexDefinition(PipelineRuntime runtime, CollectionDocument collection)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WritePropertyName("settings");
        writer.WriteStartObject();
        writer.WriteNumber("number_of_shards", _options.NumberOfShards);
        writer.WriteNumber("number_of_replicas", _options.NumberOfReplicas);
        writer.WriteEndObject();
        writer.WritePropertyName("mappings");
        writer.WriteStartObject();
        writer.WriteBoolean("dynamic", true);
        writer.WritePropertyName("_meta");
        writer.WriteStartObject();
        writer.WriteString("product", "BlueTusk Sync");
        writer.WriteNumber("formatVersion", CurrentFormatVersion);
        writer.WriteString("pipelineHash", runtime.PipelineHash);
        writer.WriteString("transformFingerprint", runtime.Transform.Fingerprint);
        writer.WriteString("generation", runtime.Generation);
        writer.WriteString("collection", collection.Collection);
        writer.WriteEndObject();
        writer.WriteEndObject();
        if (!runtime.IsBuilding)
        {
            writer.WritePropertyName("aliases");
            writer.WriteStartObject();
            writer.WritePropertyName(collection.Alias);
            writer.WriteStartObject();
            writer.WriteBoolean("is_write_index", true);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private async ValueTask EnsureReconciliationIndexAsync(
        PipelineRuntime runtime,
        CollectionDocument collection,
        CancellationToken cancellationToken)
    {
        var index = ReconciliationIndex(collection);
        var response = await SendAsync(
            HttpMethod.Put,
            index,
            BuildReconciliationIndexDefinition(runtime, collection),
            "application/json",
            cancellationToken,
            HttpStatusCode.OK,
            HttpStatusCode.BadRequest).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.BadRequest &&
            !response.Text.Contains("resource_already_exists_exception", StringComparison.Ordinal))
        {
            throw CreateResponseException(HttpMethod.Put, index, response);
        }

        if (response.StatusCode is HttpStatusCode.BadRequest)
        {
            await ValidateReconciliationIndexAsync(runtime, collection, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private byte[] BuildReconciliationIndexDefinition(
        PipelineRuntime runtime,
        CollectionDocument collection)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WritePropertyName("settings");
        writer.WriteStartObject();
        writer.WriteNumber("number_of_shards", _options.NumberOfShards);
        writer.WriteNumber("number_of_replicas", _options.NumberOfReplicas);
        writer.WriteEndObject();
        writer.WritePropertyName("mappings");
        writer.WriteStartObject();
        writer.WriteString("dynamic", "strict");
        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        WriteKeywordMapping(writer, "recordType");
        writer.WritePropertyName("formatVersion");
        writer.WriteStartObject();
        writer.WriteString("type", "integer");
        writer.WriteEndObject();
        WriteKeywordMapping(writer, "pipelineHash");
        WriteKeywordMapping(writer, "generation");
        WriteKeywordMapping(writer, "collection");
        writer.WritePropertyName("key");
        writer.WriteStartObject();
        writer.WriteString("type", "keyword");
        writer.WriteNumber("ignore_above", _options.MaxReconciliationKeyBytes);
        writer.WriteEndObject();
        writer.WritePropertyName("keyHash");
        writer.WriteStartObject();
        writer.WriteString("type", "unsigned_long");
        writer.WriteEndObject();
        WriteKeywordMapping(writer, "contentHash");
        WriteKeywordMapping(writer, "contentType");
        WriteKeywordMapping(writer, "partitionKey");
        writer.WriteEndObject();
        writer.WritePropertyName("_meta");
        writer.WriteStartObject();
        writer.WriteString("product", "BlueTusk Sync Reconciliation");
        writer.WriteNumber("formatVersion", CurrentFormatVersion);
        writer.WriteString("pipelineHash", runtime.PipelineHash);
        writer.WriteString("transformFingerprint", runtime.Transform.Fingerprint);
        writer.WriteString("generation", runtime.Generation);
        writer.WriteString("collection", collection.Collection);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private async ValueTask ValidateReconciliationIndexAsync(
        PipelineRuntime runtime,
        CollectionDocument collection,
        CancellationToken cancellationToken)
    {
        var index = ReconciliationIndex(collection);
        var response = await SendAsync(
            HttpMethod.Get,
            $"{index}/_mapping",
            null,
            null,
            cancellationToken,
            HttpStatusCode.OK).ConfigureAwait(false);
        using var document = JsonDocument.Parse(response.Content);
        var metadata = document.RootElement
            .GetProperty(index)
            .GetProperty("mappings")
            .GetProperty("_meta");
        if (!metadata.TryGetProperty("product", out var product) ||
            !string.Equals(
                product.GetString(),
                "BlueTusk Sync Reconciliation",
                StringComparison.Ordinal) ||
            !metadata.TryGetProperty("formatVersion", out var format) ||
            format.GetInt32() != CurrentFormatVersion ||
            !metadata.TryGetProperty("pipelineHash", out var pipelineHash) ||
            !string.Equals(pipelineHash.GetString(), runtime.PipelineHash, StringComparison.Ordinal) ||
            !metadata.TryGetProperty("transformFingerprint", out var transform) ||
            !string.Equals(transform.GetString(), runtime.Transform.Fingerprint, StringComparison.Ordinal) ||
            !metadata.TryGetProperty("generation", out var generation) ||
            !string.Equals(generation.GetString(), runtime.Generation, StringComparison.Ordinal) ||
            !metadata.TryGetProperty("collection", out var collectionName) ||
            !string.Equals(collectionName.GetString(), collection.Collection, StringComparison.Ordinal))
        {
            throw new OpenSearchSyncException(
                $"OpenSearch reconciliation index '{index}' is not owned by the expected pipeline, transform generation, and collection.");
        }
    }

    private static void WriteKeywordMapping(Utf8JsonWriter writer, string property)
    {
        writer.WritePropertyName(property);
        writer.WriteStartObject();
        writer.WriteString("type", "keyword");
        writer.WriteEndObject();
    }

    private async ValueTask<IReadOnlyList<CollectionDocument>> ReadCollectionsAsync(
        PipelineRuntime runtime,
        CancellationToken cancellationToken)
    {
        var response = await SendJsonAsync(
            HttpMethod.Post,
            $"{_controlIndex}/_search",
            new
            {
                size = 10_000,
                query = new
                {
                    @bool = new
                    {
                        filter = new object[]
                        {
                            new { term = new { recordType = "collection" } },
                            new { term = new { pipelineHash = runtime.PipelineHash } },
                            new { term = new { generation = runtime.Generation } },
                        },
                    },
                },
            },
            cancellationToken,
            HttpStatusCode.OK).ConfigureAwait(false);
        using var document = JsonDocument.Parse(response.Content);
        var results = new List<CollectionDocument>();
        foreach (var hit in document.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray())
        {
            var collection = hit.GetProperty("_source").Deserialize<CollectionDocument>(JsonOptions) ??
                throw new OpenSearchSyncException(
                    $"OpenSearch returned an invalid collection registry entry for pipeline '{runtime.PipelineId}'.");
            results.Add(collection);
        }

        return results;
    }

    private async ValueTask DeleteAllDocumentsAsync(
        string index,
        CancellationToken cancellationToken)
    {
        var response = await SendJsonAsync(
            HttpMethod.Post,
            $"{index}/_delete_by_query?conflicts=proceed&refresh=true&wait_for_completion=true",
            new { query = new { match_all = new { } } },
            cancellationToken,
            HttpStatusCode.OK,
            HttpStatusCode.NotFound).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            throw new OpenSearchSyncException(
                $"Registered OpenSearch index '{index}' no longer exists.");
        }

        using var document = JsonDocument.Parse(response.Content);
        var failures = document.RootElement.TryGetProperty("failures", out var failureElement)
            ? failureElement.GetArrayLength()
            : 0;
        if (failures != 0)
        {
            throw new OpenSearchSyncBulkException(
                $"OpenSearch reported {failures} failures while clearing index '{index}'.");
        }
    }

    private async ValueTask ExecuteBulkAsync(
        IReadOnlyList<MaterializedOperation> operations,
        long? externalVersion,
        CancellationToken cancellationToken)
    {
        if (operations.Count == 0)
        {
            return;
        }

        var bulkOperations = await BuildBulkOperationsAsync(operations, cancellationToken)
            .ConfigureAwait(false);
        var payloadLength = CountBulkPayload(bulkOperations, externalVersion);
        if (payloadLength > _options.MaxBulkBytes)
        {
            throw new OpenSearchSyncBulkException(
                $"The encoded OpenSearch bulk request contains {payloadLength} bytes; the configured maximum is {_options.MaxBulkBytes}.");
        }

        var refresh = _options.RefreshAfterWrite ? "wait_for" : "false";
        using var content = new BulkOperationsContent(
            bulkOperations,
            externalVersion,
            payloadLength);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
        var response = await SendAsync(
            HttpMethod.Post,
            $"_bulk?wait_for_active_shards={Uri.EscapeDataString(_options.WaitForActiveShards)}&refresh={refresh}",
            content,
            cancellationToken,
            HttpStatusCode.OK).ConfigureAwait(false);
        using var document = JsonDocument.Parse(response.Content);
        var items = document.RootElement.GetProperty("items");
        if (items.GetArrayLength() != bulkOperations.Count)
        {
            throw new OpenSearchSyncBulkException(
                $"OpenSearch returned {items.GetArrayLength()} bulk results for {bulkOperations.Count} physical operations.");
        }

        var failures = new List<string>();
        var ordinal = 0;
        foreach (var item in items.EnumerateArray())
        {
            var operation = bulkOperations[ordinal++];
            var result = item.EnumerateObject().Single().Value;
            var status = result.GetProperty("status").GetInt32();
            var versionConflict = externalVersion is not null &&
                status == 409 &&
                result.TryGetProperty("error", out var conflictError) &&
                conflictError.TryGetProperty("type", out var conflictType) &&
                string.Equals(
                    conflictType.GetString(),
                    "version_conflict_engine_exception",
                    StringComparison.Ordinal);
            var accepted = status is >= 200 and < 300 ||
                operation.Kind is SyncMutationKind.Delete && status == 404 ||
                versionConflict;
            if (!accepted)
            {
                var error = result.TryGetProperty("error", out var errorElement)
                    ? errorElement.GetRawText()
                    : "no error detail";
                failures.Add(
                    $"{operation.Kind} {operation.Description}/{operation.DocumentId}: HTTP {status} {error}");
            }
        }

        if (failures.Count != 0)
        {
            throw new OpenSearchSyncBulkException(
                "OpenSearch partially rejected the transaction bulk request. The transaction checkpoint was not advanced. " +
                string.Join(" | ", failures.Take(5)));
        }
    }

    private static long CountBulkPayload(
        IReadOnlyList<BulkOperation> operations,
        long? externalVersion)
    {
        var counter = new CountingBufferWriter();
        foreach (var operation in operations)
        {
            WriteBulkMetadata(counter, operation, externalVersion);
            counter.Advance(1);
            if (operation.Kind is SyncMutationKind.Upsert)
            {
                counter.Advance(operation.Content.Length + 1);
            }
        }

        return counter.Count;
    }

    private static void WriteBulkMetadata(
        IBufferWriter<byte> destination,
        BulkOperation operation,
        long? externalVersion)
    {
        using var writer = new Utf8JsonWriter(destination);
        writer.WriteStartObject();
        writer.WritePropertyName(
            operation.Kind is SyncMutationKind.Upsert ? "index" : "delete");
        writer.WriteStartObject();
        writer.WriteString("_index", operation.Index);
        writer.WriteString("_id", operation.DocumentId);
        if (!string.IsNullOrWhiteSpace(operation.Routing))
        {
            writer.WriteString("routing", operation.Routing);
        }

        if (externalVersion is not null)
        {
            writer.WriteNumber("version", externalVersion.Value);
            writer.WriteString("version_type", "external_gte");
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
    }

    private async ValueTask<IReadOnlyList<BulkOperation>> BuildBulkOperationsAsync(
        IReadOnlyList<MaterializedOperation> operations,
        CancellationToken cancellationToken)
    {
        var existing = await ReadExistingReconciliationDocumentsAsync(
            operations,
            cancellationToken).ConfigureAwait(false);
        var result = new List<BulkOperation>(operations.Count * 3);
        foreach (var operation in operations)
        {
            var reconciliationIndex = ReconciliationIndex(operation.Collection);
            existing.TryGetValue(
                (reconciliationIndex, operation.DocumentId),
                out var previous);
            if (operation.Kind is SyncMutationKind.Upsert)
            {
                if (previous is not null &&
                    !string.Equals(previous.PartitionKey, operation.Routing, StringComparison.Ordinal))
                {
                    result.Add(new BulkOperation(
                        SyncMutationKind.Delete,
                        operation.Collection.Index,
                        operation.DocumentId,
                        ReadOnlyMemory<byte>.Empty,
                        previous.PartitionKey,
                        operation.Collection.Collection + " route migration"));
                }

                result.Add(new BulkOperation(
                    SyncMutationKind.Upsert,
                    operation.Collection.Index,
                    operation.DocumentId,
                    operation.Content,
                    operation.Routing,
                    operation.Collection.Collection));
                var sidecar = new ReconciliationDocument(
                    "document",
                    CurrentFormatVersion,
                    operation.Collection.PipelineHash,
                    operation.Collection.Generation,
                    operation.Collection.Collection,
                    operation.Key,
                    SyncReconciler.GetKeyHash(operation.Key),
                    Convert.ToHexStringLower(
                        System.Security.Cryptography.SHA256.HashData(operation.Content.Span)),
                    operation.ContentType,
                    operation.Routing);
                result.Add(new BulkOperation(
                    SyncMutationKind.Upsert,
                    reconciliationIndex,
                    operation.DocumentId,
                    JsonSerializer.SerializeToUtf8Bytes(sidecar, JsonOptions),
                    null,
                    operation.Collection.Collection + " reconciliation sidecar"));
            }
            else
            {
                result.Add(new BulkOperation(
                    SyncMutationKind.Delete,
                    operation.Collection.Index,
                    operation.DocumentId,
                    ReadOnlyMemory<byte>.Empty,
                    previous?.PartitionKey ?? operation.Routing,
                    operation.Collection.Collection));
                result.Add(new BulkOperation(
                    SyncMutationKind.Delete,
                    reconciliationIndex,
                    operation.DocumentId,
                    ReadOnlyMemory<byte>.Empty,
                    null,
                    operation.Collection.Collection + " reconciliation sidecar"));
            }
        }

        return result;
    }

    private async ValueTask<IReadOnlyDictionary<(string Index, string Id), ReconciliationDocument>>
        ReadExistingReconciliationDocumentsAsync(
            IReadOnlyList<MaterializedOperation> operations,
            CancellationToken cancellationToken)
    {
        if (operations.Count == 0)
        {
            return new Dictionary<(string, string), ReconciliationDocument>();
        }

        var documents = operations
            .Select(operation => new
            {
                _index = ReconciliationIndex(operation.Collection),
                _id = operation.DocumentId,
                _source = RoutingSourceFields,
            })
            .ToArray();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { docs = documents }, JsonOptions);
        if (payload.Length > _options.MaxBulkBytes)
        {
            throw new OpenSearchSyncBulkException(
                $"The reconciliation routing lookup contains {payload.Length} bytes; the configured request maximum is {_options.MaxBulkBytes}.");
        }

        var response = await SendAsync(
            HttpMethod.Post,
            "_mget",
            payload,
            "application/json",
            cancellationToken,
            HttpStatusCode.OK).ConfigureAwait(false);
        using var document = JsonDocument.Parse(response.Content);
        var returned = document.RootElement.GetProperty("docs");
        if (returned.GetArrayLength() != operations.Count)
        {
            throw new OpenSearchSyncBulkException(
                $"OpenSearch returned {returned.GetArrayLength()} routing records for {operations.Count} mutations.");
        }

        var result = new Dictionary<(string, string), ReconciliationDocument>();
        var ordinal = 0;
        foreach (var item in returned.EnumerateArray())
        {
            var operation = operations[ordinal++];
            if (!item.TryGetProperty("found", out var found) || !found.GetBoolean())
            {
                continue;
            }

            var source = item.GetProperty("_source");
            var partitionKey = source.TryGetProperty("partitionKey", out var routing) &&
                routing.ValueKind is JsonValueKind.String
                    ? routing.GetString()
                    : null;
            result[(ReconciliationIndex(operation.Collection), operation.DocumentId)] =
                new ReconciliationDocument(
                    "document",
                    CurrentFormatVersion,
                    operation.Collection.PipelineHash,
                    operation.Collection.Generation,
                    operation.Collection.Collection,
                    operation.Key,
                    SyncReconciler.GetKeyHash(operation.Key),
                    new string('0', 64),
                    operation.ContentType,
                    partitionKey);
        }

        return result;
    }

    private async ValueTask<SnapshotDocument> ValidateSnapshotStateAsync(
        PipelineRuntime runtime,
        Guid epoch,
        bool requireComplete,
        CancellationToken cancellationToken)
    {
        var state = await ReadControlDocumentAsync<SnapshotDocument>(
            SnapshotDocumentId(runtime),
            cancellationToken).ConfigureAwait(false);
        if (state is null ||
            state.Source.FormatVersion != CurrentFormatVersion ||
            !string.Equals(state.Source.SourceFingerprint, runtime.Source.Fingerprint, StringComparison.Ordinal) ||
            !string.Equals(state.Source.TransformFingerprint, runtime.Transform.Fingerprint, StringComparison.Ordinal) ||
            !string.Equals(state.Source.Generation, runtime.Generation, StringComparison.Ordinal) ||
            !string.Equals(state.Source.Epoch, epoch.ToString("N"), StringComparison.Ordinal) ||
            state.Source.Complete != requireComplete)
        {
            var stateDescription = requireComplete ? "completed" : "active incomplete";
            throw new OpenSearchSyncSnapshotException(
                $"Snapshot epoch '{epoch}' is not the {stateDescription} OpenSearch destination epoch for pipeline '{runtime.PipelineId}'.");
        }

        return state.Source;
    }

    private async ValueTask<CheckpointDocument?> ReadCheckpointAsync(
        PipelineRuntime runtime,
        CancellationToken cancellationToken)
    {
        var document = await ReadControlDocumentAsync<CheckpointDocument>(
            CheckpointDocumentId(runtime),
            cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        var checkpoint = document.Source;
        if (checkpoint.FormatVersion != CurrentFormatVersion ||
            !string.Equals(checkpoint.PipelineHash, runtime.PipelineHash, StringComparison.Ordinal) ||
            !string.Equals(checkpoint.SourceFingerprint, runtime.Source.Fingerprint, StringComparison.Ordinal) ||
            !string.Equals(checkpoint.TransformFingerprint, runtime.Transform.Fingerprint, StringComparison.Ordinal) ||
            !string.Equals(checkpoint.Generation, runtime.Generation, StringComparison.Ordinal))
        {
            throw new OpenSearchSyncException(
                $"OpenSearch checkpoint metadata for pipeline '{runtime.PipelineId}' is incompatible with its provisioned identity.");
        }

        return checkpoint;
    }

    private async ValueTask<ControlDocument<T>?> ReadControlDocumentAsync<T>(
        string documentId,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            HttpMethod.Get,
            $"{_controlIndex}/_doc/{documentId}",
            null,
            null,
            cancellationToken,
            HttpStatusCode.OK,
            HttpStatusCode.NotFound).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            return null;
        }

        using var document = JsonDocument.Parse(response.Content);
        var root = document.RootElement;
        var source = root.GetProperty("_source").Deserialize<T>(JsonOptions) ??
            throw new OpenSearchSyncException(
                $"OpenSearch control document '{documentId}' cannot be decoded.");
        return new ControlDocument<T>(
            source,
            root.GetProperty("_seq_no").GetInt64(),
            root.GetProperty("_primary_term").GetInt64());
    }

    private async ValueTask<ResponsePayload> SendJsonAsync<T>(
        HttpMethod method,
        string path,
        T value,
        CancellationToken cancellationToken,
        params HttpStatusCode[] expectedStatusCodes) =>
        await SendAsync(
            method,
            path,
            JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            "application/json",
            cancellationToken,
            expectedStatusCodes).ConfigureAwait(false);

    private async ValueTask<ResponsePayload> SendAsync(
        HttpMethod method,
        string path,
        byte[]? content,
        string? mediaType,
        CancellationToken cancellationToken,
        params HttpStatusCode[] expectedStatusCodes)
    {
        HttpContent? requestContent = null;
        if (content is not null)
        {
            requestContent = new ByteArrayContent(content);
            requestContent.Headers.ContentType = new MediaTypeHeaderValue(mediaType!);
        }

        return await SendAsync(
            method,
            path,
            requestContent,
            cancellationToken,
            expectedStatusCodes).ConfigureAwait(false);
    }

    private async ValueTask<ResponsePayload> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        CancellationToken cancellationToken,
        params HttpStatusCode[] expectedStatusCodes)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = content,
        };

        using var response = await _options.Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var responseContent = await response.Content.ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var result = new ResponsePayload(response.StatusCode, responseContent);
        if (!expectedStatusCodes.Contains(response.StatusCode))
        {
            throw CreateResponseException(method, path, result);
        }

        return result;
    }

    private sealed class BulkOperationsContent(
        IReadOnlyList<BulkOperation> operations,
        long? externalVersion,
        long contentLength) : HttpContent
    {
        private static ReadOnlyMemory<byte> NewLine => "\n"u8.ToArray();

        protected override bool TryComputeLength(out long length)
        {
            length = contentLength;
            return true;
        }

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
        {
            await SerializeToStreamAsync(
                stream,
                context,
                CancellationToken.None).ConfigureAwait(false);
        }

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            foreach (var operation in operations)
            {
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName(
                        operation.Kind is SyncMutationKind.Upsert ? "index" : "delete");
                    writer.WriteStartObject();
                    writer.WriteString("_index", operation.Index);
                    writer.WriteString("_id", operation.DocumentId);
                    if (!string.IsNullOrWhiteSpace(operation.Routing))
                    {
                        writer.WriteString("routing", operation.Routing);
                    }

                    if (externalVersion is not null)
                    {
                        writer.WriteNumber("version", externalVersion.Value);
                        writer.WriteString("version_type", "external_gte");
                    }

                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                await stream.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
                if (operation.Kind is SyncMutationKind.Upsert)
                {
                    await stream.WriteAsync(operation.Content, cancellationToken)
                        .ConfigureAwait(false);
                    await stream.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private sealed class CountingBufferWriter : IBufferWriter<byte>
    {
        private byte[] _scratch = new byte[256];

        public long Count { get; private set; }

        public void Advance(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            Count = checked(Count + count);
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _scratch;
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _scratch;
        }

        private void Ensure(int sizeHint)
        {
            if (sizeHint > _scratch.Length)
            {
                _scratch = new byte[sizeHint];
            }
        }
    }

    private static OpenSearchSyncException CreateResponseException(
        HttpMethod method,
        string path,
        ResponsePayload response)
    {
        var detail = response.Text;
        if (detail.Length > 2_000)
        {
            detail = detail[..2_000] + "...";
        }

        return new OpenSearchSyncException(
            $"OpenSearch {method.Method} '{path}' returned HTTP {(int)response.StatusCode} ({response.StatusCode}): {detail}");
    }

    private string CollectionIndex(PipelineRuntime runtime, string collection) =>
        $"{_options.IndexPrefix}-p{runtime.PipelineHash}-c{Fingerprint(collection, 24)}-g{runtime.Generation}";

    private string CollectionAlias(PipelineRuntime runtime, string collection) =>
        $"{_options.IndexPrefix}-p{runtime.PipelineHash}-c{Fingerprint(collection, 24)}";

    private static string ReconciliationIndex(CollectionDocument collection) =>
        collection.Index + "-reconcile";

    private static string CollectionDocumentId(PipelineRuntime runtime, string collection) =>
        $"collection-{runtime.PipelineHash}-{runtime.Generation}-{Fingerprint(collection, 24)}";

    private static void Append(ArrayBufferWriter<byte> buffer, ReadOnlySpan<byte> value)
    {
        value.CopyTo(buffer.GetSpan(value.Length));
        buffer.Advance(value.Length);
    }

    private static void AppendNewLine(ArrayBufferWriter<byte> buffer)
    {
        buffer.GetSpan(1)[0] = (byte)'\n';
        buffer.Advance(1);
    }
}
