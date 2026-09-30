using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BlueTusk.Jobs;

/// <summary>Telemetry deliberately omits tenant names, payloads, keys, and exception text.</summary>
public static class JobTelemetry
{
    public const string InstrumentationName = "BlueTusk.Jobs";
    public static ActivitySource ActivitySource { get; } = new(InstrumentationName);
    public static Meter Meter { get; } = new(InstrumentationName);

    internal static Counter<long> Claimed { get; } = Meter.CreateCounter<long>("bluetusk.jobs.claimed");
    internal static Counter<long> Succeeded { get; } = Meter.CreateCounter<long>("bluetusk.jobs.succeeded");
    internal static Counter<long> Failed { get; } = Meter.CreateCounter<long>("bluetusk.jobs.failed_attempts");
    internal static Counter<long> Fenced { get; } = Meter.CreateCounter<long>("bluetusk.jobs.fenced_operations");
    internal static Counter<long> StoreFailures { get; } = Meter.CreateCounter<long>("bluetusk.jobs.store_failures");
    internal static Histogram<double> HandlerDuration { get; } = Meter.CreateHistogram<double>("bluetusk.jobs.handler.duration", "s");
}
