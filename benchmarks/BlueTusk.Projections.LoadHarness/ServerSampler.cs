using System.Diagnostics;
using System.Globalization;
using BlueTusk.Data;

namespace BlueTusk.Projections.LoadHarness;

/// <summary>
/// Optional diagnostic PostgreSQL sampler, enabled only by BLUETUSK_PROJECTIONS_LOAD_SERVER_SAMPLES=1
/// (run-load.ps1 -Diagnostics). It owns one dedicated session outside the measured pool under a
/// distinct application name, so the existing probe's client/wait counters are unchanged. Every
/// 100 ms it records non-idle server process wait events (walsender always) with only the leading
/// SQL keyword; every second it records cumulative WAL/relation I/O, checkpointer, WAL and replication
/// positions. It never records SQL text, parameters or payloads.
/// </summary>
internal sealed class ServerSampler : IAsyncDisposable
{
    private readonly BlueTuskDataSource _source;
    private readonly DeliveryTrace _trace;
    private readonly JsonlWriter _writer;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _sampling;

    private ServerSampler(string connection, string path, DeliveryTrace trace)
    {
        var settings = new BlueTuskConnectionStringBuilder(connection) { MaximumPoolSize = 1, ApplicationName = "BlueTuskProjectionsLoadSampler" };
        _source = BlueTuskDataSource.Create(settings.ConnectionString);
        _trace = trace;
        _writer = new JsonlWriter(path);
        _sampling = SampleAsync(_stop.Token);
    }

    internal static ServerSampler? StartIfEnabled(string connection, string path, DeliveryTrace trace) =>
        Environment.GetEnvironmentVariable("BLUETUSK_PROJECTIONS_LOAD_SERVER_SAMPLES") == "1" ? new(connection, path, trace) : null;

