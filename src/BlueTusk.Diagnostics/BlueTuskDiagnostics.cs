using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BlueTusk.Diagnostics;

/// <summary>Shared, low-overhead diagnostics instruments for BlueTusk components.</summary>
public static class BlueTuskDiagnostics
{
    public const string InstrumentationName = "BlueTusk.Diagnostics";

    public static ActivitySource ActivitySource { get; } = new(InstrumentationName);

    public static Meter Meter { get; } = new(InstrumentationName);

    public static Counter<long> ConnectionsOpened { get; } =
        Meter.CreateCounter<long>("bluetusk.connections.opened", unit: "{connection}");

    public static Counter<long> ConnectionsFailed { get; } =
        Meter.CreateCounter<long>("bluetusk.connections.failed", unit: "{connection}");

    public static Histogram<double> CommandDuration { get; } =
        Meter.CreateHistogram<double>("bluetusk.commands.duration", unit: "s");

    public static Histogram<long> ProtocolMessageSize { get; } =
        Meter.CreateHistogram<long>("bluetusk.protocol.message.size", unit: "By");

    public static UpDownCounter<long> PoolConnections { get; } =
        Meter.CreateUpDownCounter<long>("bluetusk.pool.connections", unit: "{connection}");

    public static UpDownCounter<long> PoolLeases { get; } =
        Meter.CreateUpDownCounter<long>("bluetusk.pool.leases", unit: "{connection}");

    public static UpDownCounter<long> PoolWaiters { get; } =
        Meter.CreateUpDownCounter<long>("bluetusk.pool.waiters", unit: "{request}");

    public static Counter<long> PoolReuses { get; } =
        Meter.CreateCounter<long>("bluetusk.pool.reuses", unit: "{connection}");

    public static Counter<long> PoolDiscards { get; } =
        Meter.CreateCounter<long>("bluetusk.pool.discards", unit: "{connection}");

    public static Counter<long> PoolResets { get; } =
        Meter.CreateCounter<long>("bluetusk.pool.resets", unit: "{connection}");

    public static Histogram<double> PoolCheckoutDuration { get; } =
        Meter.CreateHistogram<double>("bluetusk.pool.checkout.duration", unit: "s");
}
