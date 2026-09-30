using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using BlueTusk.Streams;

namespace BlueTusk.Sync.Webhooks;

public sealed class WebhookSyncDestination : ISyncDestination, ISyncQuarantineReplayDestination
{
    private readonly WebhookSyncOptions _options;
    private readonly byte[] _signingKey;
    private ProvisionedPipeline? _pipeline;

    public WebhookSyncDestination(WebhookSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _signingKey = options.SigningKey.ToArray();
    }

    public string Name => "Signed webhook";

    public SyncDestinationCapabilities Capabilities =>
        SyncDestinationCapabilities.TransactionalBatches |
        SyncDestinationCapabilities.IdempotentUpserts |
        SyncDestinationCapabilities.Deletes;

    public async ValueTask<SyncProvisionResult> ProvisionAsync(
        SyncProvisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var deliveryId = DeliveryId(
            "provision",
            request.PipelineId,
            request.Source.Fingerprint,
            request.Transform.Fingerprint);
        var payload = WebhookSyncEnvelopeCodec.EncodeProvision(
            deliveryId,
            request,
            _options.MaxEnvelopeBytes);
        var acknowledgement = await SendAsync(
            "provision",
            deliveryId,
            payload,
            cancellationToken).ConfigureAwait(false);
        var currentTransform = acknowledgement.TransformFingerprint ??
            throw new WebhookSyncProtocolException(
                $"Webhook receiver '{_options.Endpoint.Host}' did not return the required {WebhookSyncProtocol.TransformFingerprintHeader} provisioning header.");
        if (!string.Equals(currentTransform, request.Transform.Fingerprint, StringComparison.Ordinal))
        {
            return new SyncProvisionResult(SyncProvisionStatus.RebuildRequired, currentTransform);
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
        var deliveryId = DeliveryId(
            "snapshot-reset",
            pipeline.PipelineId,
            pipeline.Source.Fingerprint,
            reset.Epoch.Value.ToString("N"));
        var payload = WebhookSyncEnvelopeCodec.EncodeSnapshotReset(
            deliveryId,
            pipelineId,
            pipeline.Transform,
            reset,
            _options.MaxEnvelopeBytes);
        _ = await SendOrderedAsync(
            pipeline,
            "snapshot.reset",
            deliveryId,
            payload,
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
        var deliveryId = DeliveryId(
            "snapshot-start",
            pipeline.PipelineId,
            pipeline.Source.Fingerprint,
            start.Epoch.Value.ToString("N"));
        var payload = WebhookSyncEnvelopeCodec.EncodeSnapshotStart(
            deliveryId,
            pipelineId,
            transform,
            start,
            _options.MaxEnvelopeBytes);
        _ = await SendOrderedAsync(
            pipeline,
            "snapshot.start",
            deliveryId,
            payload,
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
        var deliveryId = DeliveryId(
            "snapshot-batch",
            pipeline.PipelineId,
            pipeline.Source.Fingerprint,
            batch.SourceBatch.Epoch.Value.ToString("N"),
            batch.SourceBatch.Table.Schema,
            batch.SourceBatch.Table.Name,
            batch.SourceBatch.Sequence.ToString(CultureInfo.InvariantCulture));
        var payload = WebhookSyncEnvelopeCodec.EncodeSnapshotBatch(
            deliveryId,
            batch,
            _options.MaxEnvelopeBytes);
        _ = await SendOrderedAsync(
            pipeline,
            "snapshot.batch",
            deliveryId,
            payload,
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
        var deliveryId = DeliveryId(
            "snapshot-complete",
            pipeline.PipelineId,
            pipeline.Source.Fingerprint,
            complete.Epoch.Value.ToString("N"));
        var payload = WebhookSyncEnvelopeCodec.EncodeSnapshotComplete(
            deliveryId,
            pipelineId,
            transform,
            complete,
            _options.MaxEnvelopeBytes);
        _ = await SendOrderedAsync(
            pipeline,
            "snapshot.complete",
            deliveryId,
            payload,
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

        var acknowledgement = await SendTransactionAsync(
            pipeline,
            batch,
            cancellationToken).ConfigureAwait(false);
        return acknowledgement.Duplicate
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
        var acknowledgement = await SendTransactionAsync(
            pipeline,
            batch,
            cancellationToken).ConfigureAwait(false);
        return new SyncQuarantineReplayApplyResult(
            acknowledgement.Duplicate
                ? SyncQuarantineReplayApplyStatus.AlreadyApplied
                : SyncQuarantineReplayApplyStatus.Applied,
            batch.Transaction.CommitEndPosition);
    }

    private async ValueTask<DeliveryAcknowledgement> SendTransactionAsync(
        ProvisionedPipeline pipeline,
        SyncTransactionBatch batch,
        CancellationToken cancellationToken)
    {
        var deliveryId = DeliveryId(
            "transaction",
            pipeline.PipelineId,
            pipeline.Source.Fingerprint,
            batch.Transaction.CommitEndPosition.Value.ToString("x16", CultureInfo.InvariantCulture),
            batch.Transaction.TransactionId.ToString("x8", CultureInfo.InvariantCulture));
        var payload = WebhookSyncEnvelopeCodec.EncodeTransaction(
            deliveryId,
            batch,
            _options.MaxEnvelopeBytes);
        return await SendOrderedAsync(
            pipeline,
            "transaction",
            deliveryId,
            payload,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<DeliveryAcknowledgement> SendOrderedAsync(
        ProvisionedPipeline pipeline,
        string eventName,
        string deliveryId,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        await pipeline.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SendAsync(eventName, deliveryId, payload, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _ = pipeline.Gate.Release();
        }
    }

    private async ValueTask<DeliveryAcknowledgement> SendAsync(
        string eventName,
        string deliveryId,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var timestamp = _options.TimeProvider.GetUtcNow().ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);
        var signature = Sign(timestamp, payload);
        var delay = _options.InitialRetryDelay;
        for (var attempt = 1; attempt <= _options.MaximumAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
            {
                Content = new ByteArrayContent(payload),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.TryAddWithoutValidation(WebhookSyncProtocol.EventHeader, eventName);
            request.Headers.TryAddWithoutValidation(WebhookSyncProtocol.DeliveryIdHeader, deliveryId);
            request.Headers.TryAddWithoutValidation(WebhookSyncProtocol.TimestampHeader, timestamp);
            request.Headers.TryAddWithoutValidation(WebhookSyncProtocol.SignatureHeader, signature);
            request.Headers.TryAddWithoutValidation(WebhookSyncProtocol.KeyIdHeader, _options.KeyId);

            try
            {
                using var response = await _options.Client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return ReadAcknowledgement(response);
                }

                if (!IsTransient(response.StatusCode) || attempt == _options.MaximumAttempts)
                {
                    throw new WebhookSyncDeliveryException(
                        $"Webhook receiver '{_options.Endpoint.Host}' returned HTTP {(int)response.StatusCode}; the Sync checkpoint was not advanced.");
                }
            }
            catch (HttpRequestException exception) when (attempt < _options.MaximumAttempts)
            {
                _ = exception;
            }
            catch (HttpRequestException exception)
            {
                throw new WebhookSyncDeliveryException(
                    $"Webhook receiver '{_options.Endpoint.Host}' could not durably acknowledge the delivery; the Sync checkpoint was not advanced.",
                    exception);
            }

            await Task.Delay(delay, _options.TimeProvider, cancellationToken).ConfigureAwait(false);
            var nextTicks = Math.Min(
                checked(delay.Ticks * 2),
                _options.MaximumRetryDelay.Ticks);
            delay = TimeSpan.FromTicks(nextTicks);
        }

        throw new UnreachableException();
    }

    private static DeliveryAcknowledgement ReadAcknowledgement(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(
                WebhookSyncProtocol.DeliveryStatusHeader,
                out var statusValues))
        {
            throw new WebhookSyncProtocolException(
                $"A successful webhook response did not contain {WebhookSyncProtocol.DeliveryStatusHeader}; ambiguous success is never checkpointed.");
        }

        var status = statusValues.SingleOrDefault();
        var duplicate = status switch
        {
            WebhookSyncProtocol.AppliedStatus => false,
            WebhookSyncProtocol.DuplicateStatus => true,
            _ => throw new WebhookSyncProtocolException(
                $"The webhook receiver returned unsupported delivery status '{status}'."),
        };
        var transform = response.Headers.TryGetValues(
            WebhookSyncProtocol.TransformFingerprintHeader,
            out var transformValues)
            ? transformValues.SingleOrDefault()
            : null;
        return new DeliveryAcknowledgement(duplicate, transform);
    }

    private string Sign(string timestamp, ReadOnlySpan<byte> payload)
    {
        using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, _signingKey);
        hash.AppendData(Encoding.ASCII.GetBytes(timestamp));
        hash.AppendData("."u8);
        hash.AppendData(payload);
        return "v1=" + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private ProvisionedPipeline RequirePipeline(
        string pipelineId,
        ChangeSourceIdentity source,
        SyncTransformVersion? transform = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        ArgumentNullException.ThrowIfNull(source);
        var pipeline = Volatile.Read(ref _pipeline) ??
            throw new WebhookSyncException(
                "The webhook Sync destination must be provisioned successfully before delivery.");
        if (!string.Equals(pipeline.PipelineId, pipelineId, StringComparison.Ordinal))
        {
            throw new WebhookSyncException(
                $"The destination belongs to pipeline '{pipeline.PipelineId}', not '{pipelineId}'.");
        }

        if (!string.Equals(pipeline.Source.Fingerprint, source.Fingerprint, StringComparison.Ordinal))
        {
            throw new WebhookSyncException(
                $"Pipeline '{pipelineId}' belongs to a different source identity.");
        }

        if (transform is not null && !string.Equals(
                pipeline.Transform.Fingerprint,
                transform.Fingerprint,
                StringComparison.Ordinal))
        {
            throw new SyncTransformVersionMismatchException(
                pipeline.Transform.Fingerprint,
                transform.Fingerprint);
        }

        return pipeline;
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode == 425 ||
        (int)statusCode >= 500;

    private static string DeliveryId(string kind, params string[] identity)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "bluetusk-sync-webhook-v1");
        Append(hash, kind);
        foreach (var value in identity)
        {
            Append(hash, value);
        }

        return "btw-" + Convert.ToHexStringLower(hash.GetHashAndReset());
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

    private readonly record struct DeliveryAcknowledgement(
        bool Duplicate,
        string? TransformFingerprint);
}