    private async Task SampleAsync(CancellationToken token)
    {
        try
        {
            await using var connection = await _source.OpenConnectionAsync(token);
            await using var activity = Sql.Command(connection, null, """
                SELECT extract(epoch FROM clock_timestamp())::float8 * 1000,
                    coalesce((SELECT json_agg(json_build_array(pid, backend_type, coalesce(state, ''), coalesce(wait_event_type, ''),
                        coalesce(wait_event, ''), CASE WHEN application_name = 'BlueTuskProjectionsLoadHarness' THEN 'h' ELSE '' END,
                        coalesce((extract(epoch FROM clock_timestamp() - state_change) * 1000)::bigint, -1),
                        CASE WHEN backend_type = 'client backend' THEN upper(left(split_part(ltrim(query), ' ', 1), 16)) ELSE '' END))
                    FROM pg_stat_activity
                    WHERE pid <> pg_backend_pid() AND (backend_type = 'walsender' OR (
                        wait_event_type IS DISTINCT FROM 'Activity' AND
                        NOT (backend_type = 'client backend' AND (state = 'idle' OR application_name IS DISTINCT FROM 'BlueTuskProjectionsLoadHarness'))))
                    )::text, '[]')
                """);
            activity.CommandTimeout = 5;
            await using var counters = Sql.Command(connection, null, """
                SELECT extract(epoch FROM clock_timestamp())::float8 * 1000, pg_current_wal_insert_lsn()::text, pg_current_wal_flush_lsn()::text,
                    coalesce((SELECT json_agg(json_build_array(backend_type, context, writes, write_time, fsyncs, fsync_time))
                        FROM pg_stat_io WHERE object = 'wal' AND (writes > 0 OR fsyncs > 0))::text, '[]'),
                    coalesce((SELECT json_agg(json_build_array(backend_type, context, writes, fsyncs, writebacks))
                        FROM pg_stat_io WHERE object = 'relation' AND (writes > 0 OR fsyncs > 0 OR writebacks > 0))::text, '[]'),
                    (SELECT row_to_json(c) FROM pg_stat_checkpointer c)::text,
                    (SELECT row_to_json(w) FROM pg_stat_wal w)::text,
                    coalesce((SELECT json_agg(json_build_array(pid, application_name, state, sent_lsn::text, write_lsn::text, flush_lsn::text))
                        FROM pg_stat_replication)::text, '[]'),
                    coalesce((SELECT json_agg(json_build_array(slot_name, active, restart_lsn::text, confirmed_flush_lsn::text))
                        FROM pg_replication_slots)::text, '[]')
                """);
            counters.CommandTimeout = 5;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            var tick = 0L;
            do
            {
                var started = Stopwatch.GetTimestamp();
                await using (var reader = await activity.ExecuteReaderAsync(token))
                {
                    Program.Check(await reader.ReadAsync(token), "server activity sample row");
                    var server = reader.GetDouble(0); var rows = reader.GetString(1);
                    var ended = Stopwatch.GetTimestamp();
                    _writer.Write(writer =>
                    {
                        writer.WriteString("k", "act");
                        writer.WriteNumber("t", DeliveryTrace.Round(_trace.Milliseconds(started)));
                        writer.WriteNumber("rtt", DeliveryTrace.Round(Stopwatch.GetElapsedTime(started, ended).TotalMilliseconds));
                        writer.WriteNumber("srv", Math.Round(server, 3));
                        writer.WritePropertyName("rows"); writer.WriteRawValue(rows, skipInputValidation: true);
                    });
                }
                if (tick++ % 10 == 0)
                {
                    var countersStarted = Stopwatch.GetTimestamp();
                    await using var reader = await counters.ExecuteReaderAsync(token);
                    Program.Check(await reader.ReadAsync(token), "server counter sample row");
                    var values = new string[8];
                    for (var index = 1; index < 9; index++) { values[index - 1] = reader.GetString(index); }
                    var server = reader.GetDouble(0);
                    _writer.Write(writer =>
                    {
                        writer.WriteString("k", "io");
                        writer.WriteNumber("t", DeliveryTrace.Round(_trace.Milliseconds(countersStarted)));
                        writer.WriteNumber("srv", Math.Round(server, 3));
                        writer.WriteString("insertLsn", values[0]);
                        writer.WriteString("flushLsn", values[1]);
                        writer.WritePropertyName("walIo"); writer.WriteRawValue(values[2], skipInputValidation: true);
                        writer.WritePropertyName("relationIo"); writer.WriteRawValue(values[3], skipInputValidation: true);
                        writer.WritePropertyName("checkpointer"); writer.WriteRawValue(values[4], skipInputValidation: true);
                        writer.WritePropertyName("wal"); writer.WriteRawValue(values[5], skipInputValidation: true);
                        writer.WritePropertyName("replication"); writer.WriteRawValue(values[6], skipInputValidation: true);
                        writer.WritePropertyName("slots"); writer.WriteRawValue(values[7], skipInputValidation: true);
                    });
                }
                if (tick % 600 == 1)
                {
                    var offset = await DeliveryTrace.MeasureServerOffsetAsync(connection, token);
                    _writer.Write(writer =>
                    {
                        writer.WriteString("k", "clock");
                        writer.WriteNumber("t", DeliveryTrace.Round(_trace.Milliseconds(Stopwatch.GetTimestamp())));
                        writer.WriteString("utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                        writer.WriteNumber("serverOffsetMs", DeliveryTrace.Round(offset.OffsetMilliseconds));
                        writer.WriteNumber("offsetRttMs", DeliveryTrace.Round(offset.RoundTripMilliseconds));
                    });
                }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Best-effort diagnostics never fail the measured campaign (for example after a physical
            // promotion kills the sampled primary). Only the exception type is recorded.
            _writer.Write(writer => { writer.WriteString("k", "error"); writer.WriteString("type", exception.GetType().Name); });
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { await _sampling; }
        finally { _stop.Dispose(); await _writer.DisposeAsync(); await _source.DisposeAsync(); }
    }
}
