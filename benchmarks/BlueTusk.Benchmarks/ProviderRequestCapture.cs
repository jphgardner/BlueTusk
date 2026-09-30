using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlueTusk.Benchmarks;

/// <summary>A single-provider process capture; comparisons are assembled outside this process.</summary>
internal static class ProviderRequestCapture
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    internal sealed record Options
    {
        public required string Provider { get; init; }
        public required string Feature { get; init; }
        public required int Concurrency { get; init; }
        public required double WarmupSeconds { get; init; }
        public required double MeasurementSeconds { get; init; }
        public required int MaximumSamplesPerWorker { get; init; }
        public required bool RequireTls { get; init; }
        public required string SourceCommit { get; init; }
        public required string PostgreSqlImage { get; init; }
        public required string OutputPath { get; init; }
        public required bool Diagnostic { get; init; }

        public void Validate()
        {
            if (Provider is not ("bluetusk" or "npgsql") ||
                !ProviderRequestFixture.CaptureFeatures.Contains(Feature, StringComparer.Ordinal) ||
                Concurrency is < 1 or > 256 ||
                !double.IsFinite(WarmupSeconds) || WarmupSeconds is < 0.1 or > 300 ||
                !double.IsFinite(MeasurementSeconds) || MeasurementSeconds is < 0.1 or > 300 ||
                MaximumSamplesPerWorker is < 1 or > 16_000_000 ||
                (long)Concurrency * MaximumSamplesPerWorker > 32_000_000 ||
                !Regex.IsMatch(SourceCommit, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant) ||
                !Regex.IsMatch(PostgreSqlImage, "^postgres:[^@\\s]+@sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant))
            {
                throw new ArgumentException("Invalid provider capture options; use bounded windows, samples, concurrency, and immutable source/image identities.");
            }
            ArgumentException.ThrowIfNullOrWhiteSpace(OutputPath);
            if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) ||
                RuntimeInformation.ProcessArchitecture != Architecture.X64)
            {
                throw new PlatformNotSupportedException("The release capture matrix requires Windows x64 or Linux x64.");
            }
        }
    }

    internal sealed record WorkerSamples(long[] RequestTicks, int Count);

    internal sealed record Window(
        long ElapsedTicks,
        long CompletedOperations,
        long AllocatedBytes,
        double CpuMilliseconds,
        long PeakWorkingSetBytes,
        int[] GcCollections,
        WorkerSamples[] Workers);

    public static async Task RunAsync(string optionsPath)
    {
        var options = JsonSerializer.Deserialize<Options>(await File.ReadAllTextAsync(optionsPath), JsonOptions)
            ?? throw new ArgumentException("Capture options are empty.");
        options.Validate();
        var outputPath = Path.GetFullPath(options.OutputPath);
        if (File.Exists(outputPath))
        {
            throw new IOException("Capture output already exists; evidence must not be overwritten.");
        }
        var connectionString = Environment.GetEnvironmentVariable(
            ProviderComparisonBenchmarks.ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("BLUETUSK_BENCHMARK_CONNECTION_STRING must target a dedicated benchmark database.");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(
            options.WarmupSeconds + options.MeasurementSeconds + 180));
        await using var fixture = await ProviderRequestFixture.CreateAsync(options, connectionString, timeout.Token);
        var workers = new List<ProviderRequestWorker>();
        var failures = new List<Exception>();
        try
        {
            for (var index = 0; index < options.Concurrency; index++)
            {
                workers.Add(await ProviderRequestWorker.CreateAsync(fixture, index, timeout.Token));
            }
            Func<CancellationToken, Task>[] operations = workers.Select(worker =>
                (Func<CancellationToken, Task>)worker.ExecuteCheckedAsync).ToArray();
            _ = await MeasureWindowAsync(operations, options.WarmupSeconds, 0, timeout.Token);
            var measurement = await MeasureWindowAsync(
                operations, options.MeasurementSeconds, options.MaximumSamplesPerWorker, timeout.Token);
            var report = new
            {
                SchemaVersion = 1,
                EvidenceKind = "provider-individual-request-capture",
                options.Diagnostic,
                options.SourceCommit,
                CapturedUtc = DateTimeOffset.UtcNow,
                Provider = options.Provider,
                options.Feature,
                options.Concurrency,
                options.WarmupSeconds,
                options.MeasurementSeconds,
                options.MaximumSamplesPerWorker,
                Environment = new
                {
                    Os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" : "linux",
                    OsDescription = RuntimeInformation.OSDescription,
                    Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                    Runtime = RuntimeInformation.FrameworkDescription,
                    Environment.ProcessorCount,
                    options.PostgreSqlImage,
                    fixture.ServerVersion,
                    fixture.TlsActive,
                    fixture.CertificatePolicy,
                    fixture.PoolSize,
                    fixture.MultiplexingConfigured,
                    CandidateAssembly = typeof(BlueTusk.Data.BlueTuskConnection).Assembly
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                    ReferenceAssembly = typeof(Npgsql.NpgsqlConnection).Assembly
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                    HarnessAssembly = typeof(ProviderRequestCapture).Assembly
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                },
                Method = new
                {
                    LoadModel = "closed-loop; one outstanding request per worker; shared provider pool",
                    Latency = "individual complete operation, including result verification and disposal",
                    Clock = "Stopwatch monotonic ticks",
                    Stopwatch.Frequency,
                    Counters = "process-wide; measurement window plus completion of in-flight requests",
                    Allocations = "includes measurement-loop overhead; sample buffers allocated before counters start",
                    Rss = "client process high-water mark including setup and preallocated sample buffers; database excluded",
                    SampleBufferCapacityBytes = (long)options.MaximumSamplesPerWorker * options.Concurrency * sizeof(long),
                    Fairness = "yield to the scheduler every 64 requests, outside individual latency timing; included in throughput and process counters",
                    Durability = fixture.Durability,
                    NetworkShaping = "not configured by this adapter",
                    EfUpdateKeys = "disjoint worker keys; hot-row contention is a separate workload",
                    ContentionProbe = fixture.IsContention,
                    CommandLifetime = fixture.IsContention
                        ? options.Feature.Contains("-reused-", StringComparison.Ordinal)
                            ? "one data-source command per worker; reused without explicit prepare"
                            : "new data-source command per request; disposed before completion"
                        : "original 16-feature adapter contract",
                    RequestCancellation = "explicit cancellation token passed to both drivers; window includes draining in-flight requests",
                    ContentionConfiguration = fixture.IsContention
                        ? "pool=4; timeout=0; typed int4 parameter; BlueTusk multiplexing workers=4, queue=256, pipeline=64, commands-per-lease=65536"
                        : "not a four-slot contention probe",
                },
                Measurement = measurement with
                {
                    Workers = measurement.Workers.Select(samples => new WorkerSamples(
                        samples.RequestTicks.AsSpan(0, samples.Count).ToArray(), samples.Count)).ToArray(),
                },
            };
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(output, report, JsonOptions, timeout.Token);
            Console.WriteLine($"Captured {measurement.CompletedOperations} checked {options.Provider} requests for {options.Feature} at concurrency {options.Concurrency}.");
        }
        catch (Exception error)
        {
            failures.Add(error);
        }
        finally
        {
            foreach (var worker in workers)
            {
                try { await worker.DisposeAsync(); }
                catch (Exception error) { failures.Add(error); }
            }
        }
        if (failures.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        if (failures.Count > 1)
        {
            throw new AggregateException("Capture or worker cleanup failed.", failures);
        }
    }

    internal static async Task<Window> MeasureWindowAsync(
        IReadOnlyList<Func<CancellationToken, Task>> operations,
        double seconds,
        int capacityPerWorker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count is < 1 or > 256 || !double.IsFinite(seconds) || seconds <= 0 ||
            capacityPerWorker < 0 || (long)capacityPerWorker * operations.Count > 32_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(operations), "The measurement must have bounded workers, samples, and a positive finite window.");
        }
        using var failureCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = failureCancellation.Token;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = operations.Select(_ => new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var buffers = operations.Select(_ => new long[capacityPerWorker]).ToArray();
        var counts = new long[operations.Count];
        long deadline = 0;
        var tasks = operations.Select((operation, index) => Task.Run(async () =>
        {
            ready[index].SetResult();
            try
            {
                await start.Task.WaitAsync(token);
                while (Stopwatch.GetTimestamp() < deadline)
                {
                    token.ThrowIfCancellationRequested();
                    if (capacityPerWorker != 0 && counts[index] == capacityPerWorker)
                    {
                        throw new InvalidOperationException("Raw sample capacity exhausted; increase the explicit bound. No truncated capture is accepted.");
                    }
                    var began = Stopwatch.GetTimestamp();
                    await operation(token);
                    var elapsed = Stopwatch.GetTimestamp() - began;
                    if (elapsed <= 0)
                    {
                        throw new InvalidOperationException("The timer could not resolve a request; zero-duration samples are not accepted.");
                    }
                    if (capacityPerWorker != 0)
                    {
                        buffers[index][checked((int)counts[index])] = elapsed;
                    }
                    counts[index]++;
                    // Cached checkout/empty transactions can complete synchronously. Without
                    // periodic yielding, the first few workers monopolize thread-pool threads
                    // and the nominal concurrency is not actually exercised.
                    if ((counts[index] & 63) == 0) await Task.Yield();
                }
            }
            catch
            {
                await failureCancellation.CancelAsync();
                throw;
            }
        }, CancellationToken.None)).ToArray();

        try
        {
            await Task.WhenAll(ready.Select(item => item.Task)).WaitAsync(token);
            using var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var gcBefore = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
            var bytesBefore = GC.GetTotalAllocatedBytes(precise: true);
            var began = Stopwatch.GetTimestamp();
            deadline = checked(began + (long)(seconds * Stopwatch.Frequency));
            start.SetResult();
            await Task.WhenAll(tasks);
            var elapsed = Stopwatch.GetTimestamp() - began;
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - bytesBefore;
            var collections = Enumerable.Range(0, 3).Select(generation =>
                GC.CollectionCount(generation) - gcBefore[generation]).ToArray();
            process.Refresh();
            var cpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            if (counts.Any(count => count == 0))
            {
                throw new InvalidOperationException("At least one worker completed no requests; the requested concurrency was not exercised.");
            }
            return new Window(elapsed, counts.Sum(), allocated, cpu, process.PeakWorkingSet64,
                collections, buffers.Select((buffer, index) => new WorkerSamples(
                    buffer, capacityPerWorker == 0 ? 0 : checked((int)counts[index]))).ToArray());
        }
        finally
        {
            await failureCancellation.CancelAsync();
            start.TrySetCanceled(token);
            // Observe all workers, including when readiness/cancellation fails.
            try { await Task.WhenAll(tasks); }
            catch when (tasks.Any(task => task.IsFaulted || task.IsCanceled)) { }
        }
    }
}
