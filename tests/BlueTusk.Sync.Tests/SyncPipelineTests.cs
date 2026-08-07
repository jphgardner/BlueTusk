using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;

namespace BlueTusk.Sync.Tests;

public sealed class SyncPipelineTests
{
    private static readonly ChangeSourceIdentity Source =
        new("sync-system", "sync-db", "sync-slot", "public:orders");

    [Fact]
    public void Transform_fingerprint_is_stable_and_configuration_sensitive()
    {
        var first = SyncTransformVersion.Create("orders", "v1", "tenant=a"u8);
        var same = SyncTransformVersion.Create("orders", "v1", "tenant=a"u8);
        var changed = SyncTransformVersion.Create("orders", "v1", "tenant=b"u8);

        Assert.Equal(first, same);
        Assert.NotEqual(first, changed);
        Assert.Equal(64, first.Fingerprint.Length);
        Assert.Equal(
            "b50232fa0a892240b3c9f246728508f486a0ce08bda135fa8e10becf4369e1e0",
            first.Fingerprint);
    }

    [Fact]
    public async Task Pipeline_acknowledges_only_after_exact_durable_position()
    {
        var events = new List<string>();
        var observer = new RecordingObserver(events);
        var destination = new RecordingDestination(events);
        await using var pipeline = CreatePipeline(destination);
        await pipeline.ProvisionAsync();
        await using var delivery = CreateDelivery(observer);

        await pipeline.ConsumeTransactionAsync(delivery);

        Assert.Equal(["destination", "ack"], events);
        Assert.Equal(ChangeDeliveryState.Acknowledged, delivery.State);
        Assert.Equal(SyncPipelineState.Running, pipeline.Status.State);
        Assert.Equal(1, pipeline.Status.AppliedTransactions);
    }

    [Fact]
    public async Task Explicit_transient_failures_retry_in_order_before_acknowledgement()
    {
        var events = new List<string>();
        var destination = new RecordingDestination(events)
        {
            FailuresRemaining = 2,
        };
        await using var pipeline = new SyncPipeline(
            new SyncPipelineOptions
            {
                PipelineId = "orders",
                Retry = new SyncRetryOptions
                {
                    MaximumAttempts = 3,
                    InitialDelay = TimeSpan.Zero,
                    MaximumDelay = TimeSpan.Zero,
                    JitterRatio = 0,
                },
            },
            Source,
            new RecordingTransform(),
            destination,
            retryClassifier: new InvalidOperationRetryClassifier());
        await pipeline.ProvisionAsync();
        await using var delivery = CreateDelivery(new RecordingObserver(events));

        await pipeline.ConsumeTransactionAsync(delivery);

        Assert.Equal(["destination", "destination", "destination", "ack"], events);
        Assert.Equal(2, pipeline.Status.RetryAttempts);
        Assert.Equal(ChangeDeliveryState.Acknowledged, delivery.State);
    }

    [Fact]
    public async Task Unclassified_failure_is_not_retried_and_nacks_once()
    {
        var events = new List<string>();
        var destination = new RecordingDestination(events)
        {
            FailuresRemaining = 1,
        };
        await using var pipeline = CreatePipeline(destination);
        await pipeline.ProvisionAsync();
        await using var delivery = CreateDelivery(new RecordingObserver(events));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pipeline.ConsumeTransactionAsync(delivery).AsTask());

