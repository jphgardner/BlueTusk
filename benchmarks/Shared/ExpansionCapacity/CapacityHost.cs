using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BlueTusk.Data;

namespace BlueTusk.Benchmarks.ExpansionCapacity;

// Shared exact-candidate capacity host for the Events, Schema, Sql and Studio harnesses. Each
// family supplies a workload; this host owns the fixed offered-rate schedule, raw per-operation
// samples, resource sampling and the report shape that eng/expansion-capacity-evidence.psm1
// re-verifies. Budgets are never evaluated here: the report records what happened.
internal enum OperationOutcome
{
    Accepted,
    Rejected,
    Error,
}

internal sealed record LatencySummary(long Count, double P50Milliseconds, double P95Milliseconds,
    double P99Milliseconds, double MaximumMilliseconds);

internal sealed record OperationReport(string Name, int Workers, int OfferIntervalMilliseconds, long PlannedSlots,
    long Offered, long Accepted, long Rejected, long Errors, long ScheduleSkipped,
    LatencySummary? Latency, LatencySummary? RejectedLatency, string[] ErrorTypes);

internal sealed record SeriesReport(string Name, LatencySummary Latency);

internal sealed record ResourceSample(double ElapsedSeconds, long OwnedRelationBytes, long DatabaseBytes,
    long WalInsertBytes, long DatabaseBackends, double ProcessCpuMilliseconds, long WorkingSetBytes,
    long ManagedHeapBytes, long AllocatedBytes, int Gen2Collections);

internal sealed record InvariantResult(string Name, bool Passed, string Detail);

internal sealed record RawSampleFile(string Name, string Sha256, long Rows);

internal sealed record CapacityReport(int FormatVersion, string Family, string Workload,
    string CandidateSha, string SourceTreeSha256, string HarnessBinarySha256,
    string PostgreSqlImage, string PostgreSqlVersion, string FixtureOwner, string FixtureRunKind, string FixtureRunId,
    string OperatingSystem, string Runtime, int ProcessorCount,
    DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc,
    int DurationSeconds, double MeasuredSeconds, double DrainSeconds, int SampleIntervalSeconds,
    SortedDictionary<string, long> Parameters, OperationReport[] Operations, SeriesReport[] Series,
    SortedDictionary<string, long> Counters, SortedDictionary<string, double> Metrics,
    InvariantResult[] Invariants, ResourceSample[] ResourceSamples, ResourceSample AfterDrain,
    RawSampleFile RawSamples, bool ProductionQualified, bool Passed);

internal sealed record CapacityFailure(bool Passed, bool ProductionQualified, DateTimeOffset FailedUtc,
    string Family, string FailureType, string? FailureStack, string? CandidateSha, string? SourceTreeSha256);

