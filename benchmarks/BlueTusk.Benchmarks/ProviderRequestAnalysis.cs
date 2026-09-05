using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlueTusk.Benchmarks;

/// <summary>Recomputes Provider statistics from complete, hash-checked capture indexes.</summary>
internal static class ProviderRequestAnalysis
{
    private static readonly string[] LowerTargetMetrics = ["meanUs", "p95Us", "p99Us", "allocatedBytesPerOperation"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        RespectRequiredConstructorParameters = true,
    };

    internal sealed record Index(int SchemaVersion, string SourceCommit, bool SourceDirty, bool Diagnostic,
        string HarnessSha256, string Os, string Variant, string PostgreSqlImage, int Trials,
        double WarmupSeconds, double MeasurementSeconds, Entry[] Records, bool LeadershipGatePassed);
    internal sealed record Entry(string WorkloadKey, int Trial, string Provider, string Path,
        string Sha256, long CompletedOperations);
    internal sealed record Capture(int SchemaVersion, string EvidenceKind, bool Diagnostic, string SourceCommit,
        string Provider, string Feature, int Concurrency, double WarmupSeconds, double MeasurementSeconds,
        int MaximumSamplesPerWorker, JsonElement Environment, JsonElement Method, ProviderRequestCapture.Window Measurement);
    internal sealed record Trial(string WorkloadKey, int TrialIndex, string Provider, string Path, string Sha256,
        long CompletedOperations, Dictionary<string, double> Metrics, int[] GcCollections);
    internal sealed record Comparison(string WorkloadKey, int PairedTrials,
        Dictionary<string, ProviderRequestStatistics.Metric> Metrics, bool ObservedNumericalTargetMet);
    internal sealed record Analysis(Index CaptureIndex, string CaptureIndexSha256, Trial[] Trials, Comparison[] Comparisons);

