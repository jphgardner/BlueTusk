using System.Runtime.CompilerServices;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;

namespace BlueTusk.Sync.Tests;

public sealed class SyncRebuildTests
{
    private static readonly ChangeSourceIdentity Source =
        new("rebuild-system", "rebuild-db", "rebuild-slot", "public:orders");

    [Fact]
    public async Task Coordinator_snapshots_catches_up_verifies_activates_and_retires()
    {
        var events = new List<string>();
        var observer = new RecordingObserver(events);
        var epoch = SnapshotEpoch.Create(Source, Lsn(100));
        var source = new RecordingSnapshotSource(
            [new RecordingSnapshotAttempt(
                epoch,
                [SnapshotBatch(epoch, 0), SnapshotBatch(epoch, 1)],
                [Delivery(41, 101, observer), Delivery(42, 105, observer)])]);
        var destination = new RecordingRebuildDestination(events);
        var progress = new RecordingProgress();
        var coordinator = CreateCoordinator(source, destination, progress, retire: true);

        var result = await coordinator.RunAsync();

        Assert.Equal(epoch.Value, result.SnapshotEpoch);
        Assert.Equal(Lsn(100), result.SnapshotPosition);
        Assert.Equal(Lsn(105), result.ActivatedPosition);
        Assert.Equal(2, result.SnapshotBatches);
        Assert.Equal(2, result.CatchUpTransactions);
        Assert.True(result.PreviousGenerationRetired);
        Assert.Equal(
            [
                "prepare",
                "reset",
                "start",
                "snapshot:0",
                "snapshot:1",
                "complete",
                "barrier:100",
                "apply:101",
                "ack:101",
                "apply:105",
                "ack:105",
                "verify",
                "authoritative-verify",
                "activate",
                "retire:active-v1",
                "release:105",
            ],
            events);
        Assert.Equal(SyncRebuildStage.Preparing, progress.Values[0].Stage);
        Assert.Equal(SyncRebuildStage.Completed, progress.Values[^1].Stage);
    }

    [Fact]
    public async Task Exporter_loss_abandons_epoch_and_restarts_full_snapshot()
    {
        var events = new List<string>();
        var abandoned = SnapshotEpoch.Create(Source, Lsn(100));
        var replacement = SnapshotEpoch.Create(Source, Lsn(105));
        var source = new RecordingSnapshotSource(
            [
                new RecordingSnapshotAttempt(abandoned, [], [], failSnapshot: true),
                new RecordingSnapshotAttempt(replacement, [SnapshotBatch(replacement, 0)], []),
            ]);
        var destination = new RecordingRebuildDestination(events);
        var coordinator = CreateCoordinator(source, destination);

        var result = await coordinator.RunAsync();

        Assert.Equal(replacement.Value, result.SnapshotEpoch);
        Assert.Equal([null, abandoned.Value], source.AbandonedEpochs);
        Assert.Equal(2, events.Count(static value => value == "reset"));
        Assert.Contains("activate", events);
    }

    [Fact]
    public async Task Wrong_durable_position_nacks_and_never_activates()
    {
        var events = new List<string>();
        var observer = new RecordingObserver(events);
        var epoch = SnapshotEpoch.Create(Source, Lsn(100));
        var source = new RecordingSnapshotSource(
            [new RecordingSnapshotAttempt(epoch, [], [Delivery(42, 105, observer)])]);
        var destination = new RecordingRebuildDestination(events) { PositionOffset = 1 };
        var coordinator = CreateCoordinator(source, destination);

        await Assert.ThrowsAsync<SyncDestinationDurabilityException>(
            () => coordinator.RunAsync());

        Assert.Contains("nack:105", events);
        Assert.Contains("release:105", events);
        Assert.DoesNotContain("verify", events);
        Assert.DoesNotContain("activate", events);
    }

    [Fact]
    public async Task Catch_up_rejects_a_transaction_past_the_exact_cutover_target()
    {
        var events = new List<string>();
        var observer = new RecordingObserver(events);
        var epoch = SnapshotEpoch.Create(Source, Lsn(100));
        var source = new RecordingSnapshotSource(
            [new RecordingSnapshotAttempt(epoch, [], [Delivery(42, 106, observer)])]);
        var destination = new RecordingRebuildDestination(events);
        var coordinator = CreateCoordinator(source, destination, cutoverTarget: Lsn(105));

        var exception = await Assert.ThrowsAsync<SyncRebuildException>(
            () => coordinator.RunAsync());

        Assert.Contains("exact transaction commit-end", exception.Message, StringComparison.Ordinal);
        Assert.Contains("nack:106", events);
        Assert.DoesNotContain("apply:106", events);
        Assert.DoesNotContain("activate", events);
        Assert.Contains("release:105", events);
    }

