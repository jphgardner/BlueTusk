using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Diagnostics.Tracing;

namespace BlueTusk.Diagnostics.Tests;

public sealed class BlueTuskDiagnosticsTests
{
    [Fact]
    public void SQL_diagnostics_extract_a_bounded_operation_and_query_tags()
    {
        var parsed = BlueTuskSqlDiagnosticParser.Parse(
            """
            -- checkout
            /* outer /* nested */ hint */
            -- inventory
            select * from orders where token = 'must-not-escape'
            """);

        Assert.Equal("SELECT", parsed.Operation);
        Assert.Equal(["checkout", "inventory"], parsed.QueryTags);

        var bounded = BlueTuskSqlDiagnosticParser.Parse(
            string.Join('\n', Enumerable.Range(0, 12).Select(index => $"-- tag-{index}")) +
            "\nUPDATE orders SET state = 'ready'");
        Assert.Equal("UPDATE", bounded.Operation);
        Assert.Equal(8, bounded.QueryTags.Length);
    }

    [Fact]
    public void Database_activity_uses_low_cardinality_tags_and_redacts_payloads()
    {
        Activity? stopped = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BlueTuskDiagnostics.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = activity => stopped = activity,
        };
        ActivitySource.AddActivityListener(listener);

        var instrumentation = BlueTuskDiagnostics.StartCommand(
            "-- checkout\nSELECT * FROM accounts WHERE password = 'sql-secret-must-not-escape'",
            "app",
            "db.example.test",
            5432,
            new BlueTuskDiagnosticsOptions());
        instrumentation.Complete(
            new InvalidOperationException("exception-secret-must-not-escape"));

