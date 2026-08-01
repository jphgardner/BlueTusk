using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BlueTusk.Diagnostics;

/// <summary>Shared, low-overhead diagnostics instruments for BlueTusk components.</summary>
public static class BlueTuskDiagnostics
{
    public const string InstrumentationName = "BlueTusk.Diagnostics";

    public const string SlowCommandEventSourceName = "BlueTusk-Diagnostics";

    public static ActivitySource ActivitySource { get; } = new(InstrumentationName);

    public static Meter Meter { get; } = new(InstrumentationName);

    public static Counter<long> ConnectionsOpened { get; } =
        Meter.CreateCounter<long>("bluetusk.connections.opened", unit: "{connection}");

    public static Counter<long> ConnectionsFailed { get; } =
        Meter.CreateCounter<long>("bluetusk.connections.failed", unit: "{connection}");

    public static Histogram<double> CommandDuration { get; } =
        Meter.CreateHistogram<double>("bluetusk.commands.duration", unit: "s");

    /// <summary>OpenTelemetry database client-operation duration using stable semantic naming.</summary>
    public static Histogram<double> DatabaseClientOperationDuration { get; } =
        Meter.CreateHistogram<double>("db.client.operation.duration", unit: "s");

    public static Counter<long> CommandsExecuted { get; } =
        Meter.CreateCounter<long>("bluetusk.commands.executed", unit: "{command}");

    public static Counter<long> CommandsFailed { get; } =
        Meter.CreateCounter<long>("bluetusk.commands.failed", unit: "{command}");

    public static Histogram<long> ProtocolMessageSize { get; } =
        Meter.CreateHistogram<long>("bluetusk.protocol.message.size", unit: "By");

    public static Counter<long> CopyBytes { get; } =
        Meter.CreateCounter<long>("bluetusk.copy.bytes", unit: "By");

    public static Counter<long> PreparedStatements { get; } =
        Meter.CreateCounter<long>("bluetusk.prepared_statements", unit: "{statement}");

    public static Counter<long> ConnectionRetries { get; } =
        Meter.CreateCounter<long>("bluetusk.connections.retries", unit: "{attempt}");

    public static Counter<long> ConnectionFailovers { get; } =
        Meter.CreateCounter<long>("bluetusk.connections.failovers", unit: "{connection}");

    public static Histogram<double> ReplicationReceiveLag { get; } =
        Meter.CreateHistogram<double>("bluetusk.replication.receive_lag", unit: "s");

    public static Histogram<long> ReplicationWalLag { get; } =
        Meter.CreateHistogram<long>("bluetusk.replication.wal_lag", unit: "By");

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

    internal static BlueTuskCommandInstrumentation StartCommand(
        string sql,
        string database,
        string host,
        int port,
        BlueTuskDiagnosticsOptions options) =>
        BlueTuskCommandInstrumentation.Start(sql, database, host, port, options);

    internal static BlueTuskCommandInstrumentation StartBatch(
        string database,
        string host,
        int port,
        BlueTuskDiagnosticsOptions options) =>
        BlueTuskCommandInstrumentation.StartOperation("BATCH", [], database, host, port, options);

    internal static BlueTuskConnectionInstrumentation StartConnection(
        string database,
        string host,
        int port) =>
        BlueTuskConnectionInstrumentation.Start(database, host, port);

    internal static void RecordPreparedStatements(long count, string kind, string action)
    {
        if (count <= 0 || !PreparedStatements.Enabled)
        {
            return;
        }

        PreparedStatements.Add(
            count,
            new KeyValuePair<string, object?>("bluetusk.prepared.kind", kind),
            new KeyValuePair<string, object?>("bluetusk.prepared.action", action));
    }

    internal static void RecordConnectionRetry(string host, int port, string reason)
    {
        if (!ConnectionRetries.Enabled)
        {
            return;
        }

        ConnectionRetries.Add(
            1,
            new KeyValuePair<string, object?>("server.address", host),
            new KeyValuePair<string, object?>("server.port", port),
            new KeyValuePair<string, object?>("bluetusk.retry.reason", reason));
    }

    internal static void RecordConnectionFailover(string host, int port)
    {
        if (!ConnectionFailovers.Enabled)
        {
            return;
        }

        ConnectionFailovers.Add(
            1,
            new KeyValuePair<string, object?>("server.address", host),
            new KeyValuePair<string, object?>("server.port", port));
    }

    internal static void RecordReplicationLag(
        DateTimeOffset serverClock,
        ulong serverWalEnd,
        ulong receivedWalEnd,
        string database,
        string host,
        int port)
    {
        if (!ReplicationReceiveLag.Enabled && !ReplicationWalLag.Enabled)
        {
            return;
        }

        var clockLag = DateTimeOffset.UtcNow - serverClock;
        var receiveLagSeconds = Math.Max(0, clockLag.TotalSeconds);
        var walLag = serverWalEnd > receivedWalEnd
            ? serverWalEnd - receivedWalEnd
            : 0;
        var tags = new TagList
        {
            { "db.system.name", "postgresql" },
            { "db.namespace", database },
            { "server.address", host },
            { "server.port", port },
        };
        ReplicationReceiveLag.Record(receiveLagSeconds, tags);
        ReplicationWalLag.Record(checked((long)Math.Min(walLag, long.MaxValue)), tags);
    }
}