    [Fact]
    public async Task Verification_mismatch_preserves_inactive_generation()
    {
        var events = new List<string>();
        var epoch = SnapshotEpoch.Create(Source, Lsn(105));
        var source = new RecordingSnapshotSource(
            [new RecordingSnapshotAttempt(epoch, [], [])]);
        var destination = new RecordingRebuildDestination(events)
        {
            Verification = new SyncRebuildVerification(false, "orders differ"),
        };
        var coordinator = CreateCoordinator(source, destination);

        var exception = await Assert.ThrowsAsync<SyncRebuildVerificationException>(
            () => coordinator.RunAsync());

        Assert.Contains("orders differ", exception.Message, StringComparison.Ordinal);
        Assert.Contains("verify", events);
        Assert.DoesNotContain("activate", events);
        Assert.Contains("release:105", events);
    }

    [Fact]
    public async Task Authoritative_mismatch_preserves_inactive_generation()
    {
        var events = new List<string>();
        var epoch = SnapshotEpoch.Create(Source, Lsn(105));
        var source = new RecordingSnapshotSource(
            [new RecordingSnapshotAttempt(epoch, [], [])]);
        var destination = new RecordingRebuildDestination(events);
        var coordinator = CreateCoordinator(
            source,
            destination,
            authoritativeVerification: new(false, "authoritative orders differ"));

        var exception = await Assert.ThrowsAsync<SyncRebuildVerificationException>(
            () => coordinator.RunAsync());

        Assert.Contains("authoritative orders differ", exception.Message, StringComparison.Ordinal);
        Assert.Contains("verify", events);
        Assert.Contains("authoritative-verify", events);
        Assert.DoesNotContain("activate", events);
        Assert.Contains("release:105", events);
    }

    [Fact]
    public void Reconciliation_verifier_requires_exact_non_repairing_comparison()
    {
        var reader = new EmptyReconciliationReader();
        var countRequest = new SyncReconciliationRequest
        {
            PipelineId = "orders",
            Collection = "orders",
            Mode = SyncReconciliationMode.Count,
        };
        var repairingRequest = countRequest with
        {
            Mode = SyncReconciliationMode.PartitionedContentHash,
            Repair = true,
        };

        Assert.Throws<ArgumentException>(() => new SyncRebuildReconciliation(countRequest, reader));
        Assert.Throws<ArgumentException>(() => new SyncRebuildReconciliation(repairingRequest, reader));
    }

    [Fact]
    public void Coordinator_rejects_destination_without_atomic_generation_swap()
    {
        var destination = new RecordingRebuildDestination([])
        {
            CapabilitiesOverride = SyncDestinationCapabilities.IdempotentUpserts,
        };
        var source = new RecordingSnapshotSource([]);

        var exception = Assert.Throws<ArgumentException>(
            () => CreateCoordinator(source, destination));

        Assert.Contains("atomic routing swaps", exception.Message, StringComparison.Ordinal);
    }

    private static SyncRebuildCoordinator CreateCoordinator(
        IConsistentSnapshotSource source,
        ISyncDestination destination,
        IProgress<SyncRebuildProgress>? progress = null,
        bool retire = false,
        SyncRebuildVerification? authoritativeVerification = null,
        BlueTuskLogSequenceNumber? cutoverTarget = null) =>
        new(
            new SyncRebuildOptions
            {
                PipelineId = "orders",
                RetirePreviousGeneration = retire,
            },
            Source,
            source,
            new RecordingTransform(),
            destination,
            new RecordingCutoverBarrier(
                destination is RecordingRebuildDestination recording
                    ? recording.Events
                    : [],
                cutoverTarget ?? Lsn(105)),
            new RecordingVerifier(
                destination is RecordingRebuildDestination verifierRecording
                    ? verifierRecording.Events
                    : [],
                authoritativeVerification ?? new SyncRebuildVerification(true)),
            progress);