        Assert.NotNull(stopped);
        Assert.Equal("SELECT app", stopped.DisplayName);
        Assert.Equal(ActivityKind.Client, stopped.Kind);
        Assert.Equal(ActivityStatusCode.Error, stopped.Status);
        Assert.Equal("postgresql", stopped.GetTagItem("db.system.name"));
        Assert.Equal("app", stopped.GetTagItem("db.namespace"));
        Assert.Equal("SELECT", stopped.GetTagItem("db.operation.name"));
        Assert.Equal("db.example.test", stopped.GetTagItem("server.address"));
        Assert.Equal(5432, stopped.GetTagItem("server.port"));
        Assert.Null(stopped.GetTagItem("db.query.text"));
        Assert.DoesNotContain(
            stopped.TagObjects,
            tag => tag.Value?.ToString()?.Contains("must-not-escape", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Database_metrics_record_success_and_failure_without_sensitive_payloads()
    {
        var doubleMeasurements = new List<Measurement<double>>();
        var longMeasurements = new List<Measurement<long>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == BlueTuskDiagnostics.InstrumentationName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>(
            (instrument, value, tags, _) =>
                doubleMeasurements.Add(new Measurement<double>(instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<long>(
            (instrument, value, tags, _) =>
                longMeasurements.Add(new Measurement<long>(instrument.Name, value, tags.ToArray())));
        listener.Start();

        var instrumentation = BlueTuskDiagnostics.StartCommand(
            "DELETE FROM accounts WHERE token = 'metric-secret-must-not-escape'",
            "app",
            "db.example.test",
            5432,
            new BlueTuskDiagnosticsOptions());
        instrumentation.Complete(new InvalidOperationException("metric-exception-secret-must-not-escape"));
        BlueTuskDiagnostics.RecordPreparedStatements(2, "batch", "prepare");
        BlueTuskDiagnostics.RecordConnectionRetry("standby.example.test", 5433, "multi_host");
        BlueTuskDiagnostics.RecordConnectionFailover("standby.example.test", 5433);
        BlueTuskDiagnostics.RecordReplicationLag(
            DateTimeOffset.UtcNow.AddSeconds(-1),
            serverWalEnd: 100,
            receivedWalEnd: 75,
            database: "app",
            host: "standby.example.test",
            port: 5433);

        Assert.Contains(
            doubleMeasurements,
            measurement => measurement.Name == "db.client.operation.duration" && measurement.Value >= 0);
        Assert.Contains(
            longMeasurements,
            measurement => measurement.Name == "bluetusk.commands.executed" && measurement.Value == 1);
        Assert.Contains(
            longMeasurements,
            measurement => measurement.Name == "bluetusk.commands.failed" && measurement.Value == 1);
        Assert.Contains(
            longMeasurements,
            measurement => measurement.Name == "bluetusk.prepared_statements" && measurement.Value == 2);
        Assert.Contains(
            longMeasurements,
            measurement => measurement.Name == "bluetusk.connections.retries" && measurement.Value == 1);
        Assert.Contains(
            longMeasurements,
            measurement => measurement.Name == "bluetusk.connections.failovers" && measurement.Value == 1);
        Assert.Contains(
            doubleMeasurements,
            measurement => measurement.Name == "bluetusk.replication.receive_lag" &&
                measurement.Value >= 1);
        Assert.Contains(
            longMeasurements,
            measurement => measurement.Name == "bluetusk.replication.wal_lag" &&
                measurement.Value == 25);
        Assert.DoesNotContain(
            doubleMeasurements.SelectMany(static measurement => measurement.Tags)
                .Concat(longMeasurements.SelectMany(static measurement => measurement.Tags)),
            tag => tag.Value?.ToString()?.Contains("must-not-escape", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Multiplexing_metrics_expose_bounded_scheduler_state_and_low_cardinality_outcomes()
    {
        var doubleMeasurements = new List<Measurement<double>>();
        var longMeasurements = new List<Measurement<long>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == BlueTuskDiagnostics.InstrumentationName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>(
            (instrument, value, tags, _) =>
                doubleMeasurements.Add(new Measurement<double>(instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<long>(
            (instrument, value, tags, _) =>
                longMeasurements.Add(new Measurement<long>(instrument.Name, value, tags.ToArray())));
        listener.Start();

        BlueTuskDiagnostics.RecordMultiplexingPendingDelta(1);
        BlueTuskDiagnostics.RecordMultiplexingExecutingDelta(1);
        BlueTuskDiagnostics.RecordMultiplexingAdmission("accepted");
        var queuedAt = BlueTuskDiagnostics.GetMultiplexingQueueTimestamp();
        BlueTuskDiagnostics.RecordMultiplexingQueueWait(queuedAt);
        BlueTuskDiagnostics.RecordMultiplexingPipelineSize(4);
        BlueTuskDiagnostics.RecordMultiplexingCommandOutcome("completed");
        BlueTuskDiagnostics.RecordMultiplexingExecutingDelta(-1);
        BlueTuskDiagnostics.RecordMultiplexingPendingDelta(-1);
        BlueTuskDiagnostics.RecordMultiplexingForcedShutdown();

        Assert.Contains(
            doubleMeasurements,
            measurement => measurement.Name == "bluetusk.multiplexing.queue.wait.duration" &&
                measurement.Value >= 0);
        Assert.Contains(
            longMeasurements,
            measurement => measurement.Name == "bluetusk.multiplexing.commands.pending" &&
                measurement.Value == 1);
        Assert.Contains(
            longMeasurements,
            measurement => measurement.Name == "bluetusk.multiplexing.commands.executing" &&
                measurement.Value == 1);
        Assert.Contains(
            longMeasurements,
            measurement => measurement.Name == "bluetusk.multiplexing.pipeline.size" &&
                measurement.Value == 4);
        Assert.Contains(
            longMeasurements,
            measurement => measurement.Name == "bluetusk.multiplexing.admissions" &&
                measurement.Tags.Contains(
                    new KeyValuePair<string, object?>(
                        "bluetusk.multiplexing.admission.outcome",
                        "accepted")));
        Assert.Contains(
            longMeasurements,
            measurement => measurement.Name == "bluetusk.multiplexing.commands" &&
                measurement.Tags.Contains(
                    new KeyValuePair<string, object?>(
                        "bluetusk.multiplexing.command.outcome",
                        "completed")));
        Assert.Contains(
            longMeasurements,
            measurement => measurement.Name == "bluetusk.multiplexing.forced_shutdowns" &&
                measurement.Value == 1);
    }

    [Fact]
    public void Slow_command_events_contain_summary_and_explicit_tags_but_not_SQL_or_errors()
    {
        using var listener = new RecordingEventListener();
        var instrumentation = BlueTuskDiagnostics.StartCommand(
            "-- checkout\nSELECT 'slow-sql-secret-must-not-escape'",
            "app",
            "db.example.test",
            5432,
            new BlueTuskDiagnosticsOptions { SlowCommandThreshold = TimeSpan.Zero });
        instrumentation.Complete(
            new InvalidOperationException("slow-exception-secret-must-not-escape"));

        var written = Assert.Single(listener.Events, item => item.EventId == 1);
        var payload = string.Join('|', written.Payload ?? []);
        Assert.Contains("SELECT", payload, StringComparison.Ordinal);
        Assert.Contains("app", payload, StringComparison.Ordinal);
        Assert.Contains("checkout", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-escape", payload, StringComparison.Ordinal);
    }

    private sealed record Measurement<T>(
        string Name,
        T Value,
        KeyValuePair<string, object?>[] Tags);

    private sealed class RecordingEventListener : EventListener
    {
        private List<EventWrittenEventArgs>? _events;

        internal IReadOnlyList<EventWrittenEventArgs> Events => _events ?? [];

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == BlueTuskDiagnostics.SlowCommandEventSourceName)
            {
                EnableEvents(eventSource, EventLevel.Warning);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData) =>
            (_events ??= []).Add(eventData);
    }
}
