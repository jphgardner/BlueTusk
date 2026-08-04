using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BlueTusk.ControlPlane;

internal static class ControlPlaneDiagnostics
{
    internal const string InstrumentationName = "BlueTusk.ControlPlane";

    private static readonly ActivitySource ActivitySource = new(InstrumentationName);
    private static readonly Meter Meter = new(InstrumentationName);

    private static readonly UpDownCounter<long> ActiveOperations =
        Meter.CreateUpDownCounter<long>(
            "bluetusk.control_plane.operations.active",
            unit: "{operation}");

    private static readonly Counter<long> Operations =
        Meter.CreateCounter<long>(
            "bluetusk.control_plane.operations",
            unit: "{operation}");

    private static readonly Histogram<double> OperationDuration =
        Meter.CreateHistogram<double>(
            "bluetusk.control_plane.operation.duration",
            unit: "s");

    internal static OperationScope StartOperation(string operation) =>
        new(operation);

    internal sealed class OperationScope : IDisposable
    {
        private readonly string _operation;
        private readonly long _started;
        private readonly Activity? _activity;
        private int _completed;

        internal OperationScope(string operation)
        {
            _operation = operation;
            var operationTag = OperationTag(operation);
            if (ActiveOperations.Enabled)
            {
                ActiveOperations.Add(1, operationTag);
            }

            _started = OperationDuration.Enabled
                ? Stopwatch.GetTimestamp()
                : 0;
            _activity = ActivitySource.StartActivity(
                $"bluetusk.control_plane.{operation}",
                ActivityKind.Internal);
            _activity?.SetTag("bluetusk.control_plane.operation", operation);
        }

        internal void Complete(string outcome)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            var tags = new TagList
            {
                { "bluetusk.control_plane.operation", _operation },
                { "bluetusk.control_plane.outcome", outcome },
            };
            if (ActiveOperations.Enabled)
            {
                ActiveOperations.Add(-1, OperationTag(_operation));
            }

            if (Operations.Enabled)
            {
                Operations.Add(1, tags);
            }

            if (_started != 0 && OperationDuration.Enabled)
            {
                OperationDuration.Record(
                    Stopwatch.GetElapsedTime(_started).TotalSeconds,
                    tags);
            }

            _activity?.SetTag("bluetusk.control_plane.outcome", outcome);
            _activity?.SetStatus(
                outcome is "changed" or "no_change" or "paused" or "deleted"
                    ? ActivityStatusCode.Ok
                    : ActivityStatusCode.Error,
                outcome);
            _activity?.Dispose();
        }

        public void Dispose() => Complete("abandoned");
    }

    private static KeyValuePair<string, object?> OperationTag(string operation) =>
        new("bluetusk.control_plane.operation", operation);
}
