using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using BlueTusk.Data;

namespace BlueTusk.Projections.LoadHarness;

/// <summary>
/// Diagnostic JSONL trace of the serial WAL delivery loop. One line is written per delivered
/// transaction with receive/decode/apply/process/lease phase timings, attributed provider pool
/// checkouts, pool waits, resets and command round trips. After the drain one line is written per
/// accepted operation with its offered, committed, projected, inbox and Live timestamps.
/// Times are milliseconds since the trace anchor on the monotonic Stopwatch clock. The header and
/// trailer carry UTC anchors and measured server-clock offsets so lines can be correlated with
/// PostgreSQL log timestamps. SQL text, parameters, connection strings and payloads are never written.
/// The trace is additive: report fields and verifier inputs are unchanged.
/// </summary>
internal sealed class DeliveryTrace : IAsyncDisposable
{
    internal sealed class Phase
    {
        internal int Checkouts, PoolWaits, Resets, Commands, Messages;
        internal double CheckoutMilliseconds, PoolWaitMilliseconds, CommandMilliseconds, CommandMaximumMilliseconds, AckMilliseconds;
        internal double LastReceiveLagMilliseconds = double.NaN;
        internal long LastReceiveAt, PoolWaitStarted;
    }

    private static readonly AsyncLocal<Phase?> CurrentPhase = new();
    private readonly JsonlWriter _writer;
    private readonly MeterListener _listener = new();
    private readonly long _anchor = Stopwatch.GetTimestamp();
    private readonly DateTime _anchorUtc = DateTime.UtcNow;