    internal static async Task RunAsync(string indexPath, string expectedCommit, string outputDirectory)
    {
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) || File.Exists(output))
        {
            throw new IOException("Analysis requires a new output directory; prior evidence is never overwritten.");
        }
        var analysis = await AnalyzeAsync(indexPath, expectedCommit);
        Directory.CreateDirectory(output);
        await using (var json = new FileStream(Path.Combine(output, "provider-analysis.json"),
            FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(json, new
            {
                SchemaVersion = 1,
                EvidenceKind = "provider-derived-statistics; not consolidated release evidence",
                SourceCommit = expectedCommit,
                analysis.CaptureIndex.Diagnostic,
                analysis.CaptureIndex.SourceDirty,
                InputIndex = Path.GetFullPath(indexPath),
                analysis.CaptureIndexSha256,
                analysis.CaptureIndex.HarnessSha256,
                AnalyzerAssembly = typeof(ProviderRequestAnalysis).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                AnalyzedUtc = DateTimeOffset.UtcNow,
                Method = new
                {
                    Percentiles = "nearest-rank on individual requests, separately within each process trial",
                    PointRatio = "geometric mean of matched BlueTusk/Npgsql trial ratios; trials equally weighted",
                    Confidence = "approximate two-sided 95% Student-t interval on paired trial log-ratios; minimum five pairs",
                    Assumptions = "independent trials and approximately normal trial log-ratios; not established by this analyzer",
                    CriticalValues = "NIST 0.975 column plus 0.001 to conservatively account for published rounding",
                    Multiplicity = "individual metric intervals; no simultaneous full-matrix confidence claim",
                    Threshold = "0.98 point and upper confidence ratio for mean, P95, P99 and allocation",
                    ZeroCounters = "retained as measured; no fabricated positive denominator or log-ratio confidence",
                },
                analysis.Trials,
                analysis.Comparisons,
                LeadershipGatePassed = false,
                RemainingVerification = "Dedicated-runner/image provenance, statistical assumptions, final-SHA evidence, full cross-product matrix and consolidated gate integration remain required.",
            }, JsonOptions);
        }
        await using (var markdown = new StreamWriter(new FileStream(Path.Combine(output, "provider-report.md"),
            FileMode.CreateNew, FileAccess.Write, FileShare.None), new UTF8Encoding(false)))
        {
            await markdown.WriteAsync(Render(analysis));
        }
        Console.WriteLine($"Derived {analysis.Comparisons.Length} paired workloads from {analysis.Trials.Length} verified raw captures. Report: {Path.Combine(output, "provider-report.md")}. No release gate is certified.");
    }

    internal static async Task<Analysis> AnalyzeAsync(string indexPath, string expectedCommit)
    {
        if (!Regex.IsMatch(expectedCommit, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("An exact lowercase expected commit SHA is required.");
        }
        var absoluteIndex = Path.GetFullPath(indexPath);
        var root = Path.GetDirectoryName(absoluteIndex)!;
        var (index, indexHash) = await ReadAsync<Index>(absoluteIndex, 8 * 1024 * 1024);
        if (index.SchemaVersion != 1 || index.SourceCommit != expectedCommit || index.Trials is < 1 or > 50 ||
            index.Os is not ("windows" or "linux") || index.Variant != "tls" && index.Variant != index.Os ||
            index.Records is not { Length: > 0 and <= 4800 } || index.LeadershipGatePassed ||
            !Regex.IsMatch(index.HarnessSha256, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(index.PostgreSqlImage, "^postgres:[^@\\s]+@sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant) ||
            !double.IsFinite(index.WarmupSeconds) || index.WarmupSeconds is < 0.1 or > 300 ||
            !double.IsFinite(index.MeasurementSeconds) || index.MeasurementSeconds is < 0.1 or > 300 ||
            !index.Diagnostic && (index.SourceDirty || index.Trials < 5 || index.WarmupSeconds < 5 || index.MeasurementSeconds < 10))
        {
            throw new InvalidDataException("Capture index identity, bounds, or diagnostic status is invalid.");
        }
        var seen = new HashSet<(string, int, string)>();
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var identities = new Dictionary<string, string>(StringComparer.Ordinal);
        var trials = new List<Trial>();
        foreach (var entry in index.Records)
        {
            if (entry.Provider is not ("bluetusk" or "npgsql") || entry.Trial < 0 || entry.Trial >= index.Trials ||
                !seen.Add((entry.WorkloadKey, entry.Trial, entry.Provider)) || !paths.Add(entry.Path) ||
                !Regex.IsMatch(entry.Sha256, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
            {
                throw new InvalidDataException("Duplicate or invalid capture record.");
            }
            var path = ResolveArtifact(root, entry.Path);
            var (capture, hash) = await ReadAsync<Capture>(path, 1024L * 1024 * 1024, entry.Sha256);
            if (capture.SchemaVersion != 1 || capture.EvidenceKind != "provider-individual-request-capture" ||
                capture.SourceCommit != expectedCommit || capture.Diagnostic != index.Diagnostic || capture.Provider != entry.Provider ||
                !ProviderRequestFixture.CaptureFeatures.Contains(capture.Feature, StringComparer.Ordinal) ||
                capture.Concurrency is < 1 or > 256 || capture.Measurement.Workers.Length != capture.Concurrency ||
                capture.MaximumSamplesPerWorker is < 1 or > 16_000_000 ||
                (long)capture.Concurrency * capture.MaximumSamplesPerWorker > 32_000_000 ||
                capture.Measurement.Workers.Any(worker => worker.Count > capture.MaximumSamplesPerWorker) ||
                capture.WarmupSeconds != index.WarmupSeconds || capture.MeasurementSeconds != index.MeasurementSeconds ||
                capture.Measurement.CompletedOperations != entry.CompletedOperations ||
                entry.WorkloadKey != $"{index.Os}|Provider|{capture.Feature}|c={capture.Concurrency}|variant={index.Variant}")
            {
                throw new InvalidDataException("Raw capture identity, options, or counters do not match its index.");
            }
            var environment = capture.Environment;
            if (ProviderRequestFixture.IsContentionFeature(capture.Feature) &&
                (!environment.TryGetProperty("poolSize", out var poolSize) || poolSize.GetInt32() != 4 ||
                 !environment.TryGetProperty("multiplexingConfigured", out var multiplexing) ||
                 multiplexing.GetBoolean() != capture.Feature.StartsWith("multiplexed-", StringComparison.Ordinal) ||
                 !capture.Method.TryGetProperty("contentionProbe", out var contention) || !contention.GetBoolean()))
            {
                throw new InvalidDataException("Contention capture must preserve the four-slot pool and requested multiplexing mode.");
            }
            if (environment.GetProperty("os").GetString() != index.Os ||
                environment.GetProperty("architecture").GetString() != "x64" ||
                environment.GetProperty("postgreSqlImage").GetString() != index.PostgreSqlImage ||
                environment.GetProperty("tlsActive").GetBoolean() != (index.Variant == "tls") ||
                !Regex.IsMatch(environment.GetProperty("referenceAssembly").GetString()!, "^10\\.0\\.3(?:\\+|$)", RegexOptions.CultureInvariant) ||
                !index.Diagnostic && (!environment.GetProperty("harnessAssembly").GetString()!.EndsWith("+" + expectedCommit, StringComparison.Ordinal) ||
                    !environment.GetProperty("candidateAssembly").GetString()!.EndsWith("+" + expectedCommit, StringComparison.Ordinal)))
            {
                throw new InvalidDataException("Raw capture environment does not match the candidate, reference or transport.");
            }
            var frequency = capture.Method.GetProperty("frequency").GetInt64();
            if (capture.Method.GetProperty("sampleBufferCapacityBytes").GetInt64() !=
                (long)capture.MaximumSamplesPerWorker * capture.Concurrency * sizeof(long) ||
                frequency <= 0 || (double)capture.Measurement.ElapsedTicks / frequency < capture.MeasurementSeconds)
            {
                throw new InvalidDataException("Raw capture clock, observation window or sample-buffer capacity is inconsistent.");
            }
            // Generated reports have a stable property order. Be conservative if an externally
            // rewritten report changes metadata: do not silently pair different methodologies.
            var identity = environment.GetRawText() + capture.Method.GetRawText();
            if (!identities.TryAdd(entry.WorkloadKey, identity) && identities[entry.WorkloadKey] != identity)
            {
                throw new InvalidDataException("Matched trials have different environment or measurement metadata.");
            }
            trials.Add(new(entry.WorkloadKey, entry.Trial, entry.Provider, entry.Path, hash,
                capture.Measurement.CompletedOperations, ProviderRequestStatistics.Derive(capture.Measurement, frequency),
                capture.Measurement.GcCollections));
        }
        var comparisons = new List<Comparison>();
        foreach (var group in trials.GroupBy(trial => trial.WorkloadKey).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            if (group.Count() != 2 * index.Trials)
            {
                throw new InvalidDataException("Incomplete trial pairs; every requested trial needs both providers.");
            }
            var candidate = group.Where(trial => trial.Provider == "bluetusk").OrderBy(trial => trial.TrialIndex).ToArray();
            var reference = group.Where(trial => trial.Provider == "npgsql").OrderBy(trial => trial.TrialIndex).ToArray();
            var metrics = candidate[0].Metrics.Keys.ToDictionary(metric => metric, metric =>
                ProviderRequestStatistics.Compare(candidate.Select(trial => trial.Metrics[metric]).ToArray(),
                    reference.Select(trial => trial.Metrics[metric]).ToArray()), StringComparer.Ordinal);
            var numericalTarget = LowerTargetMetrics
                .All(metric => ProviderRequestStatistics.MeetsLowerTarget(metrics[metric], 0.98));
            comparisons.Add(new(group.Key, index.Trials, metrics, numericalTarget));
        }
        return new(index, indexHash, trials.ToArray(), comparisons.ToArray());
    }

    internal static string ResolveArtifact(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':') ||
            relative.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException("Capture artifact path must be normalized and relative to its index.");
        }
        var path = root;
        foreach (var segment in relative.Split('/'))
        {
            path = Path.Combine(path, segment);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Capture artifacts must not traverse symbolic links or junctions.");
            }
        }
        return path;
    }

    private static async Task<(T Value, string Hash)> ReadAsync<T>(string path, long maximumBytes, string? expectedHash = null)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > maximumBytes)
        {
            throw new InvalidDataException("Capture file is empty or exceeds the bounded input size.");
        }
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
        if (expectedHash is not null && hash != expectedHash)
        {
            throw new InvalidDataException("Raw capture SHA-256 does not match the retained index.");
        }
        stream.Position = 0;
        var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions)
            ?? throw new InvalidDataException("Capture JSON is empty.");
        return (value, hash);
    }

    private static string Render(Analysis analysis)
    {
        var text = new StringBuilder("# BlueTusk / Npgsql request-level comparison\n\n");
        text.AppendLine(analysis.CaptureIndex.Diagnostic
            ? "**Diagnostic run — not release-performance evidence.**"
            : "**Raw-data analysis — release provenance and full-matrix verification remain outstanding.**");
        text.AppendLine(CultureInfo.InvariantCulture, $"\nCandidate: `{analysis.CaptureIndex.SourceCommit}`. Reference: Npgsql 10.0.3.");
        text.AppendLine(CultureInfo.InvariantCulture, $"Environment: {analysis.CaptureIndex.Os} x64; variant: {analysis.CaptureIndex.Variant}; " +
            $"{analysis.CaptureIndex.Trials} paired trial(s) per workload.");
        text.AppendLine("\nRatios are BlueTusk / Npgsql. Lower is better for latency, allocation, CPU and RSS; higher is better for throughput.");
        text.AppendLine("Values are geometric means of matched trial ratios. P95/P99 describe individual requests, not block averages. Each process trial is weighted equally.");
        text.AppendLine("\n## Latency and allocation\n");
        text.AppendLine("| Workload | Concurrency | Mean | P95 | P99 | Allocation | Confidence |");
        text.AppendLine("|---|---:|---:|---:|---:|---:|---|");
        foreach (var comparison in analysis.Comparisons)
        {
            var parts = comparison.WorkloadKey.Split('|');
            text.AppendLine($"| {parts[2]} | {parts[3][2..]} | {Ratio(comparison, "meanUs")} | {Ratio(comparison, "p95Us")} | " +
                $"{Ratio(comparison, "p99Us")} | {Ratio(comparison, "allocatedBytesPerOperation")} | " +
                (comparison.PairedTrials < 5 ? "Too few trials" : comparison.ObservedNumericalTargetMet ?
                    "Numeric target met; not certified" : "Target not established") + " |");
        }
        text.AppendLine("\n## Throughput and process cost\n");
        text.AppendLine("| Workload | Concurrency | Throughput | CPU / operation | Peak RSS |");
        text.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var comparison in analysis.Comparisons)
        {
            var parts = comparison.WorkloadKey.Split('|');
            text.AppendLine(CultureInfo.InvariantCulture, $"| {parts[2]} | {parts[3][2..]} | {Ratio(comparison, "operationsPerSecond")} | " +
                $"{Ratio(comparison, "cpuUsPerOperation")} | {Ratio(comparison, "peakRssBytes")} |");
        }
        text.AppendLine("\n## Reading the evidence\n");
        text.AppendLine("Brackets contain approximate two-sided 95% confidence limits on the paired ratio, when at least five trial pairs exist. N/A means a zero counter prevents log-ratio inference; no substitute denominator is used.");
        text.AppendLine("The interval assumes independent, approximately normally distributed trial log-ratios. It does not treat thousands of requests from one process as thousands of independent trials. These are individual metric intervals, not a simultaneous confidence guarantee across the whole programme.");
        text.AppendLine("The 0.98 numerical target requires both the point ratio and upper confidence limit to be at most 0.98 for all four latency/allocation metrics. Statistical ties do not pass. Diagnostic status is never promoted to a release verdict.");
        text.AppendLine("\nMeasurements include harness overhead. RSS includes setup and preallocated buffers, excludes PostgreSQL, and is a process high-water mark. COPY import and EF writes end in rollback, not durable commit. Load is closed-loop. Short windows can be dominated by warmup or timer resolution.");
        text.AppendLine("\nPer-trial absolute values, GC counters, hashes and complete confidence results are in [provider-analysis.json](provider-analysis.json). Original raw capture hashes and their index hash are retained. Runner/image attestation, final-SHA evidence and the remaining product/OS/network workloads are still required.");
        text.AppendLine("\nMethod references: [NIST confidence limits](https://www.itl.nist.gov/div898/handbook/eda/section3/eda352.htm) and [Student-t critical values](https://www.itl.nist.gov/div898/handbook/eda/section3/eda3672.htm). The analyzer applies the mean-interval formula to paired trial log-ratios, using conservatively rounded critical values.");
        return text.ToString();
    }

    private static string Ratio(Comparison comparison, string name)
    {
        var metric = comparison.Metrics[name];
        if (metric.GeometricPairedRatio is not { } ratio) return "N/A";
        var value = ratio.ToString("0.0000", CultureInfo.InvariantCulture);
        return metric.RatioCiLower is { } lower && metric.RatioCiUpper is { } upper
            ? $"{value} [{lower.ToString("0.0000", CultureInfo.InvariantCulture)}, {upper.ToString("0.0000", CultureInfo.InvariantCulture)}]"
            : value;
    }
}
