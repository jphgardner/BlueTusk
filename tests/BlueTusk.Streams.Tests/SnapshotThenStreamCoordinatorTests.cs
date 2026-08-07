using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text;
using BlueTusk.TypeSystem;

namespace BlueTusk.Streams.Tests;

public sealed class SnapshotThenStreamCoordinatorTests
{
    [Fact]
    public async Task Lost_exported_snapshot_starts_a_new_explicit_epoch()
    {
        var source = new FakeSnapshotSource(failAttempts: 1);
        var consumer = new RecordingConsumer();
        var coordinator = new SnapshotThenStreamCoordinator(
            source,
            new SnapshotThenStreamOptions { MaximumSnapshotAttempts = 2 });

        await coordinator.RunAsync(consumer);

        Assert.Equal(2, source.Attempts);
        Assert.Equal(
            ["reset", "start", "batch", "reset", "start", "batch", "complete"],
            consumer.Events);
        Assert.Equal(2, consumer.Resets.Count);
        Assert.Null(consumer.Resets[0].AbandonedEpoch);
        Assert.Equal(consumer.Resets[0].Epoch.Value, consumer.Resets[1].AbandonedEpoch);
        Assert.NotEqual(consumer.Resets[0].Epoch.Value, consumer.Resets[1].Epoch.Value);
        Assert.Equal(consumer.Resets[1].Epoch, consumer.Completion!.Epoch);
    }

    [Fact]
    public async Task Snapshot_restart_limit_fails_without_claiming_completion()
    {
        var source = new FakeSnapshotSource(failAttempts: int.MaxValue);
        var consumer = new RecordingConsumer();
        var coordinator = new SnapshotThenStreamCoordinator(
            source,
            new SnapshotThenStreamOptions { MaximumSnapshotAttempts = 2 });

        var error = await Assert.ThrowsAsync<SnapshotRestartLimitExceededException>(
            () => coordinator.RunAsync(consumer));

        Assert.Equal(2, error.Attempts);
        Assert.Equal(2, source.Attempts);
        Assert.Null(consumer.Completion);
    }

    [Fact]
    public async Task Consumer_failure_is_not_misrepresented_as_snapshot_session_loss()
    {
        var source = new FakeSnapshotSource(failAttempts: 0);
        var consumer = new RecordingConsumer { FailBatch = true };
        var coordinator = new SnapshotThenStreamCoordinator(source);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.RunAsync(consumer));

