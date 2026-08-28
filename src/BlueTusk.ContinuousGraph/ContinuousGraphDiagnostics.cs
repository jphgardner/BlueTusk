using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BlueTusk.ContinuousGraph;

internal static class ContinuousGraphDiagnostics
{
    internal const string InstrumentationName = "BlueTusk.ContinuousGraph";

    internal static readonly ActivitySource ActivitySource = new(InstrumentationName);

    private static readonly Meter Meter = new(InstrumentationName);

    private static readonly UpDownCounter<long> ActiveEvaluations =
        Meter.CreateUpDownCounter<long>(
            "bluetusk.graph.evaluations.active",
            unit: "{evaluation}");

    private static readonly Counter<long> Evaluations =
        Meter.CreateCounter<long>(
            "bluetusk.graph.evaluations",
            unit: "{evaluation}");

    private static readonly Histogram<double> EvaluationDuration =
        Meter.CreateHistogram<double>(
            "bluetusk.graph.evaluation.duration",
            unit: "s");

    private static readonly Histogram<long> EvaluationEvents =
        Meter.CreateHistogram<long>(
            "bluetusk.graph.evaluation.events",
            unit: "{event}");

    private static readonly Histogram<long> AffectedKeys =
        Meter.CreateHistogram<long>(
            "bluetusk.graph.affected_keys",
            unit: "{key}");

    private static readonly Histogram<long> QueryCount =
        Meter.CreateHistogram<long>(
            "bluetusk.graph.queries",
            unit: "{query}");

    internal static (long Started, Activity? Activity) StartEvaluation(
        ContinuousGraphEvaluationMode mode,
        ContinuousGraphMaintenanceTier tier)
    {
        if (ActiveEvaluations.Enabled)
        {
            ActiveEvaluations.Add(
                1,
                ModeTag(mode));
        }

        var activity = ActivitySource.StartActivity(
            "bluetusk.graph.evaluate",
            ActivityKind.Internal);
        activity?.SetTag(
            "bluetusk.graph.evaluation.mode",
            mode.ToString().ToLowerInvariant());
        activity?.SetTag(
            "bluetusk.graph.maintenance.tier",
            tier.ToString().ToLowerInvariant());
        return (
            EvaluationDuration.Enabled ? Stopwatch.GetTimestamp() : 0,
            activity);
    }

    internal static void RecordEvaluation(
        ContinuousGraphEvaluationMode mode,
        ContinuousGraphMaintenanceTier tier,
        string outcome,
        int eventCount,
        int affectedKeyCount,
        int queryCount,
        string? detail,
        long started,
        Activity? activity)
    {
        var tags = new TagList
        {
            {
                "bluetusk.graph.evaluation.mode",
                mode.ToString().ToLowerInvariant()
            },
            {
                "bluetusk.graph.maintenance.tier",
                tier.ToString().ToLowerInvariant()
            },
            { "bluetusk.graph.evaluation.outcome", outcome },
        };
        if (ActiveEvaluations.Enabled)
        {
            ActiveEvaluations.Add(-1, ModeTag(mode));
        }

        if (Evaluations.Enabled)
        {
            Evaluations.Add(1, tags);
        }

        if (EvaluationEvents.Enabled)
        {
            EvaluationEvents.Record(eventCount, tags);
        }

        if (AffectedKeys.Enabled)
        {
            AffectedKeys.Record(affectedKeyCount, tags);
        }

        if (QueryCount.Enabled)
        {
            QueryCount.Record(queryCount, tags);
        }

        if (started != 0 && EvaluationDuration.Enabled)
        {
            EvaluationDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalSeconds,
                tags);
        }

        activity?.SetTag("bluetusk.graph.evaluation.events", eventCount);
        activity?.SetTag("bluetusk.graph.affected_keys", affectedKeyCount);
        activity?.SetTag("bluetusk.graph.queries", queryCount);
        activity?.SetTag("bluetusk.graph.fallback.reason", detail);
        activity?.SetTag("bluetusk.graph.evaluation.outcome", outcome);
        activity?.SetStatus(
            outcome is "committed" or "abandoned"
                ? ActivityStatusCode.Ok
                : ActivityStatusCode.Error,
            outcome);
        activity?.Dispose();
    }

    private static KeyValuePair<string, object?> ModeTag(
        ContinuousGraphEvaluationMode mode) =>
        new(
            "bluetusk.graph.evaluation.mode",
            mode.ToString().ToLowerInvariant());
}
