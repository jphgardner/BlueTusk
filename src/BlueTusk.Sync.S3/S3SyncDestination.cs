using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueTusk.Streams;

namespace BlueTusk.Sync.S3;

public sealed class S3SyncDestination : ISyncDestination, ISyncQuarantineReplayDestination
{
    private readonly S3SyncOptions _options;
    private readonly IS3SyncObjectStore _store;
    private ProvisionedPipeline? _pipeline;

    public S3SyncDestination(S3SyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _store = options.ObjectStoreFactory?.Invoke(options) ?? new AwsS3SyncObjectStore(options);
    }

    public string Name => "S3 Parquet lake";

    public SyncDestinationCapabilities Capabilities =>
        SyncDestinationCapabilities.TransactionalBatches |
        SyncDestinationCapabilities.IdempotentUpserts |
        SyncDestinationCapabilities.Deletes;

    public async ValueTask<SyncProvisionResult> ProvisionAsync(
        SyncProvisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var configuration = await _store.LoadConfigurationAsync(cancellationToken)
            .ConfigureAwait(false);
        if (configuration is not null &&
            (!string.Equals(configuration.PipelineId, request.PipelineId, StringComparison.Ordinal) ||
             !string.Equals(
                 configuration.SourceFingerprint,
                 request.Source.Fingerprint,
                 StringComparison.Ordinal)))
        {
            throw new S3SyncConfigurationException(
                "The S3 prefix belongs to a different BlueTusk pipeline or PostgreSQL source.");
        }

        if (configuration is not null &&
            !string.Equals(
                configuration.TransformFingerprint,
                request.Transform.Fingerprint,
                StringComparison.Ordinal))
        {
            return new SyncProvisionResult(
                SyncProvisionStatus.RebuildRequired,
                configuration.TransformFingerprint);
        }

        if (configuration is null)
        {
            await _store.WriteConfigurationAsync(
                new S3SyncConfiguration(
                    request.PipelineId,
                    request.Source.Fingerprint,
                    request.Transform.Fingerprint),
                cancellationToken).ConfigureAwait(false);
        }

        Volatile.Write(
            ref _pipeline,
            new ProvisionedPipeline(request.PipelineId, request.Source, request.Transform));
        return new SyncProvisionResult(SyncProvisionStatus.Ready);
    }

