using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace BlueTusk.Benchmarks;

internal sealed class Percentile99Column : IColumn
{
    internal static Percentile99Column Instance { get; } = new();

    public string Id => "StatisticColumn.P99Microseconds";

    public string ColumnName => "P99 (us)";

    public bool AlwaysShow => true;

    public ColumnCategory Category => ColumnCategory.Statistics;

    public int PriorityInCategory => 3;

    public bool IsNumeric => true;

    public UnitType UnitType => UnitType.Dimensionless;

    public string Legend =>
        "99th percentile of BenchmarkDotNet workload measurements, normalized per operation, in microseconds";

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) =>
        GetValue(summary, benchmarkCase, summary.Style);

    public string GetValue(
        Summary summary,
        BenchmarkCase benchmarkCase,
        SummaryStyle style)
    {
        var statistics = summary[benchmarkCase]?.ResultStatistics;
        return statistics is null
            ? "NA"
            : (statistics.Percentiles.Percentile(99) / 1_000d)
                .ToString("N2", style.CultureInfo);
    }

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    public bool IsAvailable(Summary summary) => true;
}