        Assert.Equal(["destination", "nack"], events);
        Assert.Equal(0, pipeline.Status.RetryAttempts);
    }

    [Fact]
    public async Task Retry_exhaustion_nacks_without_acknowledging_past_destination_durability()
    {
        var events = new List<string>();
        var destination = new RecordingDestination(events)
        {
            FailuresRemaining = 3,
        };
        await using var pipeline = new SyncPipeline(
            new SyncPipelineOptions
            {
                PipelineId = "orders",
                Retry = new SyncRetryOptions
                {
                    MaximumAttempts = 3,
                    InitialDelay = TimeSpan.Zero,
                    MaximumDelay = TimeSpan.Zero,
                    JitterRatio = 0,
                },
            },
            Source,
            new RecordingTransform(),
            destination,
            retryClassifier: new InvalidOperationRetryClassifier());
        await pipeline.ProvisionAsync();
        await using var delivery = CreateDelivery(new RecordingObserver(events));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pipeline.ConsumeTransactionAsync(delivery).AsTask());

        Assert.Equal(["destination", "destination", "destination", "nack"], events);
        Assert.Equal(2, pipeline.Status.RetryAttempts);
        Assert.Equal(ChangeDeliveryState.Nacked, delivery.State);
    }

    [Fact]
    public async Task Sequential_rate_limit_applies_source_backpressure_without_reordering()
    {
        var events = new List<string>();
        await using var pipeline = new SyncPipeline(
            new SyncPipelineOptions
            {
                PipelineId = "orders",
                RateLimit = new SyncRateLimitOptions
                {
                    MaximumTransactionsPerSecond = 5,
                },
            },
            Source,
            new RecordingTransform(),
            new RecordingDestination(events));
        await pipeline.ProvisionAsync();
        await using var first = CreateDelivery(new RecordingObserver(events), 105);
        await using var second = CreateDelivery(new RecordingObserver(events), 106);

        var started = System.Diagnostics.Stopwatch.StartNew();
        await pipeline.ConsumeTransactionAsync(first);
        await pipeline.ConsumeTransactionAsync(second);

        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(150));
        Assert.Equal(["destination", "ack", "destination", "ack"], events);
        Assert.True(pipeline.Status.ThrottleDelay >= TimeSpan.FromMilliseconds(150));
    }

    [Fact]
    public async Task Wrong_durable_position_nacks_and_faults_without_advancing()
    {
        var events = new List<string>();
        var observer = new RecordingObserver(events);
        var destination = new RecordingDestination(events)
        {
            PositionOffset = 1,
        };
        await using var pipeline = CreatePipeline(destination);
        await pipeline.ProvisionAsync();
        await using var delivery = CreateDelivery(observer);

        await Assert.ThrowsAsync<SyncDestinationDurabilityException>(
            () => pipeline.ConsumeTransactionAsync(delivery).AsTask());

        Assert.Equal(["destination", "nack"], events);
        Assert.Equal(ChangeDeliveryState.Nacked, delivery.State);
        Assert.Equal(SyncPipelineState.Faulted, pipeline.Status.State);
        Assert.Equal(0, pipeline.Status.AppliedTransactions);
    }

    [Fact]
    public async Task Transform_change_requires_explicit_rebuild()
    {
        var destination = new RecordingDestination([])
        {
            ProvisionResult = new SyncProvisionResult(
                SyncProvisionStatus.RebuildRequired,
                new string('a', 64)),
        };
        await using var pipeline = CreatePipeline(destination);

        await Assert.ThrowsAsync<SyncTransformVersionMismatchException>(
            () => pipeline.ProvisionAsync().AsTask());

        Assert.Equal(SyncPipelineState.Rebuilding, pipeline.Status.State);
    }

    [Fact]
    public async Task Poison_record_pauses_and_nacks_by_default()
    {
        var events = new List<string>();
        var transform = new RecordingTransform { Poison = true };
        await using var pipeline = CreatePipeline(new RecordingDestination(events), transform);
        await pipeline.ProvisionAsync();
        await using var delivery = CreateDelivery(new RecordingObserver(events));

        await pipeline.ConsumeTransactionAsync(delivery);

        Assert.Equal(["nack"], events);
        Assert.Equal(SyncPipelineState.Paused, pipeline.Status.State);
        Assert.Equal(ChangeDeliveryState.Nacked, delivery.State);
    }

    [Fact]
    public async Task Quarantine_policy_advances_only_after_durable_quarantine()
    {
        var events = new List<string>();
        var quarantine = new RecordingQuarantine(events);
        await using var pipeline = new SyncPipeline(
            new SyncPipelineOptions
            {
                PipelineId = "orders",
                PoisonRecordPolicy = SyncPoisonRecordPolicy.QuarantineAndAdvance,
            },
            Source,
            new RecordingTransform { Poison = true },
            new RecordingDestination(events),
            quarantine);
        await pipeline.ProvisionAsync();
        await using var delivery = CreateDelivery(new RecordingObserver(events));

        await pipeline.ConsumeTransactionAsync(delivery);

        Assert.Equal(["quarantine", "ack"], events);
        Assert.Equal(1, pipeline.Status.QuarantinedTransactions);
        Assert.Equal(SyncPipelineState.Running, pipeline.Status.State);
    }

    [Fact]
    public async Task Quarantine_and_pause_stops_at_the_acknowledged_replay_boundary()
    {
        var events = new List<string>();
        var quarantine = new RecordingQuarantine(events);
        await using var pipeline = new SyncPipeline(
            new SyncPipelineOptions
            {
                PipelineId = "orders",
                PoisonRecordPolicy = SyncPoisonRecordPolicy.QuarantineAndPause,
            },
            Source,
            new RecordingTransform { Poison = true },
            new RecordingDestination(events),
            quarantine);
        await pipeline.ProvisionAsync();
        await using var delivery = CreateDelivery(new RecordingObserver(events));

        await pipeline.ConsumeTransactionAsync(delivery);

        Assert.Equal(["quarantine", "ack"], events);
        Assert.Equal(ChangeDeliveryState.Acknowledged, delivery.State);
        Assert.Equal(SyncPipelineState.Paused, pipeline.Status.State);
    }

    private static SyncPipeline CreatePipeline(
        RecordingDestination destination,
        RecordingTransform? transform = null) =>
        new(
            new SyncPipelineOptions { PipelineId = "orders" },
            Source,
            transform ?? new RecordingTransform(),
            destination);

    private static ChangeTransactionDelivery CreateDelivery(
        IChangeDeliveryObserver observer,
        ulong position = 105) =>
        ChangeDeliveryTestFactory.CreateCommitted(
            Source,
            transactionId: 42,
            new BlueTuskLogSequenceNumber(position),
            observer: observer);

    private sealed class RecordingTransform : ISyncTransform
    {
        public bool Poison { get; init; }

        public SyncTransformVersion Version { get; } =
            SyncTransformVersion.Create("orders", "v1");

        public ValueTask<IReadOnlyList<SyncMutation>> TransformTransactionAsync(
            ChangeTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Poison)
            {
                throw new SyncPoisonRecordException("Invalid destination document.");
            }

            return ValueTask.FromResult<IReadOnlyList<SyncMutation>>([]);
        }

        public ValueTask<IReadOnlyList<SyncSnapshotMutation>> TransformSnapshotBatchAsync(
            ChangeSnapshotBatch batch,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<SyncSnapshotMutation>>([]);
    }

    private sealed class RecordingDestination(List<string> events) : ISyncDestination
    {
        public long PositionOffset { get; init; }

        public SyncProvisionResult ProvisionResult { get; init; } =
            new(SyncProvisionStatus.Ready);

        public int FailuresRemaining { get; set; }

        public string Name => "recording";

        public SyncDestinationCapabilities Capabilities =>
            SyncDestinationCapabilities.TransactionalBatches |
            SyncDestinationCapabilities.IdempotentUpserts |
            SyncDestinationCapabilities.Deletes;

        public ValueTask<SyncProvisionResult> ProvisionAsync(
            SyncProvisionRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ProvisionResult);

        public ValueTask ResetSnapshotAsync(
            string pipelineId,
            SnapshotReset reset,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StartSnapshotAsync(
            string pipelineId,
            SnapshotStart start,
            SyncTransformVersion transform,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask ApplySnapshotBatchAsync(
            SyncSnapshotBatch batch,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask CompleteSnapshotAsync(
            string pipelineId,
            SnapshotComplete complete,
            SyncTransformVersion transform,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<SyncApplyResult> ApplyTransactionAsync(
            SyncTransactionBatch batch,
            CancellationToken cancellationToken = default)
        {
            events.Add("destination");
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("Injected transient destination outage.");
            }

            return ValueTask.FromResult(SyncApplyResult.Applied(
                new BlueTuskLogSequenceNumber(
                    checked((ulong)((long)batch.Transaction.CommitEndPosition.Value + PositionOffset)))));
        }
    }

    private sealed class InvalidOperationRetryClassifier : ISyncRetryClassifier
    {
        public bool IsTransient(SyncRetryContext context) =>
            context.Exception is InvalidOperationException;
    }

    private sealed class RecordingObserver(List<string> events) : IChangeDeliveryObserver
    {
        public ValueTask AcknowledgeAsync(
            ChangeTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            events.Add("ack");
            return ValueTask.CompletedTask;
        }

        public ValueTask NackAsync(
            ChangeTransaction transaction,
            Exception? failure,
            CancellationToken cancellationToken = default)
        {
            events.Add("nack");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingQuarantine(List<string> events) : ISyncQuarantineSink
    {
        public ValueTask<bool> StoreAsync(
            SyncQuarantineRecord record,
            CancellationToken cancellationToken = default)
        {
            events.Add("quarantine");
            return ValueTask.FromResult(true);
        }
    }

}
