using BenchmarkDotNet.Attributes;
using BlueTusk.Live;

namespace BlueTusk.Benchmarks;

[MemoryDiagnoser]
public class LiveAffectedSnapshotBenchmarks
{
    private LiveResultSnapshot<Row, int> _snapshot = null!;
    private Row[] _current = null!;
    private Row[] _affected = null!;

    [Params(10, 1_000, 100_000)]
    public int RowCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _current = Enumerable.Range(0, RowCount).Select(index => new Row(index, "original")).ToArray();
        _snapshot = LiveResultDiffer.Initial<Row, int>(_current, static row => row.Id).Snapshot;
        _affected = [new(RowCount - 1, "changed")];
        _current[^1] = _affected[0];
    }

    [Benchmark(Baseline = true)]
    public LiveDiffBatch<Row, int> AuthoritativeDiff() =>
        LiveResultDiffer.Diff(_snapshot, _current, static row => row.Id);

    [Benchmark]
    public LiveDiffBatch<Row, int> AffectedDiff() =>
        LiveResultDiffer.DiffAffected(_snapshot, _affected, static row => row.Id);

    public sealed record Row(int Id, string Value);
}