    public async ValueTask ResetSnapshotAsync(
        string pipelineId,
        SnapshotReset reset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reset);
        var pipeline = RequirePipeline(pipelineId, reset.Epoch.Source);
        var epoch = reset.Epoch.Value.ToString("N");
        await CommitOrderedAsync(
            pipeline,
            "snapshot.reset",
            DeliveryId("snapshot-reset", pipeline.PipelineId, pipeline.Source.Fingerprint, epoch),
            $"snapshots/{epoch}/00000000000000000000-reset",
            null,
            0,
            new { abandonedEpoch = reset.AbandonedEpoch, reset.Reason },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask StartSnapshotAsync(
        string pipelineId,
        SnapshotStart start,
        SyncTransformVersion transform,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        var pipeline = RequirePipeline(pipelineId, start.Epoch.Source, transform);
        var epoch = start.Epoch.Value.ToString("N");
        await CommitOrderedAsync(
            pipeline,
            "snapshot.start",
            DeliveryId("snapshot-start", pipeline.PipelineId, pipeline.Source.Fingerprint, epoch),
            $"snapshots/{epoch}/00000000000000000001-start",
            null,
            0,
            new { start.TableCount },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ApplySnapshotBatchAsync(
        SyncSnapshotBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var pipeline = RequirePipeline(
            batch.PipelineId,
            batch.SourceBatch.Epoch.Source,
            batch.Transform);
        var epoch = batch.SourceBatch.Epoch.Value.ToString("N");
        var table = SafePath(batch.SourceBatch.Table.Schema + "." + batch.SourceBatch.Table.Name);
        var sequence = batch.SourceBatch.Sequence.ToString("D20", CultureInfo.InvariantCulture);
        var deliveryId = DeliveryId(
            "snapshot-batch",
            pipeline.PipelineId,
            pipeline.Source.Fingerprint,
            epoch,
            table,
            sequence);
        var parquet = await S3SyncParquetCodec.EncodeSnapshotBatchAsync(
            deliveryId,
            batch,
            _options.MaxMutationCount,
            _options.MaxParquetBytes,
            cancellationToken).ConfigureAwait(false);
        await CommitOrderedAsync(
            pipeline,
            "snapshot.batch",
            deliveryId,
            $"snapshots/{epoch}/{table}/{sequence}",
            parquet,
            batch.Mutations.Count,
            new
            {
                table = batch.SourceBatch.Table.Schema + "." + batch.SourceBatch.Table.Name,
                batch.SourceBatch.Sequence,
                batch.SourceBatch.IsLastForTable,
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CompleteSnapshotAsync(
        string pipelineId,
        SnapshotComplete complete,
        SyncTransformVersion transform,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(complete);
        var pipeline = RequirePipeline(pipelineId, complete.Epoch.Source, transform);
        var epoch = complete.Epoch.Value.ToString("N");
        await CommitOrderedAsync(
            pipeline,
            "snapshot.complete",
            DeliveryId("snapshot-complete", pipeline.PipelineId, pipeline.Source.Fingerprint, epoch),
            $"snapshots/{epoch}/99999999999999999999-complete",
            null,
            0,
            new { complete.RowCount, complete.TableCount },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SyncApplyResult> ApplyTransactionAsync(
        SyncTransactionBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var pipeline = RequirePipeline(batch.PipelineId, batch.Transaction.Source);
        if (!string.Equals(
                batch.Transform.Fingerprint,
                pipeline.Transform.Fingerprint,
                StringComparison.Ordinal))
        {
            return new SyncApplyResult(
                SyncApplyStatus.TransformVersionMismatch,
                null,
                pipeline.Transform.Fingerprint);
        }

        var duplicate = await CommitTransactionAsync(pipeline, batch, cancellationToken)
            .ConfigureAwait(false);
        return duplicate
            ? SyncApplyResult.AlreadyApplied(batch.Transaction.CommitEndPosition)
            : SyncApplyResult.Applied(batch.Transaction.CommitEndPosition);
    }

    public async ValueTask<SyncQuarantineReplayApplyResult> ReplayTransactionAsync(
        SyncTransactionBatch batch,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(operationId.Length, 128);
        var pipeline = RequirePipeline(batch.PipelineId, batch.Transaction.Source, batch.Transform);
        var duplicate = await CommitTransactionAsync(pipeline, batch, cancellationToken)
            .ConfigureAwait(false);
        return new SyncQuarantineReplayApplyResult(
            duplicate
                ? SyncQuarantineReplayApplyStatus.AlreadyApplied
                : SyncQuarantineReplayApplyStatus.Applied,
            batch.Transaction.CommitEndPosition);
    }

    private async ValueTask<bool> CommitTransactionAsync(
        ProvisionedPipeline pipeline,
        SyncTransactionBatch batch,
        CancellationToken cancellationToken)
    {
        var position = batch.Transaction.CommitEndPosition.Value.ToString(
            "x16",
            CultureInfo.InvariantCulture);
        var transactionId = batch.Transaction.TransactionId.ToString(
            "x8",
            CultureInfo.InvariantCulture);
        var deliveryId = DeliveryId(
            "transaction",
            pipeline.PipelineId,
            pipeline.Source.Fingerprint,
            position,
            transactionId);
        var parquet = await S3SyncParquetCodec.EncodeTransactionAsync(
            deliveryId,
            batch,
            _options.MaxMutationCount,
            _options.MaxParquetBytes,
            cancellationToken).ConfigureAwait(false);
        return await CommitOrderedAsync(
            pipeline,
            "transaction",
            deliveryId,
            $"transactions/{position}-{transactionId}",
            parquet,
            batch.Mutations.Count,
            new
            {
                batch.Transaction.TransactionId,
                commitEndPosition = position,
                batch.Transaction.CommitTimestamp,
                outcome = batch.Transaction.Outcome.ToString(),
                batch.Transaction.GlobalTransactionId,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> CommitOrderedAsync(
        ProvisionedPipeline pipeline,
        string eventName,
        string deliveryId,
        string relativeKey,
        byte[]? parquet,
        int rowCount,
        object eventMetadata,
        CancellationToken cancellationToken)
    {
        await pipeline.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var root = _options.ObjectPrefix;
            var manifestKey = $"{root}/commits/{relativeKey}.json";
            if (await _store.CommitExistsAsync(manifestKey, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            var dataKey = parquet is null ? null : $"{root}/data/{relativeKey}.parquet";
            var dataHash = parquet is null
                ? null
                : Convert.ToHexStringLower(SHA256.HashData(parquet));
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                formatVersion = 1,
                @event = eventName,
                deliveryId,
                pipelineId = pipeline.PipelineId,
                sourceFingerprint = pipeline.Source.Fingerprint,
                transformName = pipeline.Transform.Name,
                transformFingerprint = pipeline.Transform.Fingerprint,
                dataKey,
                dataSha256 = dataHash,
                rowCount,
                metadata = eventMetadata,
            });
            await _store.CommitAsync(
                dataKey,
                parquet,
                manifestKey,
                manifest,
                cancellationToken).ConfigureAwait(false);
            return false;
        }
        finally
        {
            _ = pipeline.Gate.Release();
        }
    }

    private ProvisionedPipeline RequirePipeline(
        string pipelineId,
        ChangeSourceIdentity source,
        SyncTransformVersion? transform = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        var pipeline = Volatile.Read(ref _pipeline) ??
            throw new InvalidOperationException(
                "The S3 destination must be provisioned before it can receive data.");
        if (!string.Equals(pipeline.PipelineId, pipelineId, StringComparison.Ordinal) ||
            pipeline.Source != source)
        {
            throw new InvalidOperationException(
                "The S3 destination was provisioned for a different pipeline or PostgreSQL source.");
        }

        if (transform is not null &&
            !string.Equals(
                pipeline.Transform.Fingerprint,
                transform.Fingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The S3 destination transform changed and requires an explicit rebuild.");
        }

        return pipeline;
    }

    private static string SafePath(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string DeliveryId(string kind, params string[] components)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, kind);
        foreach (var component in components)
        {
            Append(hash, component);
        }

        return "bt1-" + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private sealed record ProvisionedPipeline(
        string PipelineId,
        ChangeSourceIdentity Source,
        SyncTransformVersion Transform)
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);
    }
}