    private static ChangeSnapshotBatch SnapshotBatch(SnapshotEpoch epoch, long sequence) =>
        new(
            epoch,
            new ChangeTable(
                7,
                "public",
                "orders",
                'd',
                [new ChangeColumn(0, "id", 23, -1, true)]),
            sequence,
            [],
            true);

    private static ChangeTransactionDelivery Delivery(
        uint transactionId,
        ulong position,
        IChangeDeliveryObserver observer) =>
        ChangeDeliveryTestFactory.CreateCommitted(
            Source,
            transactionId,
            Lsn(position),
            observer: observer);

    private static BlueTuskLogSequenceNumber Lsn(ulong value) => new(value);

    private sealed class RecordingTransform : ISyncTransform
    {
        public SyncTransformVersion Version { get; } =
            SyncTransformVersion.Create("orders", "v2");

        public ValueTask<IReadOnlyList<SyncMutation>> TransformTransactionAsync(
            ChangeTransaction transaction,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<SyncMutation>>([]);

        public ValueTask<IReadOnlyList<SyncSnapshotMutation>> TransformSnapshotBatchAsync(
            ChangeSnapshotBatch batch,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<SyncSnapshotMutation>>([]);
    }

    private sealed class RecordingSnapshotSource(
        IEnumerable<RecordingSnapshotAttempt> attempts) : IConsistentSnapshotSource
    {
        private readonly Queue<RecordingSnapshotAttempt> _attempts = new(attempts);

        public List<Guid?> AbandonedEpochs { get; } = [];

        public ValueTask<IConsistentSnapshotAttempt> BeginAttemptAsync(
            Guid? abandonedEpoch,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AbandonedEpochs.Add(abandonedEpoch);
            return ValueTask.FromResult<IConsistentSnapshotAttempt>(_attempts.Dequeue());
        }
    }

    private sealed class RecordingSnapshotAttempt(
        SnapshotEpoch epoch,
        IReadOnlyList<ChangeSnapshotBatch> batches,
        IReadOnlyList<ChangeTransactionDelivery> deliveries,
        bool failSnapshot = false) : IConsistentSnapshotAttempt
    {
        public SnapshotEpoch Epoch => epoch;

        public IReadOnlyList<ChangeTable> Tables { get; } =
            batches.Select(static batch => batch.Table).Distinct().ToArray();

        public async IAsyncEnumerable<ChangeSnapshotBatch> ReadSnapshotAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (failSnapshot)
            {
                throw new SnapshotSessionLostException("exporter lost");
            }

            foreach (var batch in batches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return batch;
            }
        }

        public IChangeStream CreateChangeStream() => new RecordingChangeStream(deliveries);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingChangeStream(
        IReadOnlyList<ChangeTransactionDelivery> deliveries) : IChangeStream
    {
        public async IAsyncEnumerable<ChangeTransactionDelivery> ReadTransactionsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var delivery in deliveries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return delivery;
            }
        }
    }