[JsonSerializable(typeof(CapacityReport))]
[JsonSerializable(typeof(CapacityFailure))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class CapacityJson : JsonSerializerContext;

// Thrown when the product returned a wrong, stale, cross-tenant or otherwise incorrect result.
// It aborts the campaign: a capacity number measured over incorrect results is not evidence.
internal sealed class CapacityInvariantException(string message) : Exception(message);

internal sealed record OperationSchedule(int Workers, int OfferIntervalMilliseconds);

internal sealed class CapacityContext
{
    public required string Family { get; init; }
    public required string Workload { get; init; }
    public required string ConnectionString { get; init; }
    public required int Seconds { get; init; }
    public required int SampleIntervalSeconds { get; init; }
    public required IReadOnlyDictionary<string, long> Parameters { get; init; }
    public required IReadOnlyDictionary<string, OperationSchedule> Schedules { get; init; }

    public long Parameter(string name) =>
        Parameters.TryGetValue(name, out var value) ? value : throw new InvalidOperationException($"Budget parameter '{name}' is missing.");

    public int IntParameter(string name)
    {
        var value = Parameter(name);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value : throw new InvalidOperationException($"Budget parameter '{name}' is out of range.");
    }

    public OperationSchedule Schedule(string name) =>
        Schedules.TryGetValue(name, out var value) ? value : throw new InvalidOperationException($"Budget operation '{name}' is missing.");
}

internal delegate ValueTask<OperationOutcome> OperationBody(int worker, long slot, CancellationToken cancellationToken);

internal sealed record ScheduledOperation(string Name, OperationBody Execute);

internal abstract class CapacityWorkload : IAsyncDisposable
{
    // Schemas whose relations are the product-owned storage measured by the resource sampler.
    public abstract IReadOnlyList<string> OwnedSchemas { get; }

    public abstract IReadOnlyList<ScheduledOperation> Operations { get; }

    // Optional continuous work that runs beside the offered schedule (for example consumers).
    public virtual Task RunBackgroundAsync(CapacityRecorder recorder, CancellationToken offerEnded, CancellationToken abort) =>
        Task.CompletedTask;

    // Called after every offered slot completed: drain, verify exact final state and record
    // counters, metrics and invariants. Throwing marks the campaign as failed.
    public abstract Task DrainAndVerifyAsync(CapacityRecorder recorder, CancellationToken cancellationToken);

    public abstract ValueTask DisposeAsync();
}

internal readonly record struct RawSample(int Kind, int Name, int Worker, long Slot, long StartMicroseconds,
    long LatencyMicroseconds, OperationOutcome Outcome);

internal sealed class CapacityRecorder
{
    internal const long MaximumRawSamples = 6_000_000;
    private readonly Stopwatch _clock;
    private readonly ConcurrentDictionary<string, ConcurrentQueue<RawSample>> _series = new(StringComparer.Ordinal);
    private readonly List<string> _seriesNames = [];
    private readonly SortedDictionary<string, long> _counters = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, double> _metrics = new(StringComparer.Ordinal);
    private readonly List<InvariantResult> _invariants = [];
    private long _rows;

    internal CapacityRecorder(Stopwatch clock) => _clock = clock;

    internal long ElapsedMicroseconds => ToMicroseconds(_clock.Elapsed.Ticks);

    internal static long ToMicroseconds(long ticks) => ticks / (TimeSpan.TicksPerMillisecond / 1000);

    internal void CountRow()
    {
        if (Interlocked.Increment(ref _rows) > MaximumRawSamples)
        {
            throw new InvalidOperationException("The bounded raw sample capacity was exceeded.");
        }
    }

    // Records one latency observation of a non-scheduled series, such as commit-to-delivery.
    public void RecordSeries(string name, int worker, long slot, long startMicroseconds, long latencyMicroseconds)
    {
        if (latencyMicroseconds < 0 || startMicroseconds < 0)
        {
            throw new InvalidOperationException("A series sample is negative.");
        }

        CountRow();
        var queue = _series.GetOrAdd(name, static _ => new ConcurrentQueue<RawSample>());
        queue.Enqueue(new RawSample(1, 0, worker, slot, startMicroseconds, latencyMicroseconds, OperationOutcome.Accepted));
    }

    public void Counter(string name, long value)
    {
        lock (_counters) { _counters[name] = value; }
    }

    public void Metric(string name, double value)
    {
        if (!double.IsFinite(value)) { throw new InvalidOperationException($"Metric '{name}' is not finite."); }
        lock (_metrics) { _metrics[name] = value; }
    }

    public void Invariant(string name, bool passed, string detail)
    {
        lock (_invariants) { _invariants.Add(new InvariantResult(name, passed, detail)); }
    }

    internal IReadOnlyList<string> SeriesNames
    {
        get
        {
            lock (_seriesNames)
            {
                _seriesNames.Clear();
                _seriesNames.AddRange(_series.Keys.Order(StringComparer.Ordinal));
                return [.. _seriesNames];
            }
        }
    }

    internal RawSample[] Series(string name) =>
        [.. _series[name].OrderBy(static item => item.Worker).ThenBy(static item => item.Slot).ThenBy(static item => item.StartMicroseconds)];

    internal SortedDictionary<string, long> Counters { get { lock (_counters) { return new(_counters, StringComparer.Ordinal); } } }

    internal SortedDictionary<string, double> Metrics { get { lock (_metrics) { return new(_metrics, StringComparer.Ordinal); } } }

    internal InvariantResult[] Invariants { get { lock (_invariants) { return [.. _invariants]; } } }
}

internal static partial class CapacityHost
{
    internal const string ReportFileName = "capacity.json";
    internal const string SamplesFileName = "samples.csv";
    internal const string FailureFileName = "failure.json";

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaName();

    [GeneratedRegex("^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex OperationName();

    internal static void Check(bool condition, string message)
    {
        if (!condition) { throw new CapacityInvariantException(message); }
    }

    internal static async Task<int> RunAsync(string[] args, string family,
        Func<CapacityContext, CancellationToken, Task<CapacityWorkload>> createWorkload)
    {
        if (args.Length != 3 || !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            seconds is < 10 or > 7200)
        {
            Console.Error.WriteLine("Usage: <seconds 10..7200> <budget-path> <run-directory>.");
            return 2;
        }

        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_CAPACITY_CONNECTION_STRING");
        var candidate = Environment.GetEnvironmentVariable("BLUETUSK_CAPACITY_COMMIT");
        var sourceHash = Environment.GetEnvironmentVariable("BLUETUSK_CAPACITY_SOURCE_SHA256");
        var binaryHash = Environment.GetEnvironmentVariable("BLUETUSK_CAPACITY_BINARY_SHA256");
        var image = Environment.GetEnvironmentVariable("BLUETUSK_CAPACITY_IMAGE");
        var fixtureOwner = Environment.GetEnvironmentVariable("BLUETUSK_CAPACITY_FIXTURE_OWNER");
        var fixtureRunKind = Environment.GetEnvironmentVariable("BLUETUSK_CAPACITY_FIXTURE_RUN_KIND");
        var fixtureRunId = Environment.GetEnvironmentVariable("BLUETUSK_CAPACITY_FIXTURE_RUN_ID");
        if (string.IsNullOrWhiteSpace(connectionString) || candidate?.Length != 40 || sourceHash?.Length != 64 ||
            binaryHash?.Length != 64 || string.IsNullOrWhiteSpace(image) || string.IsNullOrWhiteSpace(fixtureOwner) ||
            fixtureRunKind is not ("github" or "local") || string.IsNullOrWhiteSpace(fixtureRunId))
        {
            Console.Error.WriteLine("The disposable fixture connection and exact candidate provenance are required.");
            return 2;
        }

        var runDirectory = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(runDirectory);
        var started = DateTimeOffset.UtcNow;
        try
        {
            var executingBinary = typeof(CapacityHost).Assembly.Location;
            Check(string.Equals(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(executingBinary))), binaryHash,
                StringComparison.OrdinalIgnoreCase), "The executing harness binary differs from the captured candidate.");
            var context = ReadBudget(args[1], family, seconds, connectionString);
            var observerSettings = new BlueTuskConnectionStringBuilder(connectionString)
            {
                MaximumPoolSize = 2,
                ApplicationName = $"bluetusk-{family.ToLowerInvariant()}-capacity-observer",
            };
            await using var observer = BlueTuskDataSource.Create(observerSettings.ConnectionString);
            var postgreSqlVersion = await ServerVersionAsync(observer);
            await using var workload = await createWorkload(context, CancellationToken.None);
            foreach (var schema in workload.OwnedSchemas)
            {
                Check(SchemaName().IsMatch(schema), "An owned schema name is not a safe identifier.");
            }

            var operations = workload.Operations;
            Check(operations.Count > 0 && operations.Count == context.Schedules.Count &&
                operations.All(item => OperationName().IsMatch(item.Name) && context.Schedules.ContainsKey(item.Name)) &&
                operations.Select(static item => item.Name).Distinct(StringComparer.Ordinal).Count() == operations.Count,
                "The workload operations differ from the budget schedule.");

            var clock = Stopwatch.StartNew();
            var recorder = new CapacityRecorder(clock);
            var process = Process.GetCurrentProcess();
            var samples = new List<ResourceSample> { await SampleAsync(observer, workload.OwnedSchemas, process, 0) };
            using var abort = new CancellationTokenSource();
            using var offerEnded = new CancellationTokenSource();
            var drivers = operations.Select((operation, index) => new OperationDriver(index, operation,
                context.Schedule(operation.Name), seconds, recorder)).ToArray();
            var background = workload.RunBackgroundAsync(recorder, offerEnded.Token, abort.Token);
            var sampler = Task.Run(async () =>
            {
                var next = context.SampleIntervalSeconds;
                while (next <= seconds && !abort.IsCancellationRequested)
                {
                    var wait = next - clock.Elapsed.TotalSeconds;
                    if (wait > 0) { await Task.Delay(TimeSpan.FromSeconds(wait), abort.Token); }
                    var sample = await SampleAsync(observer, workload.OwnedSchemas, process, clock.Elapsed.TotalSeconds);
                    lock (samples) { samples.Add(sample); }
                    if (next % 60 == 0)
                    {
                        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                            $"{family} capacity progress: {sample.ElapsedSeconds:F0}s, accepted {drivers.Sum(static item => item.AcceptedSoFar)}, " +
                            $"rejected {drivers.Sum(static item => item.RejectedSoFar)}, errors {drivers.Sum(static item => item.ErrorsSoFar)}, " +
                            $"owned {sample.OwnedRelationBytes} bytes."));
                    }

                    next += context.SampleIntervalSeconds;
                }
            });
            var workers = drivers.SelectMany(driver => driver.Start(clock, abort)).ToArray();
            try
            {
                await Task.WhenAll(workers);
                offerEnded.Cancel();
                await sampler;
            }
            catch
            {
                await abort.CancelAsync();
                throw;
            }

            var measuredSeconds = clock.Elapsed.TotalSeconds;
            var drain = Stopwatch.StartNew();
            await background;
            await workload.DrainAndVerifyAsync(recorder, CancellationToken.None);
            var drainSeconds = drain.Elapsed.TotalSeconds;
            var afterDrain = await SampleAsync(observer, workload.OwnedSchemas, process, clock.Elapsed.TotalSeconds);
            Check(string.Equals(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(executingBinary))), binaryHash,
                StringComparison.OrdinalIgnoreCase), "The executing harness binary changed during the run.");

            var rawSamples = await WriteSamplesAsync(Path.Combine(runDirectory, SamplesFileName), drivers, recorder);
            var operationReports = drivers.Select(static driver => driver.Report()).ToArray();
            var seriesReports = recorder.SeriesNames.Select(name => new SeriesReport(name,
                Summarize(recorder.Series(name).Select(static item => item.LatencyMicroseconds)) ??
                    throw new InvalidOperationException($"Series '{name}' is empty."))).ToArray();
            var invariants = recorder.Invariants;
            var passed = invariants.Length > 0 && invariants.All(static item => item.Passed);
            ResourceSample[] resourceSamples;
            lock (samples) { resourceSamples = [.. samples]; }
            var report = new CapacityReport(1, family, context.Workload, candidate, sourceHash, binaryHash, image,
                postgreSqlVersion, fixtureOwner, fixtureRunKind, fixtureRunId,
                RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription, Environment.ProcessorCount,
                started, DateTimeOffset.UtcNow, seconds, measuredSeconds, drainSeconds, context.SampleIntervalSeconds,
                new SortedDictionary<string, long>(context.Parameters.ToDictionary(), StringComparer.Ordinal),
                operationReports, seriesReports, recorder.Counters, recorder.Metrics, invariants,
                resourceSamples, afterDrain, rawSamples, false, passed);
            await File.WriteAllTextAsync(Path.Combine(runDirectory, ReportFileName),
                JsonSerializer.Serialize(report, CapacityJson.Default.CapacityReport));
            Console.WriteLine($"{family} capacity workload completed (invariants passed: {passed}). Raw report: {Path.Combine(runDirectory, ReportFileName)}");
            return passed ? 0 : 1;
        }
        catch (Exception error)
        {
            var failure = new CapacityFailure(false, false, DateTimeOffset.UtcNow, family, error.GetType().Name,
                error is CapacityInvariantException ? error.Message + Environment.NewLine + error.StackTrace : error.StackTrace,
                candidate, sourceHash);
            await File.WriteAllTextAsync(Path.Combine(runDirectory, FailureFileName),
                JsonSerializer.Serialize(failure, CapacityJson.Default.CapacityFailure));
            Console.Error.WriteLine($"{family} capacity workload failed ({error.GetType().Name}); no connection string or payload is logged.");
            return 1;
        }
    }

    private static CapacityContext ReadBudget(string path, string family, int seconds, string connectionString)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        Check(root.GetProperty("schemaVersion").GetInt32() == 1 &&
            string.Equals(root.GetProperty("family").GetString(), family, StringComparison.Ordinal),
            "The budget contract belongs to another family or schema.");
        var parameters = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var item in root.GetProperty("parameters").EnumerateObject())
        {
            parameters.Add(item.Name, item.Value.GetInt64());
        }

        var schedules = new Dictionary<string, OperationSchedule>(StringComparer.Ordinal);
        foreach (var item in root.GetProperty("operations").EnumerateObject())
        {
            var workers = item.Value.GetProperty("workers").GetInt32();
            var interval = item.Value.GetProperty("offerIntervalMilliseconds").GetInt32();
            Check(workers is >= 1 and <= 64 && interval is >= 1 and <= 60_000 && seconds * 1000L % interval == 0,
                $"Operation '{item.Name}' has an invalid fixed schedule.");
            schedules.Add(item.Name, new OperationSchedule(workers, interval));
        }

        var sampleInterval = root.GetProperty("sampleIntervalSeconds").GetInt32();
        Check(sampleInterval is >= 1 and <= 60, "The resource sample interval is invalid.");
        return new CapacityContext
        {
            Family = family,
            Workload = root.GetProperty("workload").GetString() ?? throw new InvalidOperationException("Workload is missing."),
            ConnectionString = connectionString,
            Seconds = seconds,
            SampleIntervalSeconds = sampleInterval,
            Parameters = parameters,
            Schedules = schedules,
        };
    }

    // Nearest-rank percentiles over integral microseconds; the verifier recomputes the same values
    // from samples.csv, so the summary cannot drift from the raw observations.
    internal static LatencySummary? Summarize(IEnumerable<long> microseconds)
    {
        var sorted = microseconds.ToArray();
        if (sorted.Length == 0) { return null; }
        Array.Sort(sorted);
        static double At(long[] rows, double fraction) =>
            rows[Math.Max(0, (int)Math.Ceiling(rows.Length * fraction) - 1)] / 1000.0;
        return new LatencySummary(sorted.Length, At(sorted, 0.5), At(sorted, 0.95), At(sorted, 0.99), sorted[^1] / 1000.0);
    }

    private static async Task<RawSampleFile> WriteSamplesAsync(string path, OperationDriver[] drivers, CapacityRecorder recorder)
    {
        long rows = 0;
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" })
        {
            await writer.WriteLineAsync("kind,name,worker,slot,start_us,latency_us,outcome");
            foreach (var driver in drivers)
            {
                foreach (var sample in driver.Samples())
                {
                    await writer.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                        $"operation,{driver.Name},{sample.Worker},{sample.Slot},{sample.StartMicroseconds},{sample.LatencyMicroseconds},{Outcome(sample.Outcome)}"));
                    rows++;
                }
            }

            foreach (var name in recorder.SeriesNames)
            {
                foreach (var sample in recorder.Series(name))
                {
                    await writer.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                        $"series,{name},{sample.Worker},{sample.Slot},{sample.StartMicroseconds},{sample.LatencyMicroseconds},accepted"));
                    rows++;
                }
            }
        }

        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant();
        return new RawSampleFile(SamplesFileName, hash, rows);
    }

    internal static string Outcome(OperationOutcome outcome) => outcome switch
    {
        OperationOutcome.Accepted => "accepted",
        OperationOutcome.Rejected => "rejected",
        _ => "error",
    };

    private static async Task<ResourceSample> SampleAsync(BlueTuskDataSource source, IReadOnlyList<string> schemas,
        Process process, double elapsed)
    {
        var owned = string.Join(",", schemas.Select(static schema => $"'{schema}'"));
        await using var connection = await source.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT pg_wal_lsn_diff(pg_current_wal_insert_lsn(),'0/0')::bigint,
                   pg_database_size(current_database()),
                   coalesce((SELECT sum(pg_total_relation_size(c.oid)) FROM pg_class c
                       JOIN pg_namespace n ON n.oid=c.relnamespace
                       WHERE n.nspname IN ({owned}) AND c.relkind IN ('r','p','m')),0)::bigint,
                   (SELECT count(*) FROM pg_stat_activity WHERE datname=current_database())::bigint
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Check(await reader.ReadAsync(), "The physical observation is missing.");
        process.Refresh();
        return new ResourceSample(elapsed, reader.GetInt64(2), reader.GetInt64(1), reader.GetInt64(0), reader.GetInt64(3),
            process.TotalProcessorTime.TotalMilliseconds, process.WorkingSet64, GC.GetTotalMemory(false),
            GC.GetTotalAllocatedBytes(), GC.CollectionCount(2));
    }

    private static async Task<string> ServerVersionAsync(BlueTuskDataSource source)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version()";
        return (string)(await command.ExecuteScalarAsync())!;
    }
}

