using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BlueTusk.Projections;

internal static class ProjectionsDiagnostics
{
    private static readonly Meter Meter = new("BlueTusk.Projections");
    private static readonly Counter<long> Commits = Meter.CreateCounter<long>("bluetusk.projections.commits", "{transaction}");
    private static readonly Counter<long> Fenced = Meter.CreateCounter<long>("bluetusk.projections.fenced", "{attempt}");
    private static readonly Counter<long> Bounds = Meter.CreateCounter<long>("bluetusk.projections.bound_exceeded", "{attempt}");
    private static readonly Histogram<double> ApplyDuration = Meter.CreateHistogram<double>("bluetusk.projections.apply.duration", "s");
    private static readonly Histogram<long> Writes = Meter.CreateHistogram<long>("bluetusk.projections.output.writes", "{row}");
    private static readonly Histogram<long> Invalidations = Meter.CreateHistogram<long>("bluetusk.projections.invalidations", "{document}");
    private static readonly Histogram<long> Bytes = Meter.CreateHistogram<long>("bluetusk.projections.output.bytes", "By");
    private static readonly Counter<long> Pruned = Meter.CreateCounter<long>("bluetusk.projections.retired.rows_pruned", "{row}");
    private static readonly Counter<long> ResetRows = Meter.CreateCounter<long>("bluetusk.projections.reset.rows_deleted", "{row}");

    internal static void Commit(string operation, ProjectionWriteContext? context = null)
    {
        Add(Commits, 1, operation);
        if (context is not null)
        {
            Record(Writes, context.WriteCount);
            Record(Invalidations, context.InvalidationCount);
            Record(Bytes, context.WriteBytes);
        }
    }
    internal static void Apply(long started, string outcome)
    {
        try { ApplyDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, new KeyValuePair<string, object?>("outcome", outcome)); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }
    internal static void LeaseFenced() => Add(Fenced, 1);
    internal static void BoundExceeded() => Add(Bounds, 1);
    internal static void Prune(int count) => Add(Pruned, count);
    internal static void Reset(int count) => Add(ResetRows, count);

    private static void Add(Counter<long> counter, long value, string? operation = null)
    {
        if (!counter.Enabled) { return; }
        try { counter.Add(value, new KeyValuePair<string, object?>("operation", operation)); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }
    private static void Record(Histogram<long> histogram, long value)
    {
        if (!histogram.Enabled) { return; }
        try { histogram.Record(value); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }
}
