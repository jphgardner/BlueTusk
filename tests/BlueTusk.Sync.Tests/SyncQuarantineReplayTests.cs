using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;

namespace BlueTusk.Sync.Tests;

public sealed class SyncQuarantineReplayTests
{
    private static readonly ChangeSourceIdentity Source =
        new("replay-system", "replay-db", "replay-slot", "public:orders");

    [Fact]
    public async Task Replay_applies_before_compare_and_set_resolution()
    {
        await using var delivery = Delivery();
        var transform = new RecordingTransform();
        var record = Record(transform.Version, delivery.Transaction);
        var store = new RecordingStore(record);
        var destination = new RecordingDestination(store.Events);
        var coordinator = new SyncQuarantineReplayCoordinator(
            transform,
            store,
            new RecordingSource(delivery.Transaction),
            destination,
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero)));

        var result = await coordinator.ReplayAsync(Request(record));

        Assert.Equal(SyncQuarantineReplayStatus.Completed, result.Status);
        Assert.Equal(["read", "apply", "resolve"], store.Events);
        Assert.Equal("replay-1", store.Entry!.ResolvedOperationId);
        Assert.Equal(delivery.Transaction.CommitEndPosition, destination.Position);
    }

    [Fact]
    public async Task Replay_is_idempotent_after_destination_commit_and_resolution_crash()
    {
        await using var delivery = Delivery();
        var transform = new RecordingTransform();
        var record = Record(transform.Version, delivery.Transaction);
        var store = new RecordingStore(record) { FailResolutionOnce = true };
        var destination = new RecordingDestination();
        var coordinator = new SyncQuarantineReplayCoordinator(
            transform,
            store,
            new RecordingSource(delivery.Transaction),
            destination);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ReplayAsync(Request(record)).AsTask());
        var result = await coordinator.ReplayAsync(Request(record));

        Assert.Equal(SyncQuarantineReplayStatus.Completed, result.Status);
        Assert.Equal(2, destination.Attempts);
        Assert.Equal(1, destination.Applications);
    }

    [Fact]
    public async Task Replay_does_not_resolve_when_destination_checkpoint_has_advanced()
    {
        await using var delivery = Delivery();
        var transform = new RecordingTransform();
        var record = Record(transform.Version, delivery.Transaction);
        var store = new RecordingStore(record);
        var destination = new RecordingDestination
        {
            Position = new BlueTuskLogSequenceNumber(
                delivery.Transaction.CommitEndPosition.Value + 1),
        };
        var coordinator = new SyncQuarantineReplayCoordinator(
            transform,
            store,
            new RecordingSource(delivery.Transaction),
            destination);

        var result = await coordinator.ReplayAsync(Request(record));

        Assert.Equal(SyncQuarantineReplayStatus.CheckpointAdvanced, result.Status);
        Assert.Null(store.Entry!.ResolvedOperationId);
        Assert.DoesNotContain("resolve", store.Events);
    }

    [Fact]
    public async Task Replay_reports_expired_relay_retention_without_resolving()
    {
        await using var delivery = Delivery();
        var transform = new RecordingTransform();
        var record = Record(transform.Version, delivery.Transaction);
        var store = new RecordingStore(record);
        var coordinator = new SyncQuarantineReplayCoordinator(
            transform,
            store,
            new RecordingSource(null),
            new RecordingDestination());

        var result = await coordinator.ReplayAsync(Request(record));

        Assert.Equal(SyncQuarantineReplayStatus.SourceTransactionUnavailable, result.Status);
        Assert.Null(store.Entry!.ResolvedOperationId);
    }

    [Fact]
    public async Task Replay_rejects_stale_transform_fingerprint_before_reading_payload()
    {
        await using var delivery = Delivery();
        var transform = new RecordingTransform();
        var record = Record(transform.Version, delivery.Transaction);
        var store = new RecordingStore(record);
        var coordinator = new SyncQuarantineReplayCoordinator(
            transform,
            store,
            new RecordingSource(delivery.Transaction),
            new RecordingDestination());
        var request = Request(record) with
        {
            ExpectedTransformFingerprint = SyncTransformVersion.Create("orders", "v2").Fingerprint,
        };

        await Assert.ThrowsAsync<SyncTransformVersionMismatchException>(
            () => coordinator.ReplayAsync(request).AsTask());

        Assert.Equal(["read"], store.Events);
    }

    private static SyncQuarantineRecord Record(
        SyncTransformVersion transform,
        ChangeTransaction transaction) =>
        new(
            "orders",
            transform,
            Source,
            transaction.TransactionId,
            transaction.CommitEndPosition,
            "MappingFailure",
            "Invalid row",
            new DateTimeOffset(2026, 8, 3, 11, 0, 0, TimeSpan.Zero));

    private static SyncQuarantineReplayRequest Request(SyncQuarantineRecord record) =>
        new()
        {
            Identity = SyncQuarantineIdentity.FromRecord(record),
            ExpectedTransformFingerprint = record.Transform.Fingerprint,
            OperationId = "replay-1",
        };

    private static ChangeTransactionDelivery Delivery() =>
        ChangeDeliveryTestFactory.CreateCommitted(
            Source,
            transactionId: 42,
            new BlueTuskLogSequenceNumber(105));

    private sealed class RecordingTransform : ISyncTransform
    {
        public SyncTransformVersion Version { get; } =
            SyncTransformVersion.Create("orders", "v1");

        public ValueTask<IReadOnlyList<SyncMutation>> TransformTransactionAsync(
            ChangeTransaction transaction,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<SyncMutation>>(
                [new SyncMutation(
                    new ChangeId(
                        transaction.Source,
                        transaction.CommitEndPosition,
                        transaction.TransactionId,
                        0),
                    SyncMutationKind.Upsert,
                    "orders",
                    "42",
                    "{\"status\":\"replayed\"}"u8.ToArray(),
                    "application/json")]);

        public ValueTask<IReadOnlyList<SyncSnapshotMutation>> TransformSnapshotBatchAsync(
            ChangeSnapshotBatch batch,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<SyncSnapshotMutation>>([]);
    }

    private sealed class RecordingSource(ChangeTransaction? transaction) : ISyncQuarantineReplaySource
    {
        public ValueTask<ChangeTransaction?> ReadTransactionAsync(
            SyncQuarantineIdentity identity,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(transaction);
    }

    private sealed class RecordingDestination(List<string>? events = null) :
        ISyncQuarantineReplayDestination
    {
        public BlueTuskLogSequenceNumber? Position { get; set; }

        public int Attempts { get; private set; }

        public int Applications { get; private set; }

        public ValueTask<SyncQuarantineReplayApplyResult> ReplayTransactionAsync(
            SyncTransactionBatch batch,
            string operationId,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            events?.Add("apply");
            if (Position is { } current && current > batch.Transaction.CommitEndPosition)
            {
                return ValueTask.FromResult(new SyncQuarantineReplayApplyResult(
                    SyncQuarantineReplayApplyStatus.CheckpointAdvanced,
                    Position));
            }

            if (Position == batch.Transaction.CommitEndPosition)
            {
                return ValueTask.FromResult(new SyncQuarantineReplayApplyResult(
                    SyncQuarantineReplayApplyStatus.AlreadyApplied,
                    Position));
            }

            Applications++;
            Position = batch.Transaction.CommitEndPosition;
            return ValueTask.FromResult(new SyncQuarantineReplayApplyResult(
                SyncQuarantineReplayApplyStatus.Applied,
                Position));
        }
    }

    private sealed class RecordingStore : ISyncQuarantineStore
    {
        public RecordingStore(SyncQuarantineRecord record)
        {
            Entry = new SyncQuarantineEntry(
                SyncQuarantineIdentity.FromRecord(record),
                record.Transform.Fingerprint,
                record.ErrorType,
                record.ErrorMessage,
                record.RecordedAt,
                null,
                null);
        }

        public List<string> Events { get; } = [];

        public SyncQuarantineEntry? Entry { get; private set; }

        public bool FailResolutionOnce { get; init; }

        private bool ResolutionFailed { get; set; }

        public ValueTask<bool> StoreAsync(
            SyncQuarantineRecord record,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public ValueTask<SyncQuarantineEntry?> ReadAsync(
            SyncQuarantineIdentity identity,
            CancellationToken cancellationToken = default)
        {
            Events.Add("read");
            return ValueTask.FromResult(Entry);
        }

        public ValueTask<SyncQuarantineResolutionResult> ResolveAsync(
            SyncQuarantineIdentity identity,
            string expectedTransformFingerprint,
            string operationId,
            DateTimeOffset resolvedAt,
            CancellationToken cancellationToken = default)
        {
            Events.Add("resolve");
            if (FailResolutionOnce && !ResolutionFailed)
            {
                ResolutionFailed = true;
                throw new InvalidOperationException("Injected resolution crash.");
            }

            if (Entry!.ResolvedOperationId is not null)
            {
                return ValueTask.FromResult(new SyncQuarantineResolutionResult(
                    SyncQuarantineResolutionStatus.AlreadyResolved,
                    Entry));
            }

            Entry = Entry with
            {
                ResolvedOperationId = operationId,
                ResolvedAt = resolvedAt,
            };
            return ValueTask.FromResult(new SyncQuarantineResolutionResult(
                SyncQuarantineResolutionStatus.Resolved,
                Entry));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