// Drives one operation at a fixed offered rate: each worker owns planned slots every interval.
// A worker that falls behind skips the missed slots and counts them, so the offered load never
// silently shrinks into a closed loop.
internal sealed class OperationDriver
{
    private readonly int _index;
    private readonly ScheduledOperation _operation;
    private readonly OperationSchedule _schedule;
    private readonly int _seconds;
    private readonly CapacityRecorder _recorder;
    private readonly List<RawSample>[] _samples;
    private readonly long[] _skipped;
    private readonly ConcurrentDictionary<string, long> _errorTypes = new(StringComparer.Ordinal);
    private long _accepted;
    private long _rejected;
    private long _errors;

    internal OperationDriver(int index, ScheduledOperation operation, OperationSchedule schedule, int seconds, CapacityRecorder recorder)
    {
        _index = index;
        _operation = operation;
        _schedule = schedule;
        _seconds = seconds;
        _recorder = recorder;
        _samples = [.. Enumerable.Range(0, schedule.Workers).Select(static _ => new List<RawSample>())];
        _skipped = new long[schedule.Workers];
    }

    internal string Name => _operation.Name;

    internal long PlannedSlotsPerWorker => _seconds * 1000L / _schedule.OfferIntervalMilliseconds;

    internal long AcceptedSoFar => Interlocked.Read(ref _accepted);

