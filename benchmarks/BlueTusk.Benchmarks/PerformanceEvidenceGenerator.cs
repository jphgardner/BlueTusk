using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlueTusk.Benchmarks;

/// <summary>
/// Turns retained raw captures for one OS into a per-OS comparison summary for the schema-3
/// Core performance-leadership evidence. Families plug in through input formats: each
/// <c>raw/&lt;input-id&gt;/</c> directory holds either a Provider <c>capture-index.json</c> or a
/// family-neutral <c>trial-index.json</c>. The output never certifies a release; the
/// independent checker, the assembler and the evidence verifier all run afterwards.
/// </summary>
internal static class PerformanceEvidenceGenerator
{
    internal static readonly string[] MetricNames =
        ["throughput", "mean", "p95", "p99", "allocatedBytes", "cpuPerEvent", "peakRss", "gcCounters"];
    internal static readonly string[] CoreFamilies = ["Provider", "Streams", "Sync", "Live", "ControlPlane"];
    internal const string ProviderFormat = "provider-request-capture-index/1";
    internal const string TrialFormat = "performance-trial-index/1";
    internal const string ProviderIndexName = "capture-index.json";
    internal const string TrialIndexName = "trial-index.json";
    internal const string NetworkProfileName = "network-profile.json";
    internal const string SummaryName = "summary.json";
    internal const string RawSamplesName = "raw-samples.json";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
    };
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    internal sealed record FileBinding(string Path, string Sha256, long Bytes);
    internal sealed record TrialValues(string InputId, string WorkloadKey, string Role, int Trial, string Path,
        string Sha256, Dictionary<string, double> Values);
    internal sealed record InputSummary(string Id, string Family, string Format, string? Variant, string? CaptureProfile,
        bool Diagnostic, bool Synthetic, int Trials, string IndexPath, FileBinding[] Files, string[] ContainerImageDigests);
    internal sealed record CaptureProfile(string Name, string ClientOs, bool Tls, string? NetworkProfile);

    internal sealed record TrialIndex(int SchemaVersion, string EvidenceKind, string Family, string SourceCommit,
        string Os, bool Diagnostic, bool Synthetic, int Trials, string CandidateImplementation,
        string ReferenceImplementation, string[] ContainerImageDigests, TrialEntry[] Records, bool LeadershipGatePassed);
    internal sealed record TrialEntry(string WorkloadKey, int Trial, string Role, string Path, string Sha256);
    internal sealed record TrialFile(int SchemaVersion, string EvidenceKind, string Family, string WorkloadKey,
        string Role, int Trial, string SourceCommit, bool Diagnostic, bool Synthetic, string Implementation,
        JsonElement Environment, long Frequency, ProviderRequestCapture.Window Measurement);

    internal static async Task RunAsync(string osRoot, string expectedCommit, string os, string contractPath,
        string variantMapPath, bool allowSynthetic)
    {
        var result = await GenerateAsync(osRoot, expectedCommit, os, contractPath, variantMapPath, allowSynthetic);
        Console.WriteLine($"Generated {result.Comparisons} {os} comparisons from {result.TrialFiles} hashed raw trial captures " +
            $"({(result.Diagnostic ? "DIAGNOSTIC" : "qualification inputs")}{(result.Synthetic ? ", SYNTHETIC" : string.Empty)}). " +
            "Run the independent checker before assembly; this output certifies nothing on its own.");
    }

    internal sealed record Result(int Comparisons, int TrialFiles, bool Diagnostic, bool Synthetic);

    internal static async Task<Result> GenerateAsync(string osRoot, string expectedCommit, string os,
        string contractPath, string variantMapPath, bool allowSynthetic)
    {
        if (!Regex.IsMatch(expectedCommit, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant) || os is not ("windows" or "linux"))
        {
            throw new ArgumentException("An exact lowercase commit SHA and a windows/linux environment are required.");
        }
        var root = Path.GetFullPath(osRoot);
        var rawRoot = Path.Combine(root, "raw");
        var summaryPath = Path.Combine(root, SummaryName);
        var rawSamplesPath = Path.Combine(root, RawSamplesName);
        if (!Directory.Exists(rawRoot) || File.Exists(summaryPath) || File.Exists(rawSamplesPath))
        {
            throw new IOException("Generation needs an existing raw directory and never overwrites an existing summary.");
        }
        RejectReparsePoint(root);
        RejectReparsePoint(rawRoot);
        var contractBytes = await File.ReadAllBytesAsync(contractPath);
        var mapBytes = await File.ReadAllBytesAsync(variantMapPath);
        using var contract = JsonDocument.Parse(contractBytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
        using var map = JsonDocument.Parse(mapBytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
        var expected = ExpectedCoreWorkloads(contract.RootElement, os);

        var inputs = new List<InputSummary>();
        var trials = new List<TrialValues>();
        foreach (var directory in Directory.GetDirectories(rawRoot).Order(StringComparer.Ordinal))
        {
            RejectReparsePoint(directory);
            var id = Path.GetFileName(directory);
            if (!Regex.IsMatch(id, "^[a-z0-9][a-z0-9.-]{0,63}$", RegexOptions.CultureInvariant))
            {
                throw new InvalidDataException($"Raw input directory '{id}' must be a lowercase identifier.");
            }
            var hasProvider = File.Exists(Path.Combine(directory, ProviderIndexName));
            var hasTrial = File.Exists(Path.Combine(directory, TrialIndexName));
            if (hasProvider == hasTrial)
            {
                throw new InvalidDataException($"Raw input '{id}' must contain exactly one capture-index.json or trial-index.json.");
            }
            var (input, values) = hasProvider
                ? await ReadProviderInputAsync(root, directory, id, expectedCommit, os, map.RootElement)
                : await ReadTrialInputAsync(root, directory, id, expectedCommit, os);
            inputs.Add(input);
            trials.AddRange(values);
        }
        if (Directory.GetFiles(rawRoot).Length != 0 || inputs.Count == 0)
        {
            throw new InvalidDataException("Raw evidence must contain at least one input directory and no loose files.");
        }
        var diagnostic = inputs.Any(input => input.Diagnostic);
        var synthetic = inputs.Any(input => input.Synthetic);
        if (synthetic && (!allowSynthetic || inputs.Any(input => !input.Diagnostic)))
        {
            throw new InvalidDataException("Synthetic fixtures are accepted only by the self-test path and only when labelled diagnostic.");
        }
        var contentHashes = new HashSet<string>(StringComparer.Ordinal);
        if (trials.Any(trial => !contentHashes.Add(trial.Sha256)))
        {
            throw new InvalidDataException("Two raw trials have identical content; copied captures are never accepted as separate measurements.");
        }

        var seed = PerformanceEvidenceStatistics.DeriveSeed(expectedCommit);
        var plans = new Dictionary<int, int[]>();
        var workloadInputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var comparisons = new List<object>();
        foreach (var group in trials.GroupBy(trial => trial.WorkloadKey, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            if (!expected.TryGetValue(group.Key, out var mode))
            {
                throw new InvalidDataException($"Workload '{group.Key}' is outside the exact Core contract for {os}.");
            }
            var owners = group.Select(trial => trial.InputId).Distinct(StringComparer.Ordinal).ToArray();
            if (owners.Length != 1 || !workloadInputs.TryAdd(group.Key, owners[0]))
            {
                throw new InvalidDataException($"Workload '{group.Key}' is supplied by more than one raw input.");
            }
            var candidate = group.Where(trial => trial.Role == "candidate").OrderBy(trial => trial.Trial).ToArray();
            var reference = group.Where(trial => trial.Role == "reference").OrderBy(trial => trial.Trial).ToArray();
            var count = candidate.Length;
            var minimum = diagnostic
                ? PerformanceEvidenceStatistics.MinimumDiagnosticTrials
                : PerformanceEvidenceStatistics.MinimumQualificationTrials;
            if (count != reference.Length || count + reference.Length != group.Count() ||
                count < minimum || count > PerformanceEvidenceStatistics.MaximumTrials ||
                !candidate.Select(trial => trial.Trial).SequenceEqual(Enumerable.Range(0, count)) ||
                !reference.Select(trial => trial.Trial).SequenceEqual(Enumerable.Range(0, count)))
            {
                throw new InvalidDataException(
                    $"Workload '{group.Key}' needs {minimum}..{PerformanceEvidenceStatistics.MaximumTrials} complete, matched candidate and reference trials.");
            }
            if (!plans.TryGetValue(count, out var plan))
            {
                plan = PerformanceEvidenceStatistics.CreatePlan(seed, count);
                plans[count] = plan;
            }
            var metrics = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var metric in MetricNames)
            {
                var c = PerformanceEvidenceStatistics.Estimate(candidate.Select(trial => trial.Values[metric]).ToArray(), plan);
                var r = PerformanceEvidenceStatistics.Estimate(reference.Select(trial => trial.Values[metric]).ToArray(), plan);
                metrics[metric] = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["candidate"] = c.Point,
                    ["candidateCiLower"] = c.Lower,
                    ["candidateCiUpper"] = c.Upper,
                    ["reference"] = r.Point,
                    ["referenceCiLower"] = r.Lower,
                    ["referenceCiUpper"] = r.Upper,
                };
            }
            comparisons.Add(new
            {
                WorkloadKey = group.Key,
                Family = group.Key.Split('|')[1],
                Mode = mode,
                ConfidenceLevel = PerformanceEvidenceStatistics.ConfidenceLevel,
                Trials = count,
                PerformanceEvidenceStatistics.TailProbability,
                InputId = owners[0],
                CandidateTrials = candidate.Select(TrialView).ToArray(),
                ReferenceTrials = reference.Select(TrialView).ToArray(),
                Metrics = metrics,
            });
        }

        var rawSamples = new
        {
            SchemaVersion = 1,
            EvidenceKind = "bluetusk-performance-raw-samples",
            SourceCommit = expectedCommit,
            Os = os,
            Diagnostic = diagnostic,
            Synthetic = synthetic,
            Inputs = inputs.Select(input => new
            {
                input.Id,
                input.Family,
                input.Format,
                input.Variant,
                input.CaptureProfile,
                input.Diagnostic,
                input.Synthetic,
                input.Trials,
                Index = input.IndexPath,
                input.ContainerImageDigests,
                input.Files,
            }).ToArray(),
        };
        var rawSamplesBytes = JsonSerializer.SerializeToUtf8Bytes(rawSamples, WriteOptions);
        await WriteNewAsync(rawSamplesPath, rawSamplesBytes);
        var summary = new
        {
            SchemaVersion = 1,
            EvidenceKind = "bluetusk-performance-summary",
            Release = "1.1.0",
            Scope = "Core",
            SourceCommit = expectedCommit,
            Os = os,
            Architecture = "x64",
            Diagnostic = diagnostic,
            Synthetic = synthetic,
            Generator = new
            {
                Name = "BlueTusk.Benchmarks --performance-evidence-generate",
                Assembly = typeof(PerformanceEvidenceGenerator).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            },
            Contract = new { Sha256 = Convert.ToHexStringLower(SHA256.HashData(contractBytes)) },
            VariantMap = new
            {
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(mapBytes)),
                CrossOsProfile = map.RootElement.GetProperty("crossOsProfile").GetString(),
            },
            RawSamples = new FileBinding(RawSamplesName, Convert.ToHexStringLower(SHA256.HashData(rawSamplesBytes)), rawSamplesBytes.LongLength),
            Statistics = new
            {
                PerformanceEvidenceStatistics.Method,
                PerformanceEvidenceStatistics.ConfidenceLevel,
                PerformanceEvidenceStatistics.IntervalConfidenceLevel,
                PerformanceEvidenceStatistics.Resamples,
                Seed = seed,
                SeedDerivation = "lowercase hex SHA-256 of UTF-8 '" + PerformanceEvidenceStatistics.SeedPrefix + "' + sourceCommit",
                Plan = "index[b,j] = UInt64LE(SHA-256(seed32 || Int32LE(n) || Int32LE(b) || Int32LE(j))[0..8]) mod n",
                PointEstimate = "arithmetic mean of per-trial values; one trial is one independently restarted process",
                Interval = "studentized bootstrap of trial means; SE = sample SD / sqrt(n); t* = (mean* - mean) / SE*; " +
                    "bounds = mean - quantiles(t*, 0.995 and 0.005) * SE; singular draws retained; unbounded intervals rejected; " +
                    "intersected with nonnegative parameter space and widened to contain the point estimate",
                PerformanceEvidenceStatistics.MinimumQualificationTrials,
                PerformanceEvidenceStatistics.MinimumDiagnosticTrials,
                PerformanceEvidenceStatistics.TailProbability,
                Multiplicity = "nominal 99% intervals support the contract's 95% comparison with additional margin; no simultaneous full-matrix confidence claim",
            },
            MetricDefinitions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["throughput"] = "completed operations per second of measured elapsed time (higher is better)",
                ["mean"] = "mean individual operation latency in microseconds",
                ["p95"] = "nearest-rank 95th percentile individual operation latency in microseconds",
                ["p99"] = "nearest-rank 99th percentile individual operation latency in microseconds",
                ["allocatedBytes"] = "measured process allocation bytes per completed operation",
                ["cpuPerEvent"] = "measured process CPU microseconds per completed operation",
                ["peakRss"] = "process peak resident set (working set) bytes",
                ["gcCounters"] = "gen0 collections (every GC) per 1,000 completed operations; reported, not gated",
            },
            Comparisons = comparisons,
            LeadershipGatePassed = false,
        };
        await WriteNewAsync(summaryPath, JsonSerializer.SerializeToUtf8Bytes(summary, WriteOptions));
        return new(comparisons.Count, trials.Count, diagnostic, synthetic);

        static object TrialView(TrialValues trial) => new { trial.Trial, trial.Path, trial.Sha256, trial.Values };
    }

    internal static Dictionary<string, string> ExpectedCoreWorkloads(JsonElement contract, string os)
    {
        // Mirrors eng/verify-performance-leadership-evidence.ps1 for the Core scope.
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        var workloads = contract.GetProperty("workloads");
        void Add(string key, string mode)
        {
            if (!expected.TryAdd(key, mode)) throw new InvalidDataException($"Duplicate contract workload '{key}'.");
        }
        static IEnumerable<string> Values(JsonElement family, string name) =>
            family.GetProperty(name).EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
                ? value.GetString()! : value.GetRawText());
        var provider = workloads.GetProperty("Provider");
        foreach (var feature in Values(provider, "features"))
            foreach (var concurrency in Values(provider, "concurrency"))
                foreach (var variant in Values(provider, "variants"))
                    Add($"{os}|Provider|{feature}|c={concurrency}|variant={variant}", "same-runtime");
        var streams = workloads.GetProperty("Streams");
        foreach (var changes in Values(streams, "transactionChanges"))
        {
            foreach (var scenario in Values(streams, "scenarios")) Add($"{os}|Streams|changes={changes}|scenario={scenario}", "cross-runtime");
            foreach (var spool in Values(streams, "spoolBytes")) Add($"{os}|Streams|changes={changes}|spoolBytes={spool}", "cross-runtime");
        }
        var sync = workloads.GetProperty("Sync");
        foreach (var mutations in Values(sync, "mutationCounts"))
            foreach (var destination in Values(sync, "destinations"))
                Add($"{os}|Sync|mutations={mutations}|destination={destination}", "cross-runtime");
        var live = workloads.GetProperty("Live");
        foreach (var results in Values(live, "resultCounts"))
            foreach (var subscribers in Values(live, "subscriberCounts"))
                foreach (var scenario in Values(live, "scenarios"))
                    Add($"{os}|Live|results={results}|subscribers={subscribers}|scenario={scenario}", "cross-runtime");
        var control = workloads.GetProperty("ControlPlane");
        foreach (var sources in Values(control, "sourceCounts"))
            foreach (var clients in Values(control, "apiClientCounts"))
                Add($"{os}|ControlPlane|sources={sources}|clients={clients}", "unique");
        foreach (var family in CoreFamilies)
        {
            Add($"{os}|{family}|primary-hot-path", "unique-primary");
        }
        return expected;
    }

    internal static CaptureProfile ResolveProfile(JsonElement map, string os, string variant)
    {
        if (map.GetProperty("schemaVersion").GetInt32() != 1 ||
            !map.GetProperty("variants").TryGetProperty(os, out var variants) ||
            !variants.TryGetProperty(variant, out var entry) || entry.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Variant '{variant}' is not mapped for '{os}'.");
        }
        var name = entry.GetString()!;
        if (name == "crossOsProfile")
        {
            name = map.GetProperty("crossOsProfile").GetString()!;
        }
        if (name == "unresolved")
        {
            throw new InvalidDataException(
                $"Variant '{variant}' on '{os}' has no adopted meaning; see {map.GetProperty("proposal").GetString()}. " +
                "Cross-OS keys are never filled from same-OS captures.");
        }
        if (!map.GetProperty("profiles").TryGetProperty(name, out var profile) ||
            !profile.GetProperty("hostOs").EnumerateArray().Any(host => host.GetString() == os))
        {
            throw new InvalidDataException($"Capture profile '{name}' is undefined or cannot run on a {os} host.");
        }
        var clientOs = profile.GetProperty("clientOs").GetString() switch
        {
            "host" => os,
            "other" => os == "windows" ? "linux" : "windows",
            "windows" => "windows",
            "linux" => "linux",
            _ => throw new InvalidDataException($"Capture profile '{name}' has an unknown client OS."),
        };
        var network = profile.GetProperty("networkProfile");
        return new(name, clientOs, profile.GetProperty("tls").GetBoolean(),
            network.ValueKind == JsonValueKind.Null ? null : network.GetString());
    }

    private static async Task<(InputSummary, List<TrialValues>)> ReadProviderInputAsync(string root, string directory,
        string id, string commit, string os, JsonElement map)
    {
        var indexPath = Path.Combine(directory, ProviderIndexName);
        var index = JsonSerializer.Deserialize<ProviderRequestAnalysis.Index>(await File.ReadAllBytesAsync(indexPath), ReadOptions)
            ?? throw new InvalidDataException("Provider capture index is empty.");
        if (index.Os != os || index.SourceCommit != commit || index.CaptureProfile is null)
        {
            throw new InvalidDataException($"Provider input '{id}' belongs to another OS or commit, or predates capture profiles.");
        }
        var profile = ResolveProfile(map, os, index.Variant);
        if (index.CaptureProfile != profile.Name || index.EffectiveClientOs != profile.ClientOs ||
            index.EffectiveRequireTls != profile.Tls)
        {
            throw new InvalidDataException($"Provider input '{id}' does not match the variant map profile '{profile.Name}'.");
        }
        var files = new List<string> { ProviderIndexName };
        if (profile.NetworkProfile is null)
        {
            if (index.NetworkProfile is not null || File.Exists(Path.Combine(directory, NetworkProfileName)))
            {
                throw new InvalidDataException($"Provider input '{id}' declares network shaping its profile does not use.");
            }
        }
        else
        {
            var match = Regex.Match(index.NetworkProfile ?? string.Empty,
                "^" + Regex.Escape(profile.NetworkProfile) + "@sha256:([0-9a-f]{64})$", RegexOptions.CultureInvariant);
            var networkPath = Path.Combine(directory, NetworkProfileName);
            if (!match.Success || !File.Exists(networkPath) ||
                Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(networkPath))) != match.Groups[1].Value)
            {
                throw new InvalidDataException($"Provider input '{id}' must retain the observed '{profile.NetworkProfile}' configuration it names.");
            }
            using var network = JsonDocument.Parse(await File.ReadAllBytesAsync(networkPath));
            if (network.RootElement.GetProperty("profile").GetString() != profile.NetworkProfile ||
                !(index.ContainerImageDigests ?? []).Contains(network.RootElement.GetProperty("toxiproxyImage").GetString()))
            {
                throw new InvalidDataException($"Provider input '{id}' network profile or Toxiproxy image is not bound.");
            }
            files.Add(NetworkProfileName);
        }
        var images = index.ContainerImageDigests ?? [index.PostgreSqlImage];
        if (!images.Contains(index.PostgreSqlImage, StringComparer.Ordinal) || images.Distinct(StringComparer.Ordinal).Count() != images.Length ||
            images.Any(image => !Regex.IsMatch(image, "^\\S+@sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)))
        {
            throw new InvalidDataException($"Provider input '{id}' must name unique digest-pinned container images, including PostgreSQL.");
        }
        var analysis = await ProviderRequestAnalysis.AnalyzeAsync(indexPath, commit);
        var values = new List<TrialValues>();
        foreach (var trial in analysis.Trials)
        {
            files.Add(trial.Path);
            files.Add(trial.Path[..^".json".Length] + ".options.json");
            var operations = (double)trial.CompletedOperations;
            values.Add(new(id, trial.WorkloadKey, trial.Provider == "bluetusk" ? "candidate" : "reference", trial.TrialIndex,
                Relative(root, Path.Combine(directory, trial.Path)), trial.Sha256, new(StringComparer.Ordinal)
                {
                    ["throughput"] = trial.Metrics["operationsPerSecond"],
                    ["mean"] = trial.Metrics["meanUs"],
                    ["p95"] = trial.Metrics["p95Us"],
                    ["p99"] = trial.Metrics["p99Us"],
                    ["allocatedBytes"] = trial.Metrics["allocatedBytesPerOperation"],
                    ["cpuPerEvent"] = trial.Metrics["cpuUsPerOperation"],
                    ["peakRss"] = trial.Metrics["peakRssBytes"],
                    ["gcCounters"] = trial.GcCollections[0] * 1000d / operations,
                }));
        }
        var bindings = await BindDirectoryAsync(root, directory, files, optionalNames: files.Where(name => name.EndsWith(".options.json", StringComparison.Ordinal)));
        return (new(id, "Provider", ProviderFormat, index.Variant, index.CaptureProfile, index.Diagnostic, index.Synthetic,
            index.Trials, Relative(root, indexPath), bindings, images), values);
    }

    private static async Task<(InputSummary, List<TrialValues>)> ReadTrialInputAsync(string root, string directory,
        string id, string commit, string os)
    {
        var indexPath = Path.Combine(directory, TrialIndexName);
        var index = JsonSerializer.Deserialize<TrialIndex>(await File.ReadAllBytesAsync(indexPath), ReadOptions)
            ?? throw new InvalidDataException("Trial index is empty.");
        if (index.SchemaVersion != 1 || index.EvidenceKind != "bluetusk-performance-trial-index" ||
            !CoreFamilies.Contains(index.Family, StringComparer.Ordinal) || index.SourceCommit != commit || index.Os != os ||
            index.Trials is < 1 or > PerformanceEvidenceStatistics.MaximumTrials || index.LeadershipGatePassed ||
            index.Records is not { Length: > 0 and <= 20_000 } || index.ContainerImageDigests is not { Length: > 0 } ||
            index.ContainerImageDigests.Distinct(StringComparer.Ordinal).Count() != index.ContainerImageDigests.Length ||
            index.ContainerImageDigests.Any(image => !Regex.IsMatch(image, "^\\S+@sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)) ||
            string.IsNullOrWhiteSpace(index.CandidateImplementation) || string.IsNullOrWhiteSpace(index.ReferenceImplementation))
        {
            throw new InvalidDataException($"Trial input '{id}' identity, family, bounds or image evidence is invalid.");
        }
        var seen = new HashSet<(string, int, string)>();
        var files = new List<string> { TrialIndexName };
        var values = new List<TrialValues>();
        foreach (var entry in index.Records)
        {
            if (entry.Role is not ("candidate" or "reference") || entry.Trial < 0 || entry.Trial >= index.Trials ||
                !entry.WorkloadKey.StartsWith($"{os}|{index.Family}|", StringComparison.Ordinal) ||
                !seen.Add((entry.WorkloadKey, entry.Trial, entry.Role)) ||
                !Regex.IsMatch(entry.Sha256, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
            {
                throw new InvalidDataException($"Trial input '{id}' has a duplicate or invalid record.");
            }
            var path = ProviderRequestAnalysis.ResolveArtifact(directory, entry.Path);
            var bytes = await File.ReadAllBytesAsync(path);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (hash != entry.Sha256)
            {
                throw new InvalidDataException($"Trial '{entry.Path}' does not match its retained SHA-256.");
            }
            var trial = JsonSerializer.Deserialize<TrialFile>(bytes, ReadOptions)
                ?? throw new InvalidDataException("Trial capture is empty.");
            if (trial.SchemaVersion != 1 || trial.EvidenceKind != "bluetusk-performance-trial" || trial.Family != index.Family ||
                trial.WorkloadKey != entry.WorkloadKey || trial.Role != entry.Role || trial.Trial != entry.Trial ||
                trial.SourceCommit != commit || trial.Diagnostic != index.Diagnostic || trial.Synthetic != index.Synthetic ||
                trial.Implementation != (entry.Role == "candidate" ? index.CandidateImplementation : index.ReferenceImplementation) ||
                trial.Frequency <= 0 || trial.Environment.GetProperty("os").GetString() != os ||
                trial.Environment.GetProperty("architecture").GetString() != "x64")
            {
                throw new InvalidDataException($"Trial '{entry.Path}' identity does not match its index.");
            }
            var metrics = ProviderRequestStatistics.Derive(trial.Measurement, trial.Frequency);
            files.Add(entry.Path);
            values.Add(new(id, entry.WorkloadKey, entry.Role, entry.Trial, Relative(root, path), hash, new(StringComparer.Ordinal)
            {
                ["throughput"] = metrics["operationsPerSecond"],
                ["mean"] = metrics["meanUs"],
                ["p95"] = metrics["p95Us"],
                ["p99"] = metrics["p99Us"],
                ["allocatedBytes"] = metrics["allocatedBytesPerOperation"],
                ["cpuPerEvent"] = metrics["cpuUsPerOperation"],
                ["peakRss"] = metrics["peakRssBytes"],
                ["gcCounters"] = trial.Measurement.GcCollections[0] * 1000d / trial.Measurement.CompletedOperations,
            }));
        }
        var bindings = await BindDirectoryAsync(root, directory, files, optionalNames: []);
        return (new(id, index.Family, TrialFormat, null, null, index.Diagnostic, index.Synthetic, index.Trials,
            Relative(root, indexPath), bindings, index.ContainerImageDigests), values);
    }

    /// <summary>Binds every file in an input directory; unexpected files are rejected rather than silently retained.</summary>
    private static async Task<FileBinding[]> BindDirectoryAsync(string root, string directory, List<string> required,
        IEnumerable<string> optionalNames)
    {
        var allowed = new HashSet<string>(required, StringComparer.Ordinal);
        var optional = new HashSet<string>(optionalNames, StringComparer.Ordinal);
        var bindings = new List<FileBinding>();
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            RejectReparsePoint(file);
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            if (!allowed.Contains(relative))
            {
                throw new InvalidDataException($"Raw input '{Path.GetFileName(directory)}' contains unbound file '{relative}'.");
            }
            present.Add(relative);
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            bindings.Add(new(Relative(root, file), Convert.ToHexStringLower(await SHA256.HashDataAsync(stream)), stream.Length));
        }
        if (allowed.Any(name => !present.Contains(name) && !optional.Contains(name)))
        {
            throw new InvalidDataException($"Raw input '{Path.GetFileName(directory)}' is missing an indexed file.");
        }
        return bindings.ToArray();
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Performance evidence must not traverse symbolic links or junctions.");
        }
    }

    private static async Task WriteNewAsync(string path, byte[] bytes)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(bytes);
    }
}
