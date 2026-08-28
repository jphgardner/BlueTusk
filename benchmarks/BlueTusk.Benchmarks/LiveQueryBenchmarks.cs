using BenchmarkDotNet.Attributes;
using BlueTusk.Live;
using BlueTusk.Live.Testing;

namespace BlueTusk.Benchmarks;

[MemoryDiagnoser]
[JsonExporterAttribute.Brief]
public class LiveQueryBenchmarks
{
    private const int InvalidationCount = 100;
    private static readonly LiveTableDependency Orders = new("sales", "orders");
    private static readonly Func<Row, int> KeySelector = static row => row.Id;
    private Row[] _updatedRows = null!;
    private LiveResultSnapshot<Row, int> _snapshot = null!;
    private LiveResultEvent<Row, int> _updatedEvent = null!;

    [Params(10, 1_000, 100_000)]
    public int ResultCount { get; set; }

    [Params(1, 64, 1_000, 10_000)]
    public int SubscriberCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var initialRows = Enumerable.Range(1, ResultCount)
            .Select(static id => new Row(id, $"value-{id}", id))
            .ToArray();
        _updatedRows = initialRows.ToArray();
        _updatedRows[ResultCount / 2] = _updatedRows[ResultCount / 2] with
        {
            Value = "updated",
        };
        var initial = LiveResultDiffer.Initial(initialRows, KeySelector);
        _snapshot = initial.Snapshot;
        _updatedEvent = AssertSingle(
            LiveResultDiffer.Diff(
                _snapshot,
                _updatedRows,
                KeySelector,
                nextSequence: 2).Events);
    }

    [Benchmark]
    public LiveDiffBatch<Row, int> DiffOneUpdatedRowInOneThousand() =>
        LiveResultDiffer.Diff(
            _snapshot,
            _updatedRows,
            KeySelector,
            nextSequence: 2);

    [Benchmark]
    public LiveDiffBatch<Row, int> DiffUnchangedOneThousandRows() =>
        LiveResultDiffer.Diff(
            _snapshot,
            _snapshot.Rows,
            KeySelector,
            nextSequence: 2);

    [Benchmark]
    public LiveReplayEvent SerializeOneUpdatedRow() =>
        LiveReplayJsonSerializer.Serialize(_updatedEvent);

    [Benchmark]
    public async Task<long> CoalesceOneHundredInvalidationsAndFanOut64SubscribersAsync()
    {
        var invalidations = new InMemoryLiveInvalidationLog();
        var version = _snapshot.Rows.Count - ResultCount;
        var plan = new LiveQueryPlan<Row, int>(
            "benchmark",
            "database",
            new string('a', 64),
            LiveQueryCapabilities.SingleTable |
                LiveQueryCapabilities.TenantFilter |
                LiveQueryCapabilities.DeterministicOrdering |
                LiveQueryCapabilities.BoundedTake,
            [Orders],
            [],
            1,
            (_, _) => ValueTask.FromResult<IReadOnlyList<Row>>(
                [new Row(1, version == 0 ? "before" : "after", version)]),
            KeySelector);
        await using var session = new LiveQuerySession<Row, int>(
            plan,
            LiveQueryArguments.Create([], new Dictionary<string, object?>()),
            new LiveSecurityScope("benchmark", "v1"),
            invalidations);
        await using var shared = new LiveSharedSubscription<Row, int>(
            session,
            new InMemoryLiveReplayStore(),
            new LiveSharedSubscriptionOptions
            {
                MaximumSubscribers = SubscriberCount,
                SubscriberBufferCapacity = 1,
            });
        await shared.StartAsync().ConfigureAwait(false);
        var connections = new LiveSubscriptionConnection[SubscriberCount];
        try
        {
            for (var index = 0; index < connections.Length; index++)
            {
                var result = await shared.ConnectAsync(0).ConfigureAwait(false);
                connections[index] = result.Connection ??
                    throw new InvalidOperationException($"Subscriber {index} did not connect.");
            }

            version = 1;
            for (var index = 0; index < InvalidationCount; index++)
            {
                _ = invalidations.Append("database", [Orders]);
            }

            _ = await shared.RefreshAsync().ConfigureAwait(false);
            var status = shared.Status;
            return status.FanOutDeliveries +
                status.QuerySession.AuthoritativeQueryCount +
                status.QuerySession.CoalescedInvalidationCount;
        }
        finally
        {
            foreach (var connection in connections)
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private static T AssertSingle<T>(IReadOnlyList<T> values) =>
        values.Count == 1
            ? values[0]
            : throw new InvalidOperationException($"Expected one value, found {values.Count}.");

    public sealed record Row(int Id, string Value, int Version);
}