    internal long RejectedSoFar => Interlocked.Read(ref _rejected);

    internal long ErrorsSoFar => Interlocked.Read(ref _errors);

    internal IEnumerable<Task> Start(Stopwatch clock, CancellationTokenSource abort) =>
        Enumerable.Range(0, _schedule.Workers).Select(worker => Task.Run(() => RunWorkerAsync(worker, clock, abort)));

    private async Task RunWorkerAsync(int worker, Stopwatch clock, CancellationTokenSource abort)
    {
        var interval = _schedule.OfferIntervalMilliseconds / 1000.0;
        var planned = PlannedSlotsPerWorker;
        // Workers of one operation start evenly spread inside the first interval.
        var phase = interval * worker / _schedule.Workers;
        var samples = _samples[worker];
        for (long next = 0; next < planned;)
        {
            var due = phase + next * interval;
            var wait = due - clock.Elapsed.TotalSeconds;
            if (wait > 0) { await Task.Delay(TimeSpan.FromSeconds(wait), abort.Token); }
            if (clock.Elapsed.TotalSeconds >= _seconds)
            {
                _skipped[worker] += planned - next;
                break;
            }

            var behind = (long)Math.Floor((clock.Elapsed.TotalSeconds - due) / interval);
            var skipped = Math.Clamp(behind, 0, planned - next - 1);
            if (skipped > 0)
            {
                _skipped[worker] += skipped;
                next += skipped;
            }

            var slot = next++;
            _recorder.CountRow();
            var began = Stopwatch.GetTimestamp();
            var startMicroseconds = _recorder.ElapsedMicroseconds;
            OperationOutcome outcome;
            try
            {
                outcome = await _operation.Execute(worker, slot, abort.Token);
            }
            catch (CapacityInvariantException)
            {
                await abort.CancelAsync();
                throw;
            }
            catch (OperationCanceledException) when (abort.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                outcome = OperationOutcome.Error;
                // Type names and HTTP status codes only: messages may carry fixture details.
                var type = error is HttpRequestException { StatusCode: { } status }
                    ? string.Create(CultureInfo.InvariantCulture, $"{error.GetType().Name}:{(int)status}")
                    : error.GetType().Name;
                _errorTypes.AddOrUpdate(type, 1, static (_, count) => count + 1);
            }

            var latency = CapacityRecorder.ToMicroseconds(Stopwatch.GetElapsedTime(began).Ticks);
            samples.Add(new RawSample(0, _index, worker, slot, startMicroseconds, latency, outcome));
            _ = outcome switch
            {
                OperationOutcome.Accepted => Interlocked.Increment(ref _accepted),
                OperationOutcome.Rejected => Interlocked.Increment(ref _rejected),
                _ => Interlocked.Increment(ref _errors),
            };
        }
    }

    internal IEnumerable<RawSample> Samples() => _samples.SelectMany(static list => list);

    internal OperationReport Report()
    {
        var all = Samples().ToArray();
        var accepted = all.Where(static item => item.Outcome == OperationOutcome.Accepted).Select(static item => item.LatencyMicroseconds);
        var rejected = all.Where(static item => item.Outcome == OperationOutcome.Rejected).Select(static item => item.LatencyMicroseconds);
        return new OperationReport(Name, _schedule.Workers, _schedule.OfferIntervalMilliseconds,
            PlannedSlotsPerWorker * _schedule.Workers, all.LongLength,
            all.LongCount(static item => item.Outcome == OperationOutcome.Accepted),
            all.LongCount(static item => item.Outcome == OperationOutcome.Rejected),
            all.LongCount(static item => item.Outcome == OperationOutcome.Error),
            _skipped.Sum(), CapacityHost.Summarize(accepted), CapacityHost.Summarize(rejected),
            [.. _errorTypes.Keys.Order(StringComparer.Ordinal)]);
    }
}
