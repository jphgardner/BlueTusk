using System.Diagnostics.Metrics;

namespace BlueTusk.Projections.Live;

internal static class ProjectionLiveDiagnostics
{
    private static readonly Meter Meter = new("BlueTusk.Projections.Live");
    private static readonly Counter<long> Acquisitions = Meter.CreateCounter<long>("bluetusk.projections.live.publisher.acquisitions", "{attempt}");
    private static readonly Counter<long> Fenced = Meter.CreateCounter<long>("bluetusk.projections.live.publisher.fenced", "{attempt}");
    private static readonly Counter<long> Appends = Meter.CreateCounter<long>("bluetusk.projections.live.replay.committed", "{event}");
    private static readonly Counter<long> Duplicates = Meter.CreateCounter<long>("bluetusk.projections.live.replay.duplicates", "{event}");
    private static readonly Counter<long> Pruned = Meter.CreateCounter<long>("bluetusk.projections.live.replay.pruned", "{event}");
    internal static void Acquisition(bool acquired) => Add(Acquisitions,1,acquired ? "acquired" : "contended");
    internal static void Fence() => Add(Fenced,1);
    internal static void Append(int count) => Add(Appends,count);
    internal static void Duplicate(int count) => Add(Duplicates,count);
    internal static void Prune(int count) => Add(Pruned,count);
    private static void Add(Counter<long> counter,long count,string? outcome=null)
    { if (!counter.Enabled) { return; } try { counter.Add(count,new KeyValuePair<string,object?>("outcome",outcome)); } catch (Exception exception) when (exception is not OutOfMemoryException) { } }
}
