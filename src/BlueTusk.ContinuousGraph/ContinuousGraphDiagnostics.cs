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

    internal static (long Started, Activity? Activity) StartEvaluation(
        ContinuousGraphEvaluationMode mode)
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
        return (
            EvaluationDuration.Enabled ? Stopwatch.GetTimestamp() : 0,
            activity);
    }

    internal static void RecordEvaluation(
        ContinuousGraphEvaluationMode mode,
        string outcome,
        int eventCount,
        long started,
        Activity? activity)
    {
        var tags = new TagList
        {
            {
                "bluetusk.graph.evaluation.mode",
                mode.ToString().ToLowerInvariant()
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

        if (started != 0 && EvaluationDuration.Enabled)
        {
            EvaluationDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalSeconds,
                tags);
        }

        activity?.SetTag("bluetusk.graph.evaluation.events", eventCount);
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