    private DeliveryTrace(string path)
    {
        _writer = new JsonlWriter(path);
        _listener.InstrumentPublished = static (instrument, listener) =>
        {
            if (instrument.Meter.Name == BlueTusk.Diagnostics.BlueTuskDiagnostics.InstrumentationName &&
                instrument.Name is "db.client.operation.duration" or "bluetusk.pool.checkout.duration" or "bluetusk.pool.waiters"
                    or "bluetusk.pool.resets" or "bluetusk.replication.receive_lag")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<double>(static (instrument, value, _, _) =>
        {
            var phase = CurrentPhase.Value;
            if (phase is null) { return; }
            switch (instrument.Name)
            {
                case "db.client.operation.duration":
                    var command = value * 1000;
                    phase.Commands++; phase.CommandMilliseconds += command;
                    phase.CommandMaximumMilliseconds = Math.Max(phase.CommandMaximumMilliseconds, command);
                    break;
                case "bluetusk.pool.checkout.duration":
                    phase.Checkouts++; phase.CheckoutMilliseconds += value * 1000;
                    break;
                case "bluetusk.replication.receive_lag":
                    phase.Messages++; phase.LastReceiveLagMilliseconds = value * 1000; phase.LastReceiveAt = Stopwatch.GetTimestamp();
                    break;
            }
        });
        _listener.SetMeasurementEventCallback<long>(static (instrument, value, _, _) =>
        {
            var phase = CurrentPhase.Value;
            if (phase is null) { return; }
            if (instrument.Name == "bluetusk.pool.resets") { phase.Resets += (int)value; return; }
            if (instrument.Name != "bluetusk.pool.waiters") { return; }
            if (value > 0) { phase.PoolWaits++; phase.PoolWaitStarted = Stopwatch.GetTimestamp(); }
            else if (phase.PoolWaitStarted != 0)
            {
                phase.PoolWaitMilliseconds += Stopwatch.GetElapsedTime(phase.PoolWaitStarted).TotalMilliseconds;
                phase.PoolWaitStarted = 0;
            }
        });
        _listener.Start();
    }

    internal static string PathFor(string reportPath, string scenario, string suffix) =>
        Path.Combine(Path.GetDirectoryName(reportPath)!, Path.GetFileNameWithoutExtension(reportPath) + "-" + scenario + "-" + suffix + ".jsonl");

    internal static async Task<DeliveryTrace> StartAsync(string path, BlueTuskDataSource source, LoadCase configuration, CancellationToken token)
    {
        var trace = new DeliveryTrace(path);
        var offset = await MeasureServerOffsetAsync(source, token);
        trace.Write(writer =>
        {
            writer.WriteString("k", "header");
            writer.WriteNumber("schema", 1);
            writer.WriteString("scenario", configuration.Name);
            writer.WriteString("anchorUtc", trace._anchorUtc.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteNumber("stopwatchFrequency", Stopwatch.Frequency);
            writer.WriteNumber("processId", Environment.ProcessId);
            WriteOffset(writer, offset);
        });
        return trace;
    }

    /// <summary>Begin attributing provider measurements on the current async flow to a new phase.</summary>
    internal static Phase Begin()
    {
        var phase = new Phase();
        CurrentPhase.Value = phase;
        return phase;
    }

    internal static void End() => CurrentPhase.Value = null;

    internal static void RecordAck(TimeSpan elapsed)
    {
        if (CurrentPhase.Value is { } phase) { phase.AckMilliseconds += elapsed.TotalMilliseconds; }
    }

    internal double Milliseconds(long timestamp) => timestamp == 0 ? double.NaN : Stopwatch.GetElapsedTime(_anchor, timestamp).TotalMilliseconds;

    internal void Write(Action<Utf8JsonWriter> body) => _writer.Write(body);

    internal static void WritePhase(Utf8JsonWriter writer, string name, Phase? phase, long started, long ended)
    {
        if (phase is null) { return; }
        writer.WriteStartObject(name);
        writer.WriteNumber("ms", Round(Stopwatch.GetElapsedTime(started, ended).TotalMilliseconds));
        writer.WriteNumber("co", phase.Checkouts);
        writer.WriteNumber("coMs", Round(phase.CheckoutMilliseconds));
        writer.WriteNumber("pw", phase.PoolWaits);
        writer.WriteNumber("pwMs", Round(phase.PoolWaitMilliseconds));
        writer.WriteNumber("rs", phase.Resets);
        writer.WriteNumber("cmd", phase.Commands);
        writer.WriteNumber("cmdMs", Round(phase.CommandMilliseconds));
        writer.WriteNumber("cmdMax", Round(phase.CommandMaximumMilliseconds));
        if (phase.AckMilliseconds > 0) { writer.WriteNumber("ackMs", Round(phase.AckMilliseconds)); }
        writer.WriteEndObject();
    }

    internal static double Round(double value) => double.IsFinite(value) ? Math.Round(value, 3) : -1;

    internal async Task CompleteAsync(BlueTuskDataSource source, Action<Utf8JsonWriter> summary, CancellationToken token)
    {
        var offset = await MeasureServerOffsetAsync(source, token);
        Write(writer =>
        {
            writer.WriteString("k", "trailer");
            writer.WriteString("utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteNumber("at", Round(Milliseconds(Stopwatch.GetTimestamp())));
            WriteOffset(writer, offset);
            summary(writer);
        });
    }

    private static void WriteOffset(Utf8JsonWriter writer, (double OffsetMilliseconds, double RoundTripMilliseconds) offset)
    {
        // Positive offset means the PostgreSQL clock is ahead of this host's UTC clock.
        writer.WriteNumber("serverOffsetMs", Round(offset.OffsetMilliseconds));
        writer.WriteNumber("offsetRttMs", Round(offset.RoundTripMilliseconds));
    }

    internal static async Task<(double OffsetMilliseconds, double RoundTripMilliseconds)> MeasureServerOffsetAsync(BlueTuskDataSource source, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        return await MeasureServerOffsetAsync(connection, token);
    }

    internal static async Task<(double OffsetMilliseconds, double RoundTripMilliseconds)> MeasureServerOffsetAsync(System.Data.Common.DbConnection connection, CancellationToken token)
    {
        await using var command = Sql.Command(connection, null, "SELECT extract(epoch FROM clock_timestamp())::float8 * 1000");
        var best = (OffsetMilliseconds: double.NaN, RoundTripMilliseconds: double.MaxValue);
        for (var attempt = 0; attempt < 7; attempt++)
        {
            var before = DateTime.UtcNow;
            var server = Convert.ToDouble(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
            var after = DateTime.UtcNow;
            var roundTrip = (after - before).TotalMilliseconds;
            var middle = (before + (after - before) / 2 - DateTime.UnixEpoch).TotalMilliseconds;
            if (roundTrip < best.RoundTripMilliseconds) { best = (server - middle, roundTrip); }
        }
        return best;
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Dispose();
        await _writer.DisposeAsync();
    }
}

/// <summary>Bounded-latency background JSONL writer; one complete JSON object per line.</summary>
internal sealed class JsonlWriter : IAsyncDisposable
{
    private readonly Channel<byte[]> _lines = Channel.CreateUnbounded<byte[]>(new() { SingleReader = true });
    private readonly FileStream _file;
    private readonly Task _pump;

    internal JsonlWriter(string path)
    {
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, useAsync: true);
        _pump = PumpAsync();
    }

    internal void Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }
        buffer.Write("\n"u8);
        _lines.Writer.TryWrite(buffer.WrittenSpan.ToArray());
    }

    private async Task PumpAsync()
    {
        var lastFlush = Stopwatch.GetTimestamp();
        while (await _lines.Reader.WaitToReadAsync())
        {
            while (_lines.Reader.TryRead(out var line)) { await _file.WriteAsync(line); }
            if (Stopwatch.GetElapsedTime(lastFlush) > TimeSpan.FromSeconds(1)) { await _file.FlushAsync(); lastFlush = Stopwatch.GetTimestamp(); }
        }
        await _file.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _lines.Writer.TryComplete();
        try { await _pump; }
        finally { await _file.DisposeAsync(); }
    }
}
