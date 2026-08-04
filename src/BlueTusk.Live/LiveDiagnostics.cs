using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BlueTusk.Live;

internal static class LiveDiagnostics
{
    internal const string InstrumentationName = "BlueTusk.Live";

    internal static readonly ActivitySource ActivitySource = new(InstrumentationName);

    private static readonly Meter Meter = new(InstrumentationName);

    private static readonly Histogram<double> AuthoritativeQueryDuration =
        Meter.CreateHistogram<double>(
            "bluetusk.live.authoritative_query.duration",
            unit: "s");

    private static readonly Histogram<long> AuthoritativeQueryRows =
        Meter.CreateHistogram<long>(
            "bluetusk.live.authoritative_query.rows",
            unit: "{row}");

    private static readonly Histogram<double> RefreshDuration =
        Meter.CreateHistogram<double>(
            "bluetusk.live.refresh.duration",
            unit: "s");

    private static readonly Histogram<long> RefreshEvents =
        Meter.CreateHistogram<long>(
            "bluetusk.live.refresh.events",
            unit: "{event}");

    private static readonly Counter<long> Connections =
        Meter.CreateCounter<long>(
            "bluetusk.live.connections",
            unit: "{connection}");

    private static readonly UpDownCounter<long> ActiveClients =
        Meter.CreateUpDownCounter<long>(
            "bluetusk.live.clients.active",
            unit: "{client}");

    private static readonly Counter<long> FanOutDeliveries =
        Meter.CreateCounter<long>(
            "bluetusk.live.fanout.deliveries",
            unit: "{delivery}");

    private static readonly Counter<long> ReplayEvents =
        Meter.CreateCounter<long>(
            "bluetusk.live.replay.events",
            unit: "{event}");

    private static readonly Counter<long> ReplayBytes =
        Meter.CreateCounter<long>(
            "bluetusk.live.replay.bytes",
            unit: "By");

    private static readonly Counter<long> SlowClientDisconnects =
        Meter.CreateCounter<long>(
            "bluetusk.live.slow_client.disconnects",
            unit: "{disconnect}");

    private static readonly Counter<long> ResumeValidations =
        Meter.CreateCounter<long>(
            "bluetusk.live.resume.validations",
            unit: "{validation}");

    internal static long GetTimestamp() =>
        AuthoritativeQueryDuration.Enabled || RefreshDuration.Enabled
            ? Stopwatch.GetTimestamp()
            : 0;

    internal static Activity? StartAuthoritativeQuery(string queryName)
    {
        var activity = ActivitySource.StartActivity(
            "bluetusk.live.authoritative_query",
            ActivityKind.Internal);
        activity?.SetTag("bluetusk.live.query.name", queryName);
        return activity;
    }

    internal static void RecordAuthoritativeQuery(
        string queryName,
        string outcome,
        long started,
        int rowCount)
    {
        var tags = QueryTags(queryName, outcome);
        if (started != 0 && AuthoritativeQueryDuration.Enabled)
        {
            AuthoritativeQueryDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalSeconds,
                tags);
        }

        if (rowCount >= 0 && AuthoritativeQueryRows.Enabled)
        {
            AuthoritativeQueryRows.Record(rowCount, tags);
        }
    }

    internal static void RecordRefresh(
        string queryName,
        string outcome,
        long started,
        int eventCount)
    {
        var tags = QueryTags(queryName, outcome);
        if (started != 0 && RefreshDuration.Enabled)
        {
            RefreshDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalSeconds,
                tags);
        }

        if (eventCount >= 0 && RefreshEvents.Enabled)
        {
            RefreshEvents.Record(eventCount, tags);
        }
    }

    internal static void RecordConnection(LiveSubscriptionConnectStatus outcome)
    {
        if (Connections.Enabled)
        {
            Connections.Add(
                1,
                new KeyValuePair<string, object?>(
                    "bluetusk.live.connection.outcome",
                    outcome.ToString().ToLowerInvariant()));
        }
    }

    internal static void RecordActiveClientDelta(long delta)
    {
        if (delta != 0 && ActiveClients.Enabled)
        {
            ActiveClients.Add(delta);
        }
    }

    internal static void RecordFanOut(long deliveries)
    {
        if (deliveries > 0 && FanOutDeliveries.Enabled)
        {
            FanOutDeliveries.Add(deliveries);
        }
    }

    internal static void RecordReplay(long events, long bytes, string operation)
    {
        var tag = new KeyValuePair<string, object?>(
            "bluetusk.live.replay.operation",
            operation);
        if (events > 0 && ReplayEvents.Enabled)
        {
            ReplayEvents.Add(events, tag);
        }

        if (bytes > 0 && ReplayBytes.Enabled)
        {
            ReplayBytes.Add(bytes, tag);
        }
    }

    internal static void RecordSlowClientDisconnect(LiveSlowClientPolicy policy)
    {
        if (SlowClientDisconnects.Enabled)
        {
            SlowClientDisconnects.Add(
                1,
                new KeyValuePair<string, object?>(
                    "bluetusk.live.slow_client.policy",
                    policy.ToString().ToLowerInvariant()));
        }
    }

    internal static void RecordResumeValidation(LiveResumeTokenValidationStatus outcome)
    {
        if (ResumeValidations.Enabled)
        {
            ResumeValidations.Add(
                1,
                new KeyValuePair<string, object?>(
                    "bluetusk.live.resume.outcome",
                    outcome.ToString().ToLowerInvariant()));
        }
    }

    private static TagList QueryTags(string queryName, string outcome) =>
        new()
        {
            { "bluetusk.live.query.name", queryName },
            { "bluetusk.live.outcome", outcome },
        };
}
