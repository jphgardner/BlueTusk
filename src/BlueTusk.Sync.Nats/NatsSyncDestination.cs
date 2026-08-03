using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BlueTusk.Streams;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace BlueTusk.Sync.Nats;

public sealed class NatsSyncDestination : ISyncDestination, ISyncQuarantineReplayDestination
{
    private const string ProductMetadataKey = "bluetusk.product";
    private const string FormatMetadataKey = "bluetusk.envelope-format";
    private const string PipelineMetadataKey = "bluetusk.pipeline";
    private const string SourceMetadataKey = "bluetusk.source";
    private const string TransformMetadataKey = "bluetusk.transform";
    private const string SubjectMetadataKey = "bluetusk.subject";

    private readonly NatsSyncOptions _options;
    private ProvisionedPipeline? _pipeline;

    public NatsSyncDestination(NatsSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public string Name => "NATS JetStream";

    public SyncDestinationCapabilities Capabilities =>
        SyncDestinationCapabilities.TransactionalBatches |
        SyncDestinationCapabilities.IdempotentUpserts |
        SyncDestinationCapabilities.Deletes;

    public async ValueTask<SyncProvisionResult> ProvisionAsync(
        SyncProvisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var expected = BuildStreamConfig(request);
        var stream = await GetOrCreateStreamAsync(expected, cancellationToken).ConfigureAwait(false);
        var result = ValidateStream(stream.Info.Config, request);
        if (result.Status == SyncProvisionStatus.Ready)
        {
            Volatile.Write(
                ref _pipeline,
                new ProvisionedPipeline(
                    request.PipelineId,
                    request.Source,
                    request.Transform));
        }

        return result;
    }

    private async ValueTask<INatsJSStream> GetOrCreateStreamAsync(
        StreamConfig expected,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _options.JetStream.GetStreamAsync(
                _options.StreamName,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (NatsJSApiException exception) when (
            _options.CreateStream && exception.Error.ErrCode == 10059)
        {
            try
            {
                return await _options.JetStream.CreateStreamAsync(expected, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (NatsJSApiException createException) when (createException.Error.ErrCode == 10058)
            {
                return await _options.JetStream.GetStreamAsync(
                    _options.StreamName,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask ResetSnapshotAsync(
        string pipelineId,
        SnapshotReset reset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reset);
        var pipeline = RequirePipeline(pipelineId, reset.Epoch.Source);
        var payload = NatsSyncEnvelopeCodec.EncodeSnapshotReset(
            pipelineId,
            pipeline.Transform,
            reset);
        await PublishAsync(
            "snapshot.reset",
            payload,
            MessageId("snapshot-reset", pipeline, reset.Epoch.Value.ToString("N")),
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
        var payload = NatsSyncEnvelopeCodec.EncodeSnapshotStart(pipelineId, transform, start);
        await PublishAsync(
            "snapshot.start",
            payload,
            MessageId("snapshot-start", pipeline, start.Epoch.Value.ToString("N")),
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
        var payload = NatsSyncEnvelopeCodec.EncodeSnapshotBatch(batch);
        await PublishAsync(
            "snapshot.batch",
            payload,
            MessageId(
                "snapshot-batch",
                pipeline,
                batch.SourceBatch.Epoch.Value.ToString("N"),
                batch.SourceBatch.Table.Schema,
                batch.SourceBatch.Table.Name,
                batch.SourceBatch.Sequence.ToString(CultureInfo.InvariantCulture)),
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
        var payload = NatsSyncEnvelopeCodec.EncodeSnapshotComplete(
            pipelineId,
            transform,
            complete);
        await PublishAsync(
            "snapshot.complete",
            payload,
            MessageId("snapshot-complete", pipeline, complete.Epoch.Value.ToString("N")),
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
        await pipeline.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var payload = NatsSyncEnvelopeCodec.EncodeTransaction(batch);
            return await PublishAsync(
                "transaction",
                payload,
                MessageId(
                    "transaction",
                    pipeline,
                    batch.Transaction.CommitEndPosition.Value.ToString("x16", CultureInfo.InvariantCulture),
                    batch.Transaction.TransactionId.ToString("x8", CultureInfo.InvariantCulture)),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = pipeline.Gate.Release();
        }
    }

    private StreamConfig BuildStreamConfig(SyncProvisionRequest request) =>
        new(_options.StreamName, [_options.StreamSubject])
        {
            Description = $"BlueTusk Sync pipeline {request.PipelineId}",
            MaxConsumers = -1,
            MaxMsgs = -1,
            MaxBytes = _options.MaxBytes,
            MaxAge = _options.MaxAge,
            MaxMsgSize = _options.MaxMessageBytes,
            Storage = StreamConfigStorage.File,
            NumReplicas = _options.Replicas,
            NoAck = false,
            Discard = StreamConfigDiscard.Old,
            DuplicateWindow = _options.DuplicateWindow,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ProductMetadataKey] = "sync",
                [FormatMetadataKey] = NatsSyncEnvelopeCodec.CurrentFormatVersion.ToString(
                    CultureInfo.InvariantCulture),
                [PipelineMetadataKey] = Fingerprint(request.PipelineId),
                [SourceMetadataKey] = request.Source.Fingerprint,
                [TransformMetadataKey] = request.Transform.Fingerprint,
                [SubjectMetadataKey] = _options.SubjectPrefix,
            },
        };

    private SyncProvisionResult ValidateStream(
        StreamConfig actual,
        SyncProvisionRequest request)
    {
        var metadata = actual.Metadata ??
            throw ConfigurationError("does not contain BlueTusk ownership metadata");
        RequireMetadata(metadata, ProductMetadataKey, "sync");
        RequireMetadata(
            metadata,
            FormatMetadataKey,
            NatsSyncEnvelopeCodec.CurrentFormatVersion.ToString(CultureInfo.InvariantCulture));
        RequireMetadata(metadata, PipelineMetadataKey, Fingerprint(request.PipelineId));
        RequireMetadata(metadata, SourceMetadataKey, request.Source.Fingerprint);
        RequireMetadata(metadata, SubjectMetadataKey, _options.SubjectPrefix);

        if (!metadata.TryGetValue(TransformMetadataKey, out var existingTransform))
        {
            throw ConfigurationError($"does not contain metadata key '{TransformMetadataKey}'");
        }

        if (!string.Equals(existingTransform, request.Transform.Fingerprint, StringComparison.Ordinal))
        {
            return new SyncProvisionResult(
                SyncProvisionStatus.RebuildRequired,
                existingTransform);
        }

        if (actual.Subjects is null ||
            actual.Subjects.Count != 1 ||
            !actual.Subjects.Contains(_options.StreamSubject, StringComparer.Ordinal) ||
            actual.NoAck ||
            actual.Storage != StreamConfigStorage.File ||
            actual.MaxBytes != _options.MaxBytes ||
            actual.MaxAge != _options.MaxAge ||
            actual.MaxMsgSize != _options.MaxMessageBytes ||
            actual.NumReplicas != _options.Replicas ||
            actual.DuplicateWindow != _options.DuplicateWindow)
        {
            throw ConfigurationError(
                "configuration drifted from the requested subjects, storage, retention, message size, replicas, or duplicate window");
        }

        return new SyncProvisionResult(SyncProvisionStatus.Ready);
    }

    private static void RequireMetadata(
        IDictionary<string, string> metadata,
        string key,
        string expected)
    {
        if (!metadata.TryGetValue(key, out var actual) ||
            !string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new NatsSyncStreamConfigurationException(
                $"The NATS JetStream stream metadata key '{key}' does not match the configured BlueTusk Sync pipeline.");
        }
    }

    private NatsSyncStreamConfigurationException ConfigurationError(string detail) =>
        new($"NATS JetStream stream '{_options.StreamName}' {detail}. Use a new stream generation or restore the expected configuration explicitly.");

    private ProvisionedPipeline RequirePipeline(
        string pipelineId,
        ChangeSourceIdentity source,
        SyncTransformVersion? transform = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        ArgumentNullException.ThrowIfNull(source);
        var pipeline = Volatile.Read(ref _pipeline) ??
            throw new NatsSyncException(
                "The NATS Sync destination must be provisioned successfully before publishing.");
        if (!string.Equals(pipelineId, pipeline.PipelineId, StringComparison.Ordinal))
        {
            throw new NatsSyncException(
                $"The destination belongs to pipeline '{pipeline.PipelineId}', not '{pipelineId}'.");
        }

        if (!string.Equals(source.Fingerprint, pipeline.Source.Fingerprint, StringComparison.Ordinal))
        {
            throw new NatsSyncException(
                $"Pipeline '{pipelineId}' belongs to source '{pipeline.Source.Fingerprint}', not '{source.Fingerprint}'.");
        }

        if (transform is not null &&
            !string.Equals(
                transform.Fingerprint,
                pipeline.Transform.Fingerprint,
                StringComparison.Ordinal))
        {
            throw new SyncTransformVersionMismatchException(
                pipeline.Transform.Fingerprint,
                transform.Fingerprint);
        }

        return pipeline;
    }

    private async ValueTask<bool> PublishAsync(
        string suffix,
        byte[] payload,
        string messageId,
        CancellationToken cancellationToken)
    {
        if (payload.Length > _options.MaxMessageBytes)
        {
            throw new NatsSyncEnvelopeException(
                $"The {payload.Length}-byte NATS Sync envelope exceeds the configured {_options.MaxMessageBytes}-byte limit.");
        }

        var acknowledgement = await _options.JetStream.PublishAsync(
            _options.SubjectPrefix + "." + suffix,
            payload,
            opts: new NatsJSPubOpts
            {
                MsgId = messageId,
                RetryAttempts = _options.PublishRetryAttempts,
                RetryWaitBetweenAttempts = _options.PublishRetryDelay,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (acknowledgement.Duplicate)
        {
            return true;
        }

        acknowledgement.EnsureSuccess();
        return false;
    }

    private static string MessageId(
        string kind,
        ProvisionedPipeline pipeline,
        params string[] identity)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "bluetusk-sync-nats-v1");
        Append(hash, kind);
        Append(hash, pipeline.PipelineId);
        Append(hash, pipeline.Source.Fingerprint);
        Append(hash, pipeline.Transform.Fingerprint);
        foreach (var value in identity)
        {
            Append(hash, value);
        }

        return "bt-" + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string Fingerprint(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

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