    private sealed class RecordingRebuildDestination(List<string> events) :
        ISyncDestination,
        ISyncRebuildDestination
    {
        public List<string> Events => events;

        public long PositionOffset { get; init; }

        public SyncDestinationCapabilities? CapabilitiesOverride { get; init; }

        public SyncRebuildVerification Verification { get; init; } = new(true);

        public string Name => "recording-rebuild";

        public SyncDestinationCapabilities Capabilities =>
            CapabilitiesOverride ??
            (SyncDestinationCapabilities.IdempotentUpserts |
             SyncDestinationCapabilities.Deletes |
             SyncDestinationCapabilities.AliasSwap);

        public ValueTask<SyncProvisionResult> ProvisionAsync(
            SyncProvisionRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SyncProvisionResult(SyncProvisionStatus.Ready));

        public ValueTask ResetSnapshotAsync(
            string pipelineId,
            SnapshotReset reset,
            CancellationToken cancellationToken = default)
        {
            events.Add("reset");
            return ValueTask.CompletedTask;
        }

        public ValueTask StartSnapshotAsync(
            string pipelineId,
            SnapshotStart start,
            SyncTransformVersion transform,
            CancellationToken cancellationToken = default)
        {
            events.Add("start");
            return ValueTask.CompletedTask;
        }

        public ValueTask ApplySnapshotBatchAsync(
            SyncSnapshotBatch batch,
            CancellationToken cancellationToken = default)
        {
            events.Add("snapshot:" + batch.SourceBatch.Sequence);
            return ValueTask.CompletedTask;
        }

        public ValueTask CompleteSnapshotAsync(
            string pipelineId,
            SnapshotComplete complete,
            SyncTransformVersion transform,
            CancellationToken cancellationToken = default)
        {
            events.Add("complete");
            return ValueTask.CompletedTask;
        }

        public ValueTask<SyncApplyResult> ApplyTransactionAsync(
            SyncTransactionBatch batch,
            CancellationToken cancellationToken = default)
        {
            events.Add("apply:" + batch.Transaction.CommitEndPosition.Value);
            var position = new BlueTuskLogSequenceNumber(
                checked((ulong)((long)batch.Transaction.CommitEndPosition.Value + PositionOffset)));
            return ValueTask.FromResult(SyncApplyResult.Applied(position));
        }

        public ValueTask<SyncRebuildPreparation> PrepareRebuildAsync(
            SyncProvisionRequest request,
            CancellationToken cancellationToken = default)
        {
            events.Add("prepare");
            return ValueTask.FromResult(new SyncRebuildPreparation("active-v1"));
        }

        public ValueTask<SyncRebuildVerification> VerifyRebuildReadyAsync(
            string pipelineId,
            CancellationToken cancellationToken = default)
        {
            events.Add("verify");
            return ValueTask.FromResult(Verification);
        }

        public ValueTask ActivateRebuildAsync(
            string pipelineId,
            CancellationToken cancellationToken = default)
        {
            events.Add("activate");
            return ValueTask.CompletedTask;
        }

        public ValueTask RetireRebuildGenerationAsync(
            string pipelineId,
            string generation,
            CancellationToken cancellationToken = default)
        {
            events.Add("retire:" + generation);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingCutoverBarrier(
        List<string> events,
        BlueTuskLogSequenceNumber targetPosition) : ISyncRebuildCutoverBarrier
    {
        public ValueTask<ISyncRebuildCutoverLease> AcquireAsync(
            string pipelineId,
            SnapshotEpoch snapshotEpoch,
            CancellationToken cancellationToken = default)
        {
            events.Add("barrier:" + snapshotEpoch.ConsistentPosition.Value);
            return ValueTask.FromResult<ISyncRebuildCutoverLease>(
                new RecordingCutoverLease(events, targetPosition));
        }
    }

    private sealed class RecordingCutoverLease(
        List<string> events,
        BlueTuskLogSequenceNumber targetPosition) : ISyncRebuildCutoverLease
    {
        public BlueTuskLogSequenceNumber TargetPosition => targetPosition;

        public ValueTask DisposeAsync()
        {
            events.Add("release:" + targetPosition.Value);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingVerifier(
        List<string> events,
        SyncRebuildVerification verification) : ISyncRebuildVerifier
    {
        public ValueTask<SyncRebuildVerification> VerifyAsync(
            string pipelineId,
            ISyncDestination destination,
            CancellationToken cancellationToken = default)
        {
            events.Add("authoritative-verify");
            return ValueTask.FromResult(verification);
        }
    }

    private sealed class EmptyReconciliationReader : ISyncReconciliationReader
    {
        public ValueTask<long> CountAsync(
            string pipelineId,
            string collection,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(0L);

        public async IAsyncEnumerable<SyncReconciliationEntry> ReadPartitionAsync(
            string pipelineId,
            string collection,
            int partitionIndex,
            int partitionCount,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }

    private sealed class RecordingObserver(List<string> events) : IChangeDeliveryObserver
    {
        public ValueTask AcknowledgeAsync(
            ChangeTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            events.Add("ack:" + transaction.CommitEndPosition.Value);
            return ValueTask.CompletedTask;
        }

        public ValueTask NackAsync(
            ChangeTransaction transaction,
            Exception? failure,
            CancellationToken cancellationToken = default)
        {
            events.Add("nack:" + transaction.CommitEndPosition.Value);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingProgress : IProgress<SyncRebuildProgress>
    {
        public List<SyncRebuildProgress> Values { get; } = [];

        public void Report(SyncRebuildProgress value) => Values.Add(value);
    }
}
