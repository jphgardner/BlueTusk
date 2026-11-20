using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlueTusk.Benchmarks;

/// <summary>A single-provider process capture; comparisons are assembled outside this process.</summary>
internal static partial class ProviderRequestCapture
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

        /// <summary>Retained network-shaping profile identity, such as <c>toxiproxy-constrained-v1@sha256:...</c>.</summary>
        public string? NetworkProfile { get; init; }

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
                !Regex.IsMatch(PostgreSqlImage, "^postgres:[^@\\s]+@sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant) ||
                NetworkProfile is not null && !Regex.IsMatch(NetworkProfile,
                    "^[a-z0-9][a-z0-9.-]{0,63}@sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant))
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
                    NetworkShaping = options.NetworkProfile ?? "not configured by this adapter",
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
}
