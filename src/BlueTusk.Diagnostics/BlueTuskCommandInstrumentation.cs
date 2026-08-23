using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace BlueTusk.Diagnostics;

internal readonly struct BlueTuskCommandInstrumentation
{
    private readonly InstrumentationState? _state;

    private BlueTuskCommandInstrumentation(InstrumentationState state)
    {
        _state = state;
    }

    internal static BlueTuskCommandInstrumentation Start(
        string sql,
        string database,
        string host,
        int port,
        BlueTuskDiagnosticsOptions options)
    {
        if (!IsEnabled(options))
        {
            return default;
        }

        var diagnosticInfo = BlueTuskSqlDiagnosticParser.Parse(sql);
        return StartOperation(
            diagnosticInfo.Operation,
            diagnosticInfo.QueryTags,
            database,
            host,
            port,
            options);
    }

    internal static BlueTuskCommandInstrumentation StartOperation(
        string operation,
        string[] queryTags,
        string database,
        string host,
        int port,
        BlueTuskDiagnosticsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!IsEnabled(options))
        {
            return default;
        }

        var activityEnabled = BlueTuskDiagnostics.ActivitySource.HasListeners();
        Activity? activity = null;
        if (activityEnabled)
        {
            var activityName = string.IsNullOrWhiteSpace(database)
                ? operation
                : $"{operation} {database}";
            activity = BlueTuskDiagnostics.ActivitySource.StartActivity(
                activityName,
                ActivityKind.Client);
            activity?.SetTag("db.system.name", "postgresql");
            activity?.SetTag("db.namespace", database);
            activity?.SetTag("db.operation.name", operation);
            activity?.SetTag("db.query.summary", operation);
            activity?.SetTag("server.address", host);
            activity?.SetTag("server.port", port);
            if (queryTags.Length > 0)
            {
                activity?.SetTag("bluetusk.query.tags", queryTags);
            }
        }

        return new BlueTuskCommandInstrumentation(new InstrumentationState(
            Stopwatch.GetTimestamp(),
            activity,
            operation,
            database,
            host,
            port,
            queryTags,
            options.SlowCommandThreshold));
    }

    private static bool IsEnabled(BlueTuskDiagnosticsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return BlueTuskDiagnostics.ActivitySource.HasListeners() ||
            BlueTuskDiagnostics.DatabaseClientOperationDuration.Enabled ||
            BlueTuskDiagnostics.CommandsExecuted.Enabled ||
            BlueTuskDiagnostics.CommandsFailed.Enabled ||
            options.SlowCommandThreshold is not null &&
            BlueTuskSlowCommandEventSource.Log.IsEnabled();
    }

    internal void Complete(Exception? exception) => _state?.Complete(exception);

    private sealed class InstrumentationState
    {
        private readonly long _started;
        private readonly Activity? _activity;
        private readonly string _operation;
        private readonly string _database;
        private readonly string _host;
        private readonly int _port;
        private readonly string[] _queryTags;
        private readonly TimeSpan? _slowCommandThreshold;

        internal InstrumentationState(
            long started,
            Activity? activity,
            string operation,
            string database,
            string host,
            int port,
            string[] queryTags,
            TimeSpan? slowCommandThreshold)
        {
            _started = started;
            _activity = activity;
            _operation = operation;
            _database = database;
            _host = host;
            _port = port;
            _queryTags = queryTags;
            _slowCommandThreshold = slowCommandThreshold;
        }

        internal void Complete(Exception? exception)
        {
            var elapsed = Stopwatch.GetElapsedTime(_started);
            var errorType = exception?.GetType().FullName;
            var tags = new TagList
            {
                { "db.system.name", "postgresql" },
                { "db.namespace", _database },
                { "db.operation.name", _operation },
                { "server.address", _host },
                { "server.port", _port },
            };
            if (errorType is not null)
            {
                tags.Add("error.type", errorType);
                _activity?.SetTag("error.type", errorType);
                _activity?.SetStatus(ActivityStatusCode.Error);
                BlueTuskDiagnostics.CommandsFailed.Add(1, tags);
            }

            BlueTuskDiagnostics.CommandsExecuted.Add(1, tags);
            BlueTuskDiagnostics.DatabaseClientOperationDuration.Record(elapsed.TotalSeconds, tags);

            if (_slowCommandThreshold is { } threshold && elapsed >= threshold)
            {
                BlueTuskSlowCommandEventSource.Log.SlowCommand(
                    _operation,
                    _database,
                    elapsed.TotalSeconds,
                    _queryTags.Length > 0 ? string.Join('|', _queryTags) : string.Empty);
            }

            _activity?.Dispose();
        }
    }
}

[EventSource(Name = BlueTuskDiagnostics.SlowCommandEventSourceName)]
internal sealed class BlueTuskSlowCommandEventSource : EventSource
{
    internal static BlueTuskSlowCommandEventSource Log { get; } = new();

    [Event(1, Level = EventLevel.Warning, Message = "Slow PostgreSQL {0} command on {1}: {2:F6}s; tags={3}")]
    internal void SlowCommand(
        string operation,
        string database,
        double durationSeconds,
        string queryTags)
    {
        if (IsEnabled())
        {
            WriteEvent(1, operation, database, durationSeconds, queryTags);
        }
    }
}