        Assert.Equal("consumer failed", error.Message);
        Assert.Equal(1, source.Attempts);
    }

    [Fact]
    public async Task Session_loss_before_epoch_creation_is_retried_without_a_false_abandoned_epoch()
    {
        var source = new FakeSnapshotSource(failAttempts: 0, failBeginAttempts: 1);
        var consumer = new RecordingConsumer();
        var coordinator = new SnapshotThenStreamCoordinator(
            source,
            new SnapshotThenStreamOptions { MaximumSnapshotAttempts = 2 });

        await coordinator.RunAsync(consumer);

        Assert.Equal(2, source.Attempts);
        var reset = Assert.Single(consumer.Resets);
        Assert.Null(reset.AbandonedEpoch);
    }

    [Fact]
    public void Snapshot_table_rejects_non_key_ordinals()
    {
        var table = Table();

        var error = Assert.Throws<ArgumentException>(() => new PostgreSqlSnapshotTable(table, [1]));

        Assert.Contains("not marked as a key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_delivery_emits_exporter_neutral_metrics()
    {
        long rows = -1;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (ReferenceEquals(instrument.Meter, BlueTuskStreamsDiagnostics.Meter))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "bluetusk.streams.snapshot.rows")
            {
                rows = measurement;
            }
        });
        listener.Start();

        await new SnapshotThenStreamCoordinator(new FakeSnapshotSource(failAttempts: 0))
            .RunAsync(new RecordingConsumer());

        Assert.Equal(1, rows);
    }

    private sealed class FakeSnapshotSource : IConsistentSnapshotSource
    {
        private readonly int _failAttempts;
        private readonly int _failBeginAttempts;

        public FakeSnapshotSource(int failAttempts, int failBeginAttempts = 0)
        {
            _failAttempts = failAttempts;
            _failBeginAttempts = failBeginAttempts;
        }

        public int Attempts { get; private set; }

        public ValueTask<IConsistentSnapshotAttempt> BeginAttemptAsync(
            Guid? abandonedEpoch,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (Attempts <= _failBeginAttempts)
            {
                return ValueTask.FromException<IConsistentSnapshotAttempt>(
                    new SnapshotSessionLostException("slot creation session lost"));
            }

            return ValueTask.FromResult<IConsistentSnapshotAttempt>(
                new FakeSnapshotAttempt(Attempts - _failBeginAttempts <= _failAttempts));
        }
    }

    private sealed class FakeSnapshotAttempt : IConsistentSnapshotAttempt
    {
        private readonly bool _fail;
        private readonly ChangeTable _table = Table();

        public FakeSnapshotAttempt(bool fail)
        {
            _fail = fail;
            Epoch = SnapshotEpoch.Create(Source(), new BlueTuskLogSequenceNumber(100));
            Tables = [_table];
        }

        public SnapshotEpoch Epoch { get; }

        public IReadOnlyList<ChangeTable> Tables { get; }

        public async IAsyncEnumerable<ChangeSnapshotBatch> ReadSnapshotAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = new ChangeRow(_table, [Text("1"), Text("Ada")]);
            yield return new ChangeSnapshotBatch(
                Epoch,
                _table,
                0,
                [new ChangeSnapshotRow(SnapshotRowId.Create(Epoch, _table, [row[0]]), row)],
                isLastForTable: !_fail);
            await Task.Yield();
            if (_fail)
            {
                throw new SnapshotSessionLostException("exporter disconnected");
            }
        }

        public IChangeStream CreateChangeStream() => new EmptyChangeStream();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyChangeStream : IChangeStream
    {
        public async IAsyncEnumerable<ChangeTransactionDelivery> ReadTransactionsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class RecordingConsumer : IChangeStreamConsumer
    {
        public List<string> Events { get; } = [];

        public List<SnapshotReset> Resets { get; } = [];

        public SnapshotComplete? Completion { get; private set; }

        public bool FailBatch { get; init; }

        public ValueTask ResetSnapshotAsync(
            SnapshotReset reset,
            CancellationToken cancellationToken = default)
        {
            Events.Add("reset");
            Resets.Add(reset);
            return ValueTask.CompletedTask;
        }

        public ValueTask StartSnapshotAsync(
            SnapshotStart start,
            CancellationToken cancellationToken = default)
        {
            Events.Add("start");
            return ValueTask.CompletedTask;
        }

        public ValueTask ConsumeSnapshotBatchAsync(
            ChangeSnapshotBatch batch,
            CancellationToken cancellationToken = default)
        {
            Events.Add("batch");
            return FailBatch
                ? ValueTask.FromException(new InvalidOperationException("consumer failed"))
                : ValueTask.CompletedTask;
        }

        public ValueTask CompleteSnapshotAsync(
            SnapshotComplete complete,
            CancellationToken cancellationToken = default)
        {
            Events.Add("complete");
            Completion = complete;
            return ValueTask.CompletedTask;
        }

        public ValueTask ConsumeTransactionAsync(
            ChangeTransactionDelivery delivery,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private static ChangeTable Table() =>
        new(
            42,
            "public",
            "people",
            'd',
            [
                new ChangeColumn(0, "id", 23, -1, true),
                new ChangeColumn(1, "name", 25, -1, false),
            ]);

    private static ChangeColumnValue Text(string value) =>
        ChangeColumnValue.FromValue(Encoding.UTF8.GetBytes(value), ChangeValueEncoding.Text);

    private static ChangeSourceIdentity Source() =>
        new("system", "database", "slot", "publication");
}
