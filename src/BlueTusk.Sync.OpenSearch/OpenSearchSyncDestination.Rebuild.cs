using System.Buffers;
using System.Net;
using System.Text.Json;

namespace BlueTusk.Sync.OpenSearch;

public sealed partial class OpenSearchSyncDestination
{
    /// <summary>Creates or resumes an isolated index generation for a new transform.</summary>
    public async ValueTask BeginRebuildAsync(
        SyncProvisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var pipelineHash = Fingerprint(request.PipelineId, 24);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var current = await ReadControlDocumentAsync<PipelineDocument>(
                PipelineDocumentId(pipelineHash),
                cancellationToken).ConfigureAwait(false) ??
                throw new OpenSearchSyncException(
                    $"OpenSearch Sync pipeline '{request.PipelineId}' must be provisioned before a rebuild can begin.");
            EnsurePipelineDocument(
                current,
                request.PipelineId,
                pipelineHash,
                request.Source);
            if (string.Equals(
                    current.Source.ActiveTransformFingerprint,
                    request.Transform.Fingerprint,
                    StringComparison.Ordinal))
            {
                SetRuntime(
                    request,
                    pipelineHash,
                    current.Source.ActiveGeneration,
                    isBuilding: false);
                return;
            }

            if (current.Source.BuildingTransformFingerprint is not null)
            {
                if (!string.Equals(
                        current.Source.BuildingTransformFingerprint,
                        request.Transform.Fingerprint,
                        StringComparison.Ordinal))
                {
                    throw new OpenSearchSyncException(
                        $"OpenSearch Sync pipeline '{request.PipelineId}' is already rebuilding transform '{current.Source.BuildingTransformFingerprint}'.");
                }

                var existingGeneration = current.Source.BuildingGeneration ??
                    throw new OpenSearchSyncException(
                        $"OpenSearch Sync pipeline '{request.PipelineId}' has incomplete rebuild metadata.");
                await ResumeBuildingRuntimeAsync(
                    request,
                    pipelineHash,
                    current.Source,
                    existingGeneration,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var generation = Generation(request.Transform.Fingerprint);
            if (string.Equals(
                    generation,
                    current.Source.ActiveGeneration,
                    StringComparison.Ordinal))
            {
                throw new OpenSearchSyncException(
                    $"Transform '{request.Transform.Fingerprint}' collides with the active OpenSearch generation identifier for pipeline '{request.PipelineId}'.");
            }

            var replacement = current.Source with
            {
                BuildingTransformName = request.Transform.Name,
                BuildingTransformFingerprint = request.Transform.Fingerprint,
                BuildingGeneration = generation,
            };
            var response = await WritePipelineDocumentAsync(
                current,
                replacement,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Conflict)
            {
                continue;
            }

            await ResumeBuildingRuntimeAsync(
                request,
                pipelineHash,
                replacement,
                generation,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new OpenSearchSyncException(
            $"OpenSearch Sync pipeline '{request.PipelineId}' rebuild metadata changed repeatedly during provisioning.");
    }

    /// <summary>Compares active and rebuilding collection counts before cutover.</summary>
    public async ValueTask<OpenSearchRebuildVerification> VerifyRebuildAsync(
        string pipelineId,
        CancellationToken cancellationToken = default)
    {
        var rebuildingRuntime = RequirePipeline(pipelineId);
        if (!rebuildingRuntime.IsBuilding)
        {
            throw new OpenSearchSyncException(
                $"OpenSearch Sync pipeline '{pipelineId}' is not in rebuild mode.");
        }

        await using var gate = await rebuildingRuntime.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        return await VerifyRebuildCoreAsync(rebuildingRuntime, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<OpenSearchRebuildVerification> VerifyRebuildCoreAsync(
        PipelineRuntime rebuildingRuntime,
        CancellationToken cancellationToken)
    {
        var pipeline = await ReadControlDocumentAsync<PipelineDocument>(
            PipelineDocumentId(rebuildingRuntime.PipelineHash),
            cancellationToken).ConfigureAwait(false) ??
            throw new OpenSearchSyncException(
                $"OpenSearch Sync pipeline '{rebuildingRuntime.PipelineId}' control metadata is unavailable.");
        EnsureBuildingRuntime(rebuildingRuntime, pipeline.Source);
        var activeRuntime = ActiveRuntime(rebuildingRuntime, pipeline.Source);
        var activeCollections = await ReadCollectionsAsync(activeRuntime, cancellationToken)
            .ConfigureAwait(false);
        var rebuildCollections = await ReadCollectionsAsync(rebuildingRuntime, cancellationToken)
            .ConfigureAwait(false);
        var active = activeCollections.ToDictionary(
            collection => collection.Collection,
            StringComparer.Ordinal);
        var rebuilding = rebuildCollections.ToDictionary(
            collection => collection.Collection,
            StringComparer.Ordinal);
        var counts = new List<OpenSearchCollectionCount>();
        foreach (var collectionName in active.Keys.Union(rebuilding.Keys, StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            var activeCount = active.TryGetValue(collectionName, out var activeCollection)
                ? await CountDocumentsAsync(activeCollection.Index, cancellationToken).ConfigureAwait(false)
                : 0;
            var rebuildCount = rebuilding.TryGetValue(collectionName, out var rebuildCollection)
                ? await CountDocumentsAsync(rebuildCollection.Index, cancellationToken).ConfigureAwait(false)
                : 0;
            counts.Add(new OpenSearchCollectionCount(collectionName, activeCount, rebuildCount));
        }

        return new OpenSearchRebuildVerification(counts);
    }

    /// <summary>Verifies the rebuild and atomically moves every collection alias.</summary>
    public async ValueTask CompleteRebuildAsync(
        string pipelineId,
        CancellationToken cancellationToken = default)
    {
        var rebuildingRuntime = RequirePipeline(pipelineId);
        if (!rebuildingRuntime.IsBuilding)
        {
            throw new OpenSearchSyncException(
                $"OpenSearch Sync pipeline '{pipelineId}' is not in rebuild mode.");
        }

        await using var gate = await rebuildingRuntime.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        var verification = await VerifyRebuildCoreAsync(rebuildingRuntime, cancellationToken)
            .ConfigureAwait(false);
        if (!verification.IsMatch)
        {
            var mismatches = string.Join(
                ", ",
                verification.Collections
                    .Where(collection => !collection.IsMatch)
                    .Select(collection =>
                        $"{collection.Collection} ({collection.ActiveDocuments} active, {collection.RebuildDocuments} rebuild)"));
            throw new OpenSearchSyncException(
                $"OpenSearch Sync rebuild for pipeline '{pipelineId}' cannot be activated because count verification failed: {mismatches}.");
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var current = await ReadControlDocumentAsync<PipelineDocument>(
                PipelineDocumentId(rebuildingRuntime.PipelineHash),
                cancellationToken).ConfigureAwait(false) ??
                throw new OpenSearchSyncException(
                    $"OpenSearch Sync pipeline '{pipelineId}' control metadata is unavailable.");
            if (string.Equals(
                    current.Source.ActiveTransformFingerprint,
                    rebuildingRuntime.Transform.Fingerprint,
                    StringComparison.Ordinal))
            {
                SetRuntime(
                    new SyncProvisionRequest(
                        pipelineId,
                        rebuildingRuntime.Source,
                        rebuildingRuntime.Transform),
                    rebuildingRuntime.PipelineHash,
                    current.Source.ActiveGeneration,
                    isBuilding: false);
                return;
            }

            EnsureBuildingRuntime(rebuildingRuntime, current.Source);
            var activeRuntime = ActiveRuntime(rebuildingRuntime, current.Source);
            var activeCollections = await ReadCollectionsAsync(activeRuntime, cancellationToken)
                .ConfigureAwait(false);
            var rebuildCollections = await ReadCollectionsAsync(rebuildingRuntime, cancellationToken)
                .ConfigureAwait(false);
            var active = activeCollections.ToDictionary(
                collection => collection.Collection,
                StringComparer.Ordinal);
            var rebuilding = rebuildCollections.ToDictionary(
                collection => collection.Collection,
                StringComparer.Ordinal);
            var collectionNames = active.Keys.Union(rebuilding.Keys, StringComparer.Ordinal).ToArray();
            if (collectionNames.Any(collection => !rebuilding.ContainsKey(collection)))
            {
                throw new OpenSearchSyncException(
                    $"OpenSearch Sync rebuild for pipeline '{pipelineId}' is missing one or more active collections.");
            }

            if (collectionNames.Length != 0)
            {
                var aliasPayload = BuildAliasSwapPayload(collectionNames, active, rebuilding);
                _ = await SendAsync(
                    HttpMethod.Post,
                    "_aliases",
                    aliasPayload,
                    "application/json",
                    cancellationToken,
                    HttpStatusCode.OK).ConfigureAwait(false);
            }

            var replacement = current.Source with
            {
                ActiveTransformName = rebuildingRuntime.Transform.Name,
                ActiveTransformFingerprint = rebuildingRuntime.Transform.Fingerprint,
                ActiveGeneration = rebuildingRuntime.Generation,
                BuildingTransformName = null,
                BuildingTransformFingerprint = null,
                BuildingGeneration = null,
            };
            var update = await WritePipelineDocumentAsync(
                current,
                replacement,
                cancellationToken).ConfigureAwait(false);
            if (update.StatusCode is HttpStatusCode.Conflict)
            {
                continue;
            }

            SetRuntime(
                new SyncProvisionRequest(
                    pipelineId,
                    rebuildingRuntime.Source,
                    rebuildingRuntime.Transform),
                rebuildingRuntime.PipelineHash,
                rebuildingRuntime.Generation,
                isBuilding: false);
            return;
        }

        throw new OpenSearchSyncException(
            $"OpenSearch Sync pipeline '{pipelineId}' metadata changed repeatedly during alias activation. The alias operation is idempotent and can be retried.");
    }

    /// <summary>Deletes an inactive generation and its BlueTusk control records.</summary>
    public async ValueTask RetireGenerationAsync(
        string pipelineId,
        string generation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(generation);
        if (generation.Length != 16 || !generation.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "An OpenSearch generation identifier must contain exactly 16 hexadecimal characters.",
                nameof(generation));
        }

        var pipelineHash = Fingerprint(pipelineId, 24);
        var current = await ReadControlDocumentAsync<PipelineDocument>(
            PipelineDocumentId(pipelineHash),
            cancellationToken).ConfigureAwait(false) ??
            throw new OpenSearchSyncException(
                $"OpenSearch Sync pipeline '{pipelineId}' control metadata is unavailable.");
        if (string.Equals(current.Source.ActiveGeneration, generation, StringComparison.Ordinal) ||
            string.Equals(current.Source.BuildingGeneration, generation, StringComparison.Ordinal))
        {
            throw new OpenSearchSyncException(
                $"OpenSearch generation '{generation}' for pipeline '{pipelineId}' is active or rebuilding and cannot be retired.");
        }

        var retired = new PipelineRuntime(
            pipelineId,
            pipelineHash,
            new BlueTusk.Streams.ChangeSourceIdentity(
                "retirement",
                current.Source.SourceFingerprint,
                "retirement",
                "retirement"),
            new SyncTransformVersion("retirement", new string('0', 64)),
            generation,
            isBuilding: true);
        var collections = await ReadCollectionsAsync(retired, cancellationToken).ConfigureAwait(false);
        foreach (var collection in collections)
        {
            _ = await SendAsync(
                HttpMethod.Delete,
                collection.Index,
                null,
                null,
                cancellationToken,
                HttpStatusCode.OK,
                HttpStatusCode.NotFound).ConfigureAwait(false);
            _ = await SendAsync(
                HttpMethod.Delete,
                $"{_controlIndex}/_doc/{CollectionDocumentId(retired, collection.Collection)}?refresh=wait_for",
                null,
                null,
                cancellationToken,
                HttpStatusCode.OK,
                HttpStatusCode.NotFound).ConfigureAwait(false);
        }

        _ = await SendAsync(
            HttpMethod.Delete,
            $"{_controlIndex}/_doc/{CheckpointDocumentId(retired)}?refresh=wait_for",
            null,
            null,
            cancellationToken,
            HttpStatusCode.OK,
            HttpStatusCode.NotFound).ConfigureAwait(false);
        _ = await SendAsync(
            HttpMethod.Delete,
            $"{_controlIndex}/_doc/{SnapshotDocumentId(retired)}?refresh=wait_for",
            null,
            null,
            cancellationToken,
            HttpStatusCode.OK,
            HttpStatusCode.NotFound).ConfigureAwait(false);
    }

    private async ValueTask<ResponsePayload> WritePipelineDocumentAsync(
        ControlDocument<PipelineDocument> current,
        PipelineDocument replacement,
        CancellationToken cancellationToken) =>
        await SendJsonAsync(
            HttpMethod.Put,
            $"{_controlIndex}/_doc/{PipelineDocumentId(replacement.PipelineHash)}?if_seq_no={current.SequenceNumber}&if_primary_term={current.PrimaryTerm}&refresh=wait_for",
            replacement,
            cancellationToken,
            HttpStatusCode.OK,
            HttpStatusCode.Conflict).ConfigureAwait(false);

    private async ValueTask ResumeBuildingRuntimeAsync(
        SyncProvisionRequest request,
        string pipelineHash,
        PipelineDocument pipeline,
        string generation,
        CancellationToken cancellationToken)
    {
        var activeRuntime = new PipelineRuntime(
            request.PipelineId,
            pipelineHash,
            request.Source,
            new SyncTransformVersion(
                pipeline.ActiveTransformName,
                pipeline.ActiveTransformFingerprint),
            pipeline.ActiveGeneration,
            isBuilding: false);
        var rebuildingRuntime = new PipelineRuntime(
            request.PipelineId,
            pipelineHash,
            request.Source,
            request.Transform,
            generation,
            isBuilding: true);
        var activeCollections = await ReadCollectionsAsync(activeRuntime, cancellationToken)
            .ConfigureAwait(false);
        foreach (var collection in activeCollections)
        {
            _ = await EnsureCollectionAsync(
                rebuildingRuntime,
                collection.Collection,
                cancellationToken).ConfigureAwait(false);
        }

        _pipelines.AddOrUpdate(request.PipelineId, rebuildingRuntime, (_, _) => rebuildingRuntime);
    }

    private static PipelineRuntime ActiveRuntime(
        PipelineRuntime rebuildingRuntime,
        PipelineDocument pipeline) =>
        new(
            rebuildingRuntime.PipelineId,
            rebuildingRuntime.PipelineHash,
            rebuildingRuntime.Source,
            new SyncTransformVersion(
                pipeline.ActiveTransformName,
                pipeline.ActiveTransformFingerprint),
            pipeline.ActiveGeneration,
            isBuilding: false);

    private static void EnsureBuildingRuntime(
        PipelineRuntime runtime,
        PipelineDocument pipeline)
    {
        if (!string.Equals(
                pipeline.BuildingTransformFingerprint,
                runtime.Transform.Fingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                pipeline.BuildingGeneration,
                runtime.Generation,
                StringComparison.Ordinal))
        {
            throw new OpenSearchSyncException(
                $"OpenSearch Sync pipeline '{runtime.PipelineId}' rebuild metadata no longer matches this worker.");
        }
    }

    private async ValueTask<long> CountDocumentsAsync(
        string index,
        CancellationToken cancellationToken)
    {
        _ = await SendAsync(
            HttpMethod.Post,
            $"{index}/_refresh",
            null,
            null,
            cancellationToken,
            HttpStatusCode.OK).ConfigureAwait(false);
        var response = await SendJsonAsync(
            HttpMethod.Post,
            $"{index}/_count",
            new { query = new { match_all = new { } } },
            cancellationToken,
            HttpStatusCode.OK).ConfigureAwait(false);
        using var document = JsonDocument.Parse(response.Content);
        return document.RootElement.GetProperty("count").GetInt64();
    }

    private static byte[] BuildAliasSwapPayload(
        IReadOnlyList<string> collectionNames,
        IReadOnlyDictionary<string, CollectionDocument> active,
        IReadOnlyDictionary<string, CollectionDocument> rebuilding)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WritePropertyName("actions");
        writer.WriteStartArray();
        foreach (var collectionName in collectionNames.Order(StringComparer.Ordinal))
        {
            if (active.TryGetValue(collectionName, out var current))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("remove");
                writer.WriteStartObject();
                writer.WriteString("index", current.Index);
                writer.WriteString("alias", current.Alias);
                writer.WriteBoolean("must_exist", false);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            var replacement = rebuilding[collectionName];
            writer.WriteStartObject();
            writer.WritePropertyName("add");
            writer.WriteStartObject();
            writer.WriteString("index", replacement.Index);
            writer.WriteString("alias", replacement.Alias);
            writer.WriteBoolean("is_write_index", true);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }
}
