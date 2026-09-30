using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BlueTusk.Streams;

namespace BlueTusk.Sync.Kafka;

public sealed class KafkaSyncDestination :
    ISyncDestination,
    ISyncQuarantineReplayDestination,
    IAsyncDisposable
{
    private const string PositionCheckpoint = "position";
    private readonly KafkaSyncOptions _options;
    private readonly IKafkaSyncTransport _transport;
    private ProvisionedPipeline? _pipeline;

    public KafkaSyncDestination(KafkaSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _transport = options.TransportFactory?.Invoke(options) ??
            new ConfluentKafkaSyncTransport(options);
    }

    public string Name => "Apache Kafka";

    public SyncDestinationCapabilities Capabilities =>
        SyncDestinationCapabilities.TransactionalBatches |
        SyncDestinationCapabilities.IdempotentUpserts |
        SyncDestinationCapabilities.Deletes;

    public async ValueTask<SyncProvisionResult> ProvisionAsync(
        SyncProvisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await _transport.LoadAsync(cancellationToken).ConfigureAwait(false);
        var newConfiguration = loaded.PipelineId is null;
        if (!newConfiguration &&
            (!string.Equals(loaded.PipelineId, request.PipelineId, StringComparison.Ordinal) ||
             !string.Equals(
                 loaded.SourceFingerprint,
                 request.Source.Fingerprint,
                 StringComparison.Ordinal)))
        {
            throw new KafkaSyncConfigurationException(
                "The Kafka state topic belongs to a different BlueTusk pipeline or PostgreSQL source.");
        }

        if (!newConfiguration &&
            !string.Equals(
                loaded.TransformFingerprint,
                request.Transform.Fingerprint,
                StringComparison.Ordinal))
        {
            return new SyncProvisionResult(
                SyncProvisionStatus.RebuildRequired,
                loaded.TransformFingerprint);
        }

        await _transport.InitializeAsync(request, newConfiguration, cancellationToken)
            .ConfigureAwait(false);
        Volatile.Write(
            ref _pipeline,
            new ProvisionedPipeline(
                request.PipelineId,
                request.Source,
                request.Transform,
                new Dictionary<string, string>(loaded.Checkpoints, StringComparer.Ordinal)));
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
        var checkpoint = $"snapshot:{epoch}:reset";
        var deliveryId = DeliveryId(
            "snapshot-reset",
            pipeline.PipelineId,
            pipeline.Source.Fingerprint,
            epoch);
        var payload = KafkaSyncEnvelopeCodec.EncodeSnapshotReset(
            deliveryId,
            pipelineId,
            pipeline.Transform,
            reset,
            _options.MaxEnvelopeBytes);
        var tombstones = pipeline.Checkpoints.Keys
            .Where(key => key.StartsWith("snapshot:", StringComparison.Ordinal) &&
                !key.StartsWith($"snapshot:{epoch}:", StringComparison.Ordinal))
            .ToArray();
        _ = await PublishOrderedAsync(
            pipeline,
            "snapshot.reset",
            deliveryId,
            payload,
            checkpoint,
            "1",
            tombstones,
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
        var checkpoint = $"snapshot:{epoch}:start";
        var deliveryId = DeliveryId(
            "snapshot-start",
            pipeline.PipelineId,
            pipeline.Source.Fingerprint,
            epoch);
        var payload = KafkaSyncEnvelopeCodec.EncodeSnapshotStart(
            deliveryId,
            pipelineId,
            transform,
            start,
            _options.MaxEnvelopeBytes);
        _ = await PublishOrderedAsync(
            pipeline,
            "snapshot.start",
            deliveryId,
            payload,
            checkpoint,
            "1",
            [],
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
        var table = batch.SourceBatch.Table.Schema + "." + batch.SourceBatch.Table.Name;
        var checkpoint = $"snapshot:{epoch}:table:{table}";
        if (pipeline.Checkpoints.TryGetValue(checkpoint, out var current) &&
            long.TryParse(current, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) &&
            sequence >= batch.SourceBatch.Sequence)
        {
            return;
        }

        var deliveryId = DeliveryId(
            "snapshot-batch",
            pipeline.PipelineId,
            pipeline.Source.Fingerprint,
            epoch,
            table,
            batch.SourceBatch.Sequence.ToString(CultureInfo.InvariantCulture));
        var payload = KafkaSyncEnvelopeCodec.EncodeSnapshotBatch(
            deliveryId,
            batch,
            _options.MaxEnvelopeBytes);
        _ = await PublishOrderedAsync(
            pipeline,
            "snapshot.batch",
            deliveryId,
            payload,
            checkpoint,
            batch.SourceBatch.Sequence.ToString(CultureInfo.InvariantCulture),
            [],
            cancellationToken,
            CheckpointComparison.Decimal).ConfigureAwait(false);
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
        var checkpoint = $"snapshot:{epoch}:complete";
        var deliveryId = DeliveryId(
            "snapshot-complete",
            pipeline.PipelineId,
            pipeline.Source.Fingerprint,
            epoch);
        var payload = KafkaSyncEnvelopeCodec.EncodeSnapshotComplete(
            deliveryId,
            pipelineId,
            transform,
            complete,
            _options.MaxEnvelopeBytes);
        _ = await PublishOrderedAsync(
            pipeline,
            "snapshot.complete",
            deliveryId,
            payload,
            checkpoint,
            "1",
            [],
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

        var duplicate = await PublishTransactionAsync(pipeline, batch, cancellationToken)
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
        var duplicate = await PublishTransactionAsync(pipeline, batch, cancellationToken)
            .ConfigureAwait(false);
        return new SyncQuarantineReplayApplyResult(
            duplicate
                ? SyncQuarantineReplayApplyStatus.AlreadyApplied
                : SyncQuarantineReplayApplyStatus.Applied,
            batch.Transaction.CommitEndPosition);
    }

    private async ValueTask<bool> PublishTransactionAsync(
        ProvisionedPipeline pipeline,
        SyncTransactionBatch batch,
        CancellationToken cancellationToken)
    {
        var position = batch.Transaction.CommitEndPosition.Value;
        if (pipeline.Checkpoints.TryGetValue(PositionCheckpoint, out var current) &&
            ulong.TryParse(current, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var durable) &&
            durable >= position)
        {
            return true;
        }

        var deliveryId = DeliveryId(
            "transaction",
            pipeline.PipelineId,
            pipeline.Source.Fingerprint,
            position.ToString("x16", CultureInfo.InvariantCulture),
            batch.Transaction.TransactionId.ToString("x8", CultureInfo.InvariantCulture));
        var payload = KafkaSyncEnvelopeCodec.EncodeTransaction(
            deliveryId,
            batch,
            _options.MaxEnvelopeBytes);
        return await PublishOrderedAsync(
            pipeline,
            "transaction",
            deliveryId,
            payload,
            PositionCheckpoint,
            position.ToString("x16", CultureInfo.InvariantCulture),
            [],
            cancellationToken,
            CheckpointComparison.Hexadecimal).ConfigureAwait(false);
    }

    private async ValueTask<bool> PublishOrderedAsync(
        ProvisionedPipeline pipeline,
        string eventName,
        string deliveryId,
        byte[] payload,
        string checkpointKey,
        string checkpointValue,
        IReadOnlyList<string> tombstoneKeys,
        CancellationToken cancellationToken,
        CheckpointComparison comparison = CheckpointComparison.Exact)
    {
        await pipeline.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (pipeline.Checkpoints.TryGetValue(checkpointKey, out var existing) &&
                IsAlreadyApplied(existing, checkpointValue, comparison))
            {
                return true;
            }

            try
            {
                await _transport.PublishAsync(
                    new KafkaSyncMessage(
                        eventName,
                        deliveryId,
                        pipeline.Transform.Fingerprint,
                        payload,
                        checkpointKey,
                        checkpointValue,
                        tombstoneKeys),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (KafkaSyncDeliveryException)
            {
                Volatile.Write(ref _pipeline, null);
                throw;
            }

            foreach (var key in tombstoneKeys)
            {
                _ = pipeline.Checkpoints.Remove(key);
            }

            pipeline.Checkpoints[checkpointKey] = checkpointValue;
            return false;
        }
        finally
        {
            _ = pipeline.Gate.Release();
        }
    }

    private static bool IsAlreadyApplied(
        string existing,
        string proposed,
        CheckpointComparison comparison) => comparison switch
        {
            CheckpointComparison.Exact => string.Equals(existing, proposed, StringComparison.Ordinal),
            CheckpointComparison.Decimal =>
                long.TryParse(existing, NumberStyles.None, CultureInfo.InvariantCulture, out var current) &&
                long.TryParse(proposed, NumberStyles.None, CultureInfo.InvariantCulture, out var next) &&
                current >= next,
            CheckpointComparison.Hexadecimal =>
                ulong.TryParse(existing, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var current) &&
                ulong.TryParse(proposed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var next) &&
                current >= next,
            _ => throw new ArgumentOutOfRangeException(nameof(comparison)),
        };

    private ProvisionedPipeline RequirePipeline(
        string pipelineId,
        ChangeSourceIdentity source,
        SyncTransformVersion? transform = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        var pipeline = Volatile.Read(ref _pipeline) ??
            throw new InvalidOperationException(
                "The Kafka destination must be provisioned before it can receive data.");
        if (!string.Equals(pipeline.PipelineId, pipelineId, StringComparison.Ordinal) ||
            pipeline.Source != source)
        {
            throw new InvalidOperationException(
                "The Kafka destination was provisioned for a different pipeline or PostgreSQL source.");
        }

        if (transform is not null &&
            !string.Equals(
                pipeline.Transform.Fingerprint,
                transform.Fingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Kafka destination transform changed and requires an explicit rebuild.");
        }

        return pipeline;
    }

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

    public ValueTask DisposeAsync() => _transport.DisposeAsync();

    private sealed record ProvisionedPipeline(
        string PipelineId,
        ChangeSourceIdentity Source,
        SyncTransformVersion Transform,
        Dictionary<string, string> Checkpoints)
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private enum CheckpointComparison
    {
        Exact,
        Decimal,
        Hexadecimal,
    }
}
