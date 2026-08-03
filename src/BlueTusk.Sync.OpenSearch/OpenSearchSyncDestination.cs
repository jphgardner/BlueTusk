using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using BlueTusk.Streams;
using BlueTusk.TypeSystem;

namespace BlueTusk.Sync.OpenSearch;

/// <summary>
/// Materializes source transactions into versioned OpenSearch indexes and advances a durable
/// checkpoint only after every bulk item succeeds.
/// </summary>
public sealed partial class OpenSearchSyncDestination :
    ISyncDestination,
    ISyncQuarantineStore,
    ISyncQuarantineReplayDestination,
    ISyncReconciliationReader,
    ISyncRepairSink,
    ISyncRebuildDestination
{
    /// <summary>Gets the control-document and index metadata format written by this build.</summary>
    public const int CurrentFormatVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] RoutingSourceFields = ["partitionKey"];
    private readonly OpenSearchSyncOptions _options;
    private readonly string _controlIndex;
    private readonly ConcurrentDictionary<string, PipelineRuntime> _pipelines =
        new(StringComparer.Ordinal);
    private readonly object _initializeLock = new();
    private Task? _initializationTask;

    /// <summary>Initializes a new OpenSearch Sync destination.</summary>
    public OpenSearchSyncDestination(OpenSearchSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _controlIndex = options.IndexPrefix + "-control";
    }

    /// <inheritdoc />
    public string Name => "OpenSearch";

    /// <inheritdoc />
    public SyncDestinationCapabilities Capabilities =>
        SyncDestinationCapabilities.IdempotentUpserts |
        SyncDestinationCapabilities.Deletes |
        SyncDestinationCapabilities.Reconciliation |
        SyncDestinationCapabilities.AliasSwap;

    /// <summary>Creates or validates the BlueTusk-owned OpenSearch control index.</summary>
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        Task initialization;
        lock (_initializeLock)
        {
            initialization = _initializationTask ??= InitializeCoreAsync();
        }

        try
        {
            await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_initializeLock)
            {
                if (ReferenceEquals(_initializationTask, initialization) && initialization.IsFaulted)
                {
                    _initializationTask = null;
                }
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<SyncProvisionResult> ProvisionAsync(
        SyncProvisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var pipelineHash = Fingerprint(request.PipelineId, 24);
        var documentId = PipelineDocumentId(pipelineHash);
        var existing = await ReadControlDocumentAsync<PipelineDocument>(
            documentId,
            cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            var generation = Generation(request.Transform.Fingerprint);
            var created = new PipelineDocument(
                "pipeline",
                CurrentFormatVersion,
                pipelineHash,
                request.Source.Fingerprint,
                request.Transform.Name,
                request.Transform.Fingerprint,
                generation,
                null,
                null,
                null);
            var createResponse = await SendJsonAsync(
                HttpMethod.Put,
                $"{_controlIndex}/_create/{documentId}?refresh=wait_for",
                created,
                cancellationToken,
                HttpStatusCode.Created,
                HttpStatusCode.Conflict).ConfigureAwait(false);
            if (createResponse.StatusCode is HttpStatusCode.Conflict)
            {
                existing = await ReadControlDocumentAsync<PipelineDocument>(
                    documentId,
                    cancellationToken).ConfigureAwait(false) ??
                    throw new OpenSearchSyncException(
                        $"OpenSearch reported a provisioning race for pipeline '{request.PipelineId}', but its control document is unavailable.");
            }
            else
            {
                SetRuntime(request, pipelineHash, generation, isBuilding: false);
                return new SyncProvisionResult(SyncProvisionStatus.Ready);
            }
        }

        EnsurePipelineDocument(existing, request.PipelineId, pipelineHash, request.Source);
        if (!string.Equals(
                existing.Source.ActiveTransformFingerprint,
                request.Transform.Fingerprint,
                StringComparison.Ordinal))
        {
            return new SyncProvisionResult(
                SyncProvisionStatus.RebuildRequired,
                existing.Source.ActiveTransformFingerprint);
        }

        SetRuntime(
            request,
            pipelineHash,
            existing.Source.ActiveGeneration,
            isBuilding: false);
        return new SyncProvisionResult(SyncProvisionStatus.Ready);
    }

    /// <inheritdoc />
    public async ValueTask ResetSnapshotAsync(
        string pipelineId,
        SnapshotReset reset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reset);
        var runtime = RequirePipeline(pipelineId, reset.Epoch.Source);
        await using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);
        var collections = await ReadCollectionsAsync(runtime, cancellationToken).ConfigureAwait(false);
        foreach (var collection in collections)
        {
            await DeleteAllDocumentsAsync(collection.Index, cancellationToken).ConfigureAwait(false);
            await DeleteAllDocumentsAsync(
                ReconciliationIndex(collection),
                cancellationToken).ConfigureAwait(false);
        }

        _ = await SendAsync(
            HttpMethod.Delete,
            $"{_controlIndex}/_doc/{CheckpointDocumentId(runtime)}?refresh=wait_for",
            null,
            null,
            cancellationToken,
            HttpStatusCode.OK,
            HttpStatusCode.NotFound).ConfigureAwait(false);
        var state = new SnapshotDocument(
            "snapshot",
            CurrentFormatVersion,
            runtime.PipelineHash,
            runtime.Source.Fingerprint,
            runtime.Transform.Fingerprint,
            runtime.Generation,
            reset.Epoch.Value.ToString("N"),
            false);
        _ = await SendJsonAsync(
            HttpMethod.Put,
            $"{_controlIndex}/_doc/{SnapshotDocumentId(runtime)}?refresh=wait_for",
            state,
            cancellationToken,
            HttpStatusCode.OK,
            HttpStatusCode.Created).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask StartSnapshotAsync(
        string pipelineId,
        SnapshotStart start,
        SyncTransformVersion transform,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        var runtime = RequirePipeline(pipelineId, start.Epoch.Source, transform);
        await ValidateSnapshotStateAsync(
            runtime,
            start.Epoch.Value,
            requireComplete: false,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ApplySnapshotBatchAsync(
        SyncSnapshotBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var runtime = RequirePipeline(
            batch.PipelineId,
            batch.SourceBatch.Epoch.Source,
            batch.Transform);
        ValidateLimits(
            batch.Mutations.Count,
            batch.Mutations.Select(mutation => mutation.Content));
        await using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);
        await ValidateSnapshotStateAsync(
            runtime,
            batch.SourceBatch.Epoch.Value,
            requireComplete: false,
            cancellationToken).ConfigureAwait(false);
        var operations = new List<MaterializedOperation>(batch.Mutations.Count);
        foreach (var mutation in batch.Mutations)
        {
            ValidateJsonDocument(mutation.Content, mutation.ContentType);
            ValidateReconciliationKey(mutation.Key);
            var collection = await EnsureCollectionAsync(
                runtime,
                mutation.Collection,
                cancellationToken).ConfigureAwait(false);
            operations.Add(new MaterializedOperation(
                SyncMutationKind.Upsert,
                collection,
                mutation.Key,
                StableDocumentId(mutation.Collection, mutation.Key),
                mutation.Content,
                mutation.ContentType,
                mutation.PartitionKey));
        }

        await ExecuteBulkAsync(operations, null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask CompleteSnapshotAsync(
        string pipelineId,
        SnapshotComplete complete,
        SyncTransformVersion transform,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(complete);
        var runtime = RequirePipeline(pipelineId, complete.Epoch.Source, transform);
        await using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);
        var current = await ValidateSnapshotStateAsync(
            runtime,
            complete.Epoch.Value,
            requireComplete: false,
            cancellationToken).ConfigureAwait(false);
        var completed = current with { Complete = true };
        _ = await SendJsonAsync(
            HttpMethod.Put,
            $"{_controlIndex}/_doc/{SnapshotDocumentId(runtime)}?refresh=wait_for",
            completed,
            cancellationToken,
            HttpStatusCode.OK,
            HttpStatusCode.Created).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<SyncApplyResult> ApplyTransactionAsync(
        SyncTransactionBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var runtime = RequirePipeline(batch.PipelineId, batch.Transaction.Source);
        if (!string.Equals(
                runtime.Transform.Fingerprint,
                batch.Transform.Fingerprint,
                StringComparison.Ordinal))
        {
            return new SyncApplyResult(
                SyncApplyStatus.TransformVersionMismatch,
                null,
                runtime.Transform.Fingerprint);
        }

        var result = await ApplyTransactionCoreAsync(
            runtime,
            batch,
            replay: false,
            cancellationToken).ConfigureAwait(false);
        return result.Status is SyncQuarantineReplayApplyStatus.Applied
            ? SyncApplyResult.Applied(batch.Transaction.CommitEndPosition)
            : SyncApplyResult.AlreadyApplied(batch.Transaction.CommitEndPosition);
    }

    private async ValueTask<SyncQuarantineReplayApplyResult> ApplyTransactionCoreAsync(
        PipelineRuntime runtime,
        SyncTransactionBatch batch,
        bool replay,
        CancellationToken cancellationToken)
    {
        var position = batch.Transaction.CommitEndPosition;
        var externalVersion = ToExternalVersion(position);
        ValidateLimits(batch.Mutations.Count, batch.Mutations.Select(static mutation => mutation.Content));
        await using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);
        var existingCheckpoint = await ReadCheckpointAsync(runtime, cancellationToken)
            .ConfigureAwait(false);
        if (existingCheckpoint is not null &&
            existingCheckpoint.Position == position.Value &&
            existingCheckpoint.TransactionId != batch.Transaction.TransactionId)
        {
            throw new OpenSearchSyncException(
                $"OpenSearch checkpoint {position} for pipeline '{batch.PipelineId}' belongs to transaction {existingCheckpoint.TransactionId}, not {batch.Transaction.TransactionId}.");
        }

        if (existingCheckpoint is not null && existingCheckpoint.Position >= position.Value)
        {
            return new SyncQuarantineReplayApplyResult(
                replay && existingCheckpoint.Position > position.Value
                    ? SyncQuarantineReplayApplyStatus.CheckpointAdvanced
                    : SyncQuarantineReplayApplyStatus.AlreadyApplied,
                new BlueTuskLogSequenceNumber(existingCheckpoint.Position));
        }

        var plan = PlanTransaction(batch.Mutations);
        if (replay && plan.ResetCollections.Count != 0)
        {
            throw new SyncDestinationDurabilityException(
                "OpenSearch cannot safely replay an unscoped collection delete; use rebuild or reconciliation.");
        }

        foreach (var collectionName in plan.ResetCollections)
        {
            var collection = await EnsureCollectionAsync(
                runtime,
                collectionName,
                cancellationToken).ConfigureAwait(false);
            await DeleteAllDocumentsAsync(collection.Index, cancellationToken).ConfigureAwait(false);
            await DeleteAllDocumentsAsync(
                ReconciliationIndex(collection),
                cancellationToken).ConfigureAwait(false);
        }

        var operations = new List<MaterializedOperation>(plan.Mutations.Count);
        foreach (var mutation in plan.Mutations)
        {
            if (mutation.Kind is SyncMutationKind.Upsert)
            {
                ValidateJsonDocument(mutation.Content, mutation.ContentType!);
            }

            ValidateReconciliationKey(mutation.Key!);
            var collection = await EnsureCollectionAsync(
                runtime,
                mutation.Collection,
                cancellationToken).ConfigureAwait(false);
            operations.Add(new MaterializedOperation(
                mutation.Kind,
                collection,
                mutation.Key!,
                StableDocumentId(mutation.Collection, mutation.Key!),
                mutation.Content,
                mutation.ContentType,
                mutation.PartitionKey));
        }

        await ExecuteBulkAsync(operations, externalVersion, cancellationToken).ConfigureAwait(false);
        var checkpoint = new CheckpointDocument(
            "checkpoint",
            CurrentFormatVersion,
            runtime.PipelineHash,
            runtime.Source.Fingerprint,
            runtime.Transform.Fingerprint,
            runtime.Generation,
            position.Value,
            batch.Transaction.TransactionId);
        var checkpointResponse = await SendJsonAsync(
            HttpMethod.Put,
            $"{_controlIndex}/_doc/{CheckpointDocumentId(runtime)}?version={externalVersion.ToString(CultureInfo.InvariantCulture)}&version_type=external_gte&refresh=wait_for",
            checkpoint,
            cancellationToken,
            HttpStatusCode.OK,
            HttpStatusCode.Created,
            HttpStatusCode.Conflict).ConfigureAwait(false);
        if (checkpointResponse.StatusCode is HttpStatusCode.Conflict)
        {
            existingCheckpoint = await ReadCheckpointAsync(runtime, cancellationToken)
                .ConfigureAwait(false);
            if (existingCheckpoint is null || existingCheckpoint.Position < position.Value)
            {
                throw new OpenSearchSyncException(
                    $"OpenSearch rejected checkpoint {position} for pipeline '{batch.PipelineId}' without exposing an equal or later durable checkpoint.");
            }

            if (existingCheckpoint.Position == position.Value &&
                existingCheckpoint.TransactionId != batch.Transaction.TransactionId)
            {
                throw new OpenSearchSyncException(
                    $"OpenSearch checkpoint {position} for pipeline '{batch.PipelineId}' belongs to transaction {existingCheckpoint.TransactionId}, not {batch.Transaction.TransactionId}.");
            }

            return new SyncQuarantineReplayApplyResult(
                replay && existingCheckpoint.Position > position.Value
                    ? SyncQuarantineReplayApplyStatus.CheckpointAdvanced
                    : SyncQuarantineReplayApplyStatus.AlreadyApplied,
                new BlueTuskLogSequenceNumber(existingCheckpoint.Position));
        }

        return new SyncQuarantineReplayApplyResult(
            SyncQuarantineReplayApplyStatus.Applied,
            position);
    }

    /// <summary>Reads one materialized JSON document from the provisioned target generation.</summary>
    public async ValueTask<ReadOnlyMemory<byte>?> ReadDocumentAsync(
        string pipelineId,
        string collection,
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var runtime = RequirePipeline(pipelineId);
        var index = CollectionIndex(runtime, collection);
        var response = await SendAsync(
            HttpMethod.Get,
            $"{index}/_doc/{StableDocumentId(collection, key)}",
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
        var source = document.RootElement.GetProperty("_source");
        return Encoding.UTF8.GetBytes(source.GetRawText());
    }

    /// <inheritdoc />
    public async ValueTask<bool> StoreAsync(
        SyncQuarantineRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var runtime = RequirePipeline(record.PipelineId, record.Source, record.Transform);
        var documentId = QuarantineDocumentId(
            runtime,
            record.CommitEndPosition,
            record.TransactionId);
        var response = await SendJsonAsync(
            HttpMethod.Put,
            $"{_controlIndex}/_create/{documentId}?refresh=wait_for",
            new QuarantineDocument(
                "quarantine",
                CurrentFormatVersion,
                runtime.PipelineHash,
                runtime.Source.Fingerprint,
                runtime.Transform.Fingerprint,
                record.CommitEndPosition.Value,
                record.TransactionId,
                record.ErrorType,
                record.ErrorMessage,
                record.RecordedAt,
                null,
                null),
            cancellationToken,
            HttpStatusCode.Created,
            HttpStatusCode.Conflict).ConfigureAwait(false);
        return response.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict;
    }

    public async ValueTask<SyncQuarantineEntry?> ReadAsync(
        SyncQuarantineIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var runtime = RequirePipeline(identity.PipelineId, identity.Source);
        var control = await ReadControlDocumentAsync<QuarantineDocument>(
            QuarantineDocumentId(runtime, identity.CommitEndPosition, identity.TransactionId),
            cancellationToken).ConfigureAwait(false);
        return control is null ? null : ToQuarantineEntry(identity, control.Source);
    }

    public async ValueTask<SyncQuarantineResolutionResult> ResolveAsync(
        SyncQuarantineIdentity identity,
        string expectedTransformFingerprint,
        string operationId,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedTransformFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(operationId.Length, 128);
        var runtime = RequirePipeline(identity.PipelineId, identity.Source);
        var documentId = QuarantineDocumentId(
            runtime,
            identity.CommitEndPosition,
            identity.TransactionId);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var control = await ReadControlDocumentAsync<QuarantineDocument>(
                documentId,
                cancellationToken).ConfigureAwait(false);
            if (control is null)
            {
                return new SyncQuarantineResolutionResult(
                    SyncQuarantineResolutionStatus.NotFound,
                    null);
            }

            var current = ToQuarantineEntry(identity, control.Source);
            if (!string.Equals(
                    current.TransformFingerprint,
                    expectedTransformFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new SyncQuarantineResolutionResult(
                    SyncQuarantineResolutionStatus.Conflict,
                    current);
            }

            if (current.ResolvedOperationId is not null)
            {
                return new SyncQuarantineResolutionResult(
                    SyncQuarantineResolutionStatus.AlreadyResolved,
                    current);
            }

            var replacement = control.Source with
            {
                ResolvedOperationId = operationId,
                ResolvedAt = resolvedAt,
            };
            var response = await SendJsonAsync(
                HttpMethod.Put,
                $"{_controlIndex}/_doc/{documentId}?if_seq_no={control.SequenceNumber.ToString(CultureInfo.InvariantCulture)}&if_primary_term={control.PrimaryTerm.ToString(CultureInfo.InvariantCulture)}&refresh=wait_for",
                replacement,
                cancellationToken,
                HttpStatusCode.OK,
                HttpStatusCode.Conflict).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.OK)
            {
                return new SyncQuarantineResolutionResult(
                    SyncQuarantineResolutionStatus.Resolved,
                    ToQuarantineEntry(identity, replacement));
            }
        }

        var latest = await ReadAsync(identity, cancellationToken).ConfigureAwait(false);
        return new SyncQuarantineResolutionResult(
            latest?.ResolvedOperationId is null
                ? SyncQuarantineResolutionStatus.Conflict
                : SyncQuarantineResolutionStatus.AlreadyResolved,
            latest);
    }

    public async ValueTask<SyncQuarantineReplayApplyResult> ReplayTransactionAsync(
        SyncTransactionBatch batch,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(operationId.Length, 128);
        var runtime = RequirePipeline(batch.PipelineId, batch.Transaction.Source, batch.Transform);
        return await ApplyTransactionCoreAsync(
            runtime,
            batch,
            replay: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task InitializeCoreAsync()
    {
        var response = await SendJsonAsync(
            HttpMethod.Put,
            _controlIndex,
            new
            {
                settings = new
                {
                    number_of_shards = 1,
                    number_of_replicas = _options.NumberOfReplicas,
                },
                mappings = new
                {
                    dynamic = true,
                    properties = new
                    {
                        recordType = new { type = "keyword" },
                        pipelineHash = new { type = "keyword" },
                        generation = new { type = "keyword" },
                        sourceFingerprint = new { type = "keyword" },
                        transformFingerprint = new { type = "keyword" },
                        collection = new { type = "keyword" },
                        index = new { type = "keyword" },
                        alias = new { type = "keyword" },
                        position = new { type = "unsigned_long" },
                    },
                    _meta = new
                    {
                        product = "BlueTusk Sync",
                        formatVersion = CurrentFormatVersion,
                    },
                },
            },
            CancellationToken.None,
            HttpStatusCode.OK,
            HttpStatusCode.BadRequest).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.BadRequest &&
            !response.Text.Contains("resource_already_exists_exception", StringComparison.Ordinal))
        {
            throw CreateResponseException(HttpMethod.Put, _controlIndex, response);
        }

        await ValidateControlIndexAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void SetRuntime(
        SyncProvisionRequest request,
        string pipelineHash,
        string generation,
        bool isBuilding)
    {
        var runtime = new PipelineRuntime(
            request.PipelineId,
            pipelineHash,
            request.Source,
            request.Transform,
            generation,
            isBuilding);
        _pipelines.AddOrUpdate(request.PipelineId, runtime, (_, _) => runtime);
    }

    private PipelineRuntime RequirePipeline(
        string pipelineId,
        ChangeSourceIdentity? source = null,
        SyncTransformVersion? transform = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        if (!_pipelines.TryGetValue(pipelineId, out var runtime))
        {
            throw new OpenSearchSyncException(
                $"OpenSearch Sync pipeline '{pipelineId}' must be provisioned or placed into rebuild mode before use.");
        }

        if (source is not null &&
            !string.Equals(source.Fingerprint, runtime.Source.Fingerprint, StringComparison.Ordinal))
        {
            throw new OpenSearchSyncSourceMismatchException(
                $"OpenSearch Sync pipeline '{pipelineId}' belongs to source '{runtime.Source.Fingerprint}', not '{source.Fingerprint}'.");
        }

        if (transform is not null &&
            !string.Equals(transform.Fingerprint, runtime.Transform.Fingerprint, StringComparison.Ordinal))
        {
            throw new SyncTransformVersionMismatchException(
                runtime.Transform.Fingerprint,
                transform.Fingerprint);
        }

        return runtime;
    }

    private void ValidateLimits(int mutationCount, IEnumerable<ReadOnlyMemory<byte>> contents)
    {
        if (mutationCount > _options.MaxMutationsPerTransaction)
        {
            throw new OpenSearchSyncBulkException(
                $"The Sync batch contains {mutationCount} mutations; the configured maximum is {_options.MaxMutationsPerTransaction}.");
        }

        long total = 0;
        foreach (var content in contents)
        {
            if (content.Length > _options.MaxDocumentBytes)
            {
                throw new OpenSearchSyncBulkException(
                    $"A Sync document contains {content.Length} bytes; the configured maximum is {_options.MaxDocumentBytes}.");
            }

            total = checked(total + content.Length);
        }

        if (total > _options.MaxBulkBytes)
        {
            throw new OpenSearchSyncBulkException(
                $"The Sync batch contains {total} document bytes; the configured maximum is {_options.MaxBulkBytes}.");
        }
    }

    private static long ToExternalVersion(BlueTuskLogSequenceNumber position)
    {
        if (position.Value is 0 || position.Value > long.MaxValue)
        {
            throw new OpenSearchSyncException(
                $"PostgreSQL commit position {position} cannot be represented as a positive OpenSearch external version.");
        }

        return checked((long)position.Value);
    }

    private static void ValidateJsonDocument(ReadOnlyMemory<byte> content, string contentType)
    {
        if (!contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new OpenSearchSyncBulkException(
                $"OpenSearch materialisations require application/json content, not '{contentType}'.");
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                throw new OpenSearchSyncBulkException(
                    "OpenSearch materialisations require each document to be a JSON object.");
            }
        }
        catch (JsonException exception)
        {
            throw new OpenSearchSyncBulkException(
                $"OpenSearch materialisation contains invalid JSON: {exception.Message}");
        }
    }

    private static TransactionPlan PlanTransaction(IReadOnlyList<SyncMutation> mutations)
    {
        var resets = new HashSet<string>(StringComparer.Ordinal);
        var resetOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
        var latest = new Dictionary<(string Collection, string Key), (int Ordinal, SyncMutation Mutation)>();
        for (var index = 0; index < mutations.Count; index++)
        {
            var mutation = mutations[index];
            if (mutation.Kind is SyncMutationKind.DeleteCollection)
            {
                resets.Add(mutation.Collection);
                resetOrdinals[mutation.Collection] = index;
                continue;
            }

            latest[(mutation.Collection, mutation.Key!)] = (index, mutation);
        }

        return new TransactionPlan(
            resets.Order(StringComparer.Ordinal).ToArray(),
            latest.Values
                .Where(value =>
                    !resetOrdinals.TryGetValue(value.Mutation.Collection, out var resetOrdinal) ||
                    value.Ordinal > resetOrdinal)
                .OrderBy(value => value.Ordinal)
                .Select(value => value.Mutation)
                .ToArray());
    }

    private static string Fingerprint(string value, int length = 64)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        return hash[..length];
    }

    private static string Generation(string transformFingerprint) => transformFingerprint[..16];

    private static string StableDocumentId(string collection, string key) =>
        Fingerprint(collection + "\n" + key);

    private void ValidateReconciliationKey(string key)
    {
        var byteCount = Encoding.UTF8.GetByteCount(key);
        if (byteCount > _options.MaxReconciliationKeyBytes)
        {
            throw new OpenSearchSyncBulkException(
                $"A Sync logical key contains {byteCount} UTF-8 bytes; the configured reconciliation maximum is {_options.MaxReconciliationKeyBytes}.");
        }
    }

    private static string PipelineDocumentId(string pipelineHash) => "pipeline-" + pipelineHash;

    private static string SnapshotDocumentId(PipelineRuntime runtime) =>
        $"snapshot-{runtime.PipelineHash}-{runtime.Generation}";

    private static string CheckpointDocumentId(PipelineRuntime runtime) =>
        $"checkpoint-{runtime.PipelineHash}-{runtime.Generation}";

    private static string QuarantineDocumentId(
        PipelineRuntime runtime,
        BlueTuskLogSequenceNumber position,
        uint transactionId) =>
        "quarantine-" + Fingerprint(
            $"{runtime.PipelineHash}\n{position.Value:x16}\n{transactionId:x8}",
            48);

    private static SyncQuarantineEntry ToQuarantineEntry(
        SyncQuarantineIdentity identity,
        QuarantineDocument document)
    {
        var pipelineHash = Fingerprint(identity.PipelineId, 24);
        if (!string.Equals(document.RecordType, "quarantine", StringComparison.Ordinal) ||
            !string.Equals(document.PipelineHash, pipelineHash, StringComparison.Ordinal) ||
            !string.Equals(
                document.SourceFingerprint,
                identity.Source.Fingerprint,
                StringComparison.Ordinal) ||
            document.Position != identity.CommitEndPosition.Value ||
            document.TransactionId != identity.TransactionId)
        {
            throw new OpenSearchSyncException(
                "The OpenSearch quarantine document does not match its requested identity.");
        }

        return new SyncQuarantineEntry(
            identity,
            document.TransformFingerprint,
            document.ErrorType,
            document.ErrorMessage,
            document.RecordedAt,
            document.ResolvedOperationId,
            document.ResolvedAt);
    }

    private sealed record PipelineDocument(
        string RecordType,
        int FormatVersion,
        string PipelineHash,
        string SourceFingerprint,
        string ActiveTransformName,
        string ActiveTransformFingerprint,
        string ActiveGeneration,
        string? BuildingTransformName,
        string? BuildingTransformFingerprint,
        string? BuildingGeneration);

    private sealed record SnapshotDocument(
        string RecordType,
        int FormatVersion,
        string PipelineHash,
        string SourceFingerprint,
        string TransformFingerprint,
        string Generation,
        string Epoch,
        bool Complete);

    private sealed record CheckpointDocument(
        string RecordType,
        int FormatVersion,
        string PipelineHash,
        string SourceFingerprint,
        string TransformFingerprint,
        string Generation,
        ulong Position,
        uint TransactionId);

    private sealed record QuarantineDocument(
        string RecordType,
        int FormatVersion,
        string PipelineHash,
        string SourceFingerprint,
        string TransformFingerprint,
        ulong Position,
        uint TransactionId,
        string ErrorType,
        string ErrorMessage,
        DateTimeOffset RecordedAt,
        string? ResolvedOperationId,
        DateTimeOffset? ResolvedAt);

    private sealed record CollectionDocument(
        string RecordType,
        int FormatVersion,
        string PipelineHash,
        string Generation,
        string Collection,
        string Index,
        string Alias);

    private sealed record ControlDocument<T>(T Source, long SequenceNumber, long PrimaryTerm);

    private sealed record ResponsePayload(HttpStatusCode StatusCode, byte[] Content)
    {
        internal string Text => Encoding.UTF8.GetString(Content);
    }

    private sealed record MaterializedOperation(
        SyncMutationKind Kind,
        CollectionDocument Collection,
        string Key,
        string DocumentId,
        ReadOnlyMemory<byte> Content,
        string? ContentType,
        string? Routing);

    private sealed record BulkOperation(
        SyncMutationKind Kind,
        string Index,
        string DocumentId,
        ReadOnlyMemory<byte> Content,
        string? Routing,
        string Description);

    private sealed record ReconciliationDocument(
        string RecordType,
        int FormatVersion,
        string PipelineHash,
        string Generation,
        string Collection,
        string Key,
        uint KeyHash,
        string ContentHash,
        string? ContentType,
        string? PartitionKey);

    private sealed record TransactionPlan(
        IReadOnlyList<string> ResetCollections,
        IReadOnlyList<SyncMutation> Mutations);

    private sealed class PipelineRuntime
    {
        private readonly Channel<bool> _gate = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
            });
        private readonly ConcurrentDictionary<string, CollectionDocument> _collections =
            new(StringComparer.Ordinal);

        internal PipelineRuntime(
            string pipelineId,
            string pipelineHash,
            ChangeSourceIdentity source,
            SyncTransformVersion transform,
            string generation,
            bool isBuilding)
        {
            PipelineId = pipelineId;
            PipelineHash = pipelineHash;
            Source = source;
            Transform = transform;
            Generation = generation;
            IsBuilding = isBuilding;
            if (!_gate.Writer.TryWrite(true))
            {
                throw new OpenSearchSyncException("Unable to initialize the OpenSearch pipeline operation gate.");
            }
        }

        internal string PipelineId { get; }

        internal string PipelineHash { get; }

        internal ChangeSourceIdentity Source { get; }

        internal SyncTransformVersion Transform { get; }

        internal string Generation { get; }

        internal bool IsBuilding { get; }

        internal CollectionDocument? GetCollection(string collection) =>
            _collections.TryGetValue(collection, out var definition) ? definition : null;

        internal void CacheCollection(CollectionDocument definition) =>
            _collections.AddOrUpdate(definition.Collection, definition, (_, _) => definition);

        internal async ValueTask<GateLease> EnterAsync(CancellationToken cancellationToken)
        {
            _ = await _gate.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return new GateLease(_gate.Writer);
        }
    }

    private sealed class GateLease(ChannelWriter<bool> writer) : IAsyncDisposable
    {
        private ChannelWriter<bool>? _writer = writer;

        public ValueTask DisposeAsync()
        {
            var current = Interlocked.Exchange(ref _writer, null);
            if (current is not null && !current.TryWrite(true))
            {
                throw new OpenSearchSyncException(
                    "Unable to release the OpenSearch pipeline operation gate.");
            }

            return ValueTask.CompletedTask;
        }
    }
}
