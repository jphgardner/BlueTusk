using System.Runtime.CompilerServices;
using BlueTusk.Replication;
using BlueTusk.Replication.PgOutput;
using BlueTusk.TypeSystem;

namespace BlueTusk.Streams.Tests;

public sealed class DurableStateTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Memory_store_enforces_monotonic_compare_and_swap()
    {
        var store = new MemoryChangeStreamStateStore();
        var identity = CheckpointIdentity();
        var key = ChangeStreamStateKey.Create(identity.Source, "orders");
        var lease = AssertLease(await store.AcquireAsync(key, "worker-1", TimeSpan.FromMinutes(1)));

        var first = identity.MoveTo(Lsn(100), 0);
        var stored = await store.CompareExchangeAsync(key, -1, first, lease);
        Assert.Equal(ChangeCheckpointWriteStatus.Stored, stored.Status);

        var conflict = await store.CompareExchangeAsync(key, -1, identity.MoveTo(Lsn(125), 0), lease);
        Assert.Equal(ChangeCheckpointWriteStatus.Conflict, conflict.Status);
        Assert.Equal(first, conflict.Current);

        var backwards = await store.CompareExchangeAsync(key, 0, identity.MoveTo(Lsn(50), 1), lease);
        Assert.Equal(ChangeCheckpointWriteStatus.BackwardMovement, backwards.Status);
        Assert.Equal(first, await store.ReadAsync(key));
    }

    [Fact]
    public async Task Expired_owner_is_fenced_after_a_new_owner_acquires_the_lease()
    {
        var clock = new ManualTimeProvider(Timestamp);
        var store = new MemoryChangeStreamStateStore(clock);
        var identity = CheckpointIdentity();
        var key = ChangeStreamStateKey.Create(identity.Source, "orders");
        var staleLease = AssertLease(
            await store.AcquireAsync(key, "worker-1", TimeSpan.FromMinutes(1)));

        clock.Advance(TimeSpan.FromMinutes(2));
        var replacementLease = AssertLease(
            await store.AcquireAsync(key, "worker-2", TimeSpan.FromMinutes(1)));

        Assert.True(replacementLease.FencingToken > staleLease.FencingToken);
        var write = await store.CompareExchangeAsync(
            key,
            -1,
            identity.MoveTo(Lsn(100), 0),
            staleLease);
        Assert.Equal(ChangeCheckpointWriteStatus.Fenced, write.Status);
        Assert.Null(await store.ReadAsync(key));
    }

    [Fact]
    public async Task Acknowledgement_persists_checkpoint_before_feedback()
    {
        var events = new List<string>();
        var store = new RecordingStateStore(events);
        var feedback = new RecordingFeedbackSender(events);
        var identity = CheckpointIdentity();
        var key = ChangeStreamStateKey.Create(identity.Source, "orders");
        await using var observer = await CheckpointingChangeDeliveryObserver.AcquireAsync(
            store,
            key,
            "worker-1",
            TimeSpan.FromMinutes(1),
            identity,
            feedback);
        var delivery = await ReadDeliveryAsync(observer);

        await delivery.AcknowledgeAsync();

        Assert.Equal(["checkpoint", "feedback"], events);
        Assert.Equal(Lsn(21), observer.Checkpoint?.AcknowledgedCommitPosition);
        Assert.Equal(Lsn(21), Assert.Single(feedback.Positions));
    }

    [Fact]
    public async Task Checkpoint_failure_never_sends_feedback()
    {
        var store = new RecordingStateStore { RejectWrites = true };
        var feedback = new RecordingFeedbackSender();
        var identity = CheckpointIdentity();
        await using var observer = await CheckpointingChangeDeliveryObserver.AcquireAsync(
            store,
            ChangeStreamStateKey.Create(identity.Source, "orders"),
            "worker-1",
            TimeSpan.FromMinutes(1),
            identity,
            feedback);
        var delivery = await ReadDeliveryAsync(observer);

        var exception = await Assert.ThrowsAsync<ChangeStreamCheckpointWriteException>(
            () => delivery.AcknowledgeAsync().AsTask());

        Assert.Equal(ChangeCheckpointWriteStatus.Conflict, exception.Status);
        Assert.Empty(feedback.Positions);
        Assert.Equal(ChangeDeliveryState.Active, delivery.State);
    }

    [Fact]
    public async Task Feedback_failure_retries_without_rewriting_the_durable_checkpoint()
    {
        var store = new RecordingStateStore();
        var feedback = new RecordingFeedbackSender { FailuresRemaining = 1 };
        var identity = CheckpointIdentity();
        await using var observer = await CheckpointingChangeDeliveryObserver.AcquireAsync(
            store,
            ChangeStreamStateKey.Create(identity.Source, "orders"),
            "worker-1",
            TimeSpan.FromMinutes(1),
            identity,
            feedback);
        var delivery = await ReadDeliveryAsync(observer);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => delivery.AcknowledgeAsync().AsTask());
        Assert.Equal(Lsn(21), observer.Checkpoint?.AcknowledgedCommitPosition);
        Assert.Equal(ChangeDeliveryState.Active, delivery.State);

        await delivery.AcknowledgeAsync();

        Assert.Equal(1, store.CheckpointWriteCount);
        Assert.Equal(2, feedback.AttemptCount);
        Assert.Equal(Lsn(21), Assert.Single(feedback.Positions));
    }

    [Fact]
    public async Task Nack_does_not_advance_checkpoint_or_feedback()
    {
        var store = new RecordingStateStore();
        var feedback = new RecordingFeedbackSender();
        var identity = CheckpointIdentity();
        var key = ChangeStreamStateKey.Create(identity.Source, "orders");
        await using var observer = await CheckpointingChangeDeliveryObserver.AcquireAsync(
            store,
            key,
            "worker-1",
            TimeSpan.FromMinutes(1),
            identity,
            feedback);
        var delivery = await ReadDeliveryAsync(observer);

        await delivery.NackAsync(new InvalidOperationException("Destination unavailable."));

        Assert.Null(await store.ReadAsync(key));
        Assert.Empty(feedback.Positions);
    }

    [Fact]
    public async Task Incompatible_checkpoint_releases_the_acquired_lease()
    {
        var store = new MemoryChangeStreamStateStore();
        var identity = CheckpointIdentity();
        var key = ChangeStreamStateKey.Create(identity.Source, "orders");
        await using (var observer = await CheckpointingChangeDeliveryObserver.AcquireAsync(
                         store,
                         key,
                         "worker-1",
                         TimeSpan.FromMinutes(1),
                         identity,
                         new RecordingFeedbackSender()))
        {
            var delivery = await ReadDeliveryAsync(observer);
            await delivery.AcknowledgeAsync();
        }

        var changedMapping = ChangeStreamCheckpoint.CreateInitial(
            identity.Source,
            identity.DatabaseIdentity,
            identity.OutputPlugin,
            "mapping-v2");
        await Assert.ThrowsAsync<ChangeStreamCheckpointMismatchException>(
            () => CheckpointingChangeDeliveryObserver.AcquireAsync(
                    store,
                    key,
                    "worker-2",
                    TimeSpan.FromMinutes(1),
                    changedMapping,
                    new RecordingFeedbackSender())
                .AsTask());

        var acquired = await store.AcquireAsync(key, "worker-3", TimeSpan.FromMinutes(1));
        Assert.Equal(ChangeLeaseAcquireStatus.Acquired, acquired.Status);
    }

    private static ChangeStreamCheckpoint CheckpointIdentity() =>
        ChangeStreamCheckpoint.CreateInitial(
            new ChangeSourceIdentity("739463", "app", "orders_slot", "public:orders"),
            "app@739463",
            "pgoutput",
            "mapping-v1");

    private static ChangeStreamLease AssertLease(ChangeLeaseAcquireResult result)
    {
        Assert.Equal(ChangeLeaseAcquireStatus.Acquired, result.Status);
        return Assert.IsType<ChangeStreamLease>(result.Lease);
    }

    private static async Task<ChangeTransactionDelivery> ReadDeliveryAsync(
        IChangeDeliveryObserver observer)
    {
        var stream = new PgOutputChangeStream(
            Messages(
                Envelope(new BlueTuskPgOutputRelation(
                    null,
                    7,
                    "public",
                    "orders",
                    'd',
                    [new BlueTuskPgOutputRelationColumn(
                        BlueTuskPgOutputRelationColumnOptions.Key,
                        "id",
                        23,
                        -1)])),
                Envelope(new BlueTuskPgOutputBegin(Lsn(10), Timestamp, 1)),
                Envelope(new BlueTuskPgOutputInsert(
                    null,
                    7,
                    new BlueTuskPgOutputTuple(
                        [new BlueTuskPgOutputTupleValue(
                            BlueTuskPgOutputTupleValueKind.Text,
                            "1"u8.ToArray())]))),
                Envelope(new BlueTuskPgOutputCommit(Lsn(20), Lsn(21), Timestamp))),
            CheckpointIdentity().Source,
            observer: observer);
        var enumerator = stream.ReadTransactionsAsync().GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        return enumerator.Current;
    }

    private static BlueTuskPgOutputEnvelope Envelope(BlueTuskPgOutputMessage message) =>
        new(
            new BlueTuskXLogData(
                Lsn(1),
                Lsn(500),
                Timestamp,
                ReadOnlyMemory<byte>.Empty),
            message);

    private static BlueTuskLogSequenceNumber Lsn(ulong value) => new(value);

    private static async IAsyncEnumerable<BlueTuskPgOutputEnvelope> Messages(
        IEnumerable<BlueTuskPgOutputEnvelope> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return message;
        }
    }

    private static IAsyncEnumerable<BlueTuskPgOutputEnvelope> Messages(
        params BlueTuskPgOutputEnvelope[] messages) =>
        Messages((IEnumerable<BlueTuskPgOutputEnvelope>)messages);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class RecordingFeedbackSender(List<string>? events = null)
        : IReplicationFeedbackSender
    {
        private readonly List<string> _events = events ?? [];

        public int AttemptCount { get; private set; }

        public int FailuresRemaining { get; set; }

        public List<BlueTuskLogSequenceNumber> Positions { get; } = [];

        public ValueTask SendFeedbackAsync(
            BlueTuskLogSequenceNumber position,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AttemptCount++;
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("Injected feedback failure.");
            }

            Positions.Add(position);
            _events.Add("feedback");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingStateStore(List<string>? events = null)
        : IChangeStreamStateStore
    {
        private readonly List<string> _events = events ?? [];
        private readonly MemoryChangeStreamStateStore _inner = new();

        public int CheckpointWriteCount { get; private set; }

        public bool RejectWrites { get; set; }

        public ValueTask<ChangeStreamCheckpoint?> ReadAsync(
            ChangeStreamStateKey key,
            CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(key, cancellationToken);

        public async ValueTask<ChangeCheckpointWriteResult> CompareExchangeAsync(
            ChangeStreamStateKey key,
            long expectedGeneration,
            ChangeStreamCheckpoint replacement,
            ChangeStreamLease lease,
            CancellationToken cancellationToken = default)
        {
            if (RejectWrites)
            {
                return new ChangeCheckpointWriteResult(
                    ChangeCheckpointWriteStatus.Conflict,
                    await _inner.ReadAsync(key, cancellationToken));
            }

            var result = await _inner.CompareExchangeAsync(
                key,
                expectedGeneration,
                replacement,
                lease,
                cancellationToken);
            if (result.Status == ChangeCheckpointWriteStatus.Stored)
            {
                CheckpointWriteCount++;
                _events.Add("checkpoint");
            }

            return result;
        }

        public ValueTask<ChangeLeaseAcquireResult> AcquireAsync(
            ChangeStreamStateKey key,
            string ownerId,
            TimeSpan duration,
            CancellationToken cancellationToken = default) =>
            _inner.AcquireAsync(key, ownerId, duration, cancellationToken);

        public ValueTask<ChangeStreamLease?> RenewAsync(
            ChangeStreamLease lease,
            TimeSpan duration,
            CancellationToken cancellationToken = default) =>
            _inner.RenewAsync(lease, duration, cancellationToken);

        public ValueTask<bool> ReleaseAsync(
            ChangeStreamLease lease,
            CancellationToken cancellationToken = default) =>
            _inner.ReleaseAsync(lease, cancellationToken);
    }
}
