using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// Independent raw-to-summary checker. Usage:
//   PerformanceEvidenceChecker check <osRoot> <expectedCommit> <windows|linux> <variantMapPath> [reportPath]
//   PerformanceEvidenceChecker self-test
try
{
    switch (args)
    {
        case ["check", var root, var commit, var os, var map]:
            Console.WriteLine(Checker.Check(root, commit, os, map, null));
            return 0;
        case ["check", var root, var commit, var os, var map, var report]:
            // Re-check retained evidence later (for example before emitting a local record) without touching it.
            Console.WriteLine(Checker.Check(root, commit, os, map, report));
            return 0;
        case ["self-test"]:
            Console.WriteLine(Checker.SelfTest());
            return 0;
        default:
            Console.Error.WriteLine("Usage: check <osRoot> <expectedCommit> <windows|linux> <variantMapPath> [reportPath] | self-test");
            return 2;
    }
}
catch (CheckFailedException error)
{
    Console.Error.WriteLine("Performance evidence check FAILED: " + error.Message);
    return 1;
}

/// <summary>A raw/summary inconsistency or a malformed artifact.</summary>
internal sealed class CheckFailedException(string message) : Exception(message);

internal static class Checker
{
    private const string Method = "conservative-studentized-bootstrap-of-trial-means/2";
    private const int Resamples = 10_000;
    private const string SeedPrefix = "bluetusk-performance-bootstrap/v2|";
    private const double TailProbability = 0.005;
    private const double Tolerance = 1e-9;
    private static readonly string[] Metrics =
        ["throughput", "mean", "p95", "p99", "allocatedBytes", "cpuPerEvent", "peakRss", "gcCounters"];
    private static readonly string[] Families = ["Provider", "Streams", "Sync", "Live", "ControlPlane"];
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowDuplicateProperties = false, MaxDepth = 64 };

    // Two-sided 95% Student-t critical values t(0.975, df), df = 1..49, to six decimals.
    // The self-test re-derives each one by numerically integrating the t density.
    private static readonly double[] StudentT975 =
    [
        12.706205, 4.302653, 3.182446, 2.776445, 2.570582, 2.446912, 2.364624, 2.306004, 2.262157, 2.228139,
        2.200985, 2.178813, 2.160369, 2.144787, 2.131450, 2.119905, 2.109816, 2.100922, 2.093024, 2.085963,
        2.079614, 2.073873, 2.068658, 2.063899, 2.059539, 2.055529, 2.051831, 2.048407, 2.045230, 2.042272,
        2.039513, 2.036933, 2.034515, 2.032245, 2.030108, 2.028094, 2.026192, 2.024394, 2.022691, 2.021075,
        2.019541, 2.018082, 2.016692, 2.015368, 2.014103, 2.012896, 2.011741, 2.010635, 2.009575,
    ];

    private sealed record TrialMetrics(string Key, string Role, int Trial, string Path, string Sha256, Dictionary<string, double> Values);

    public static string Check(string osRoot, string commit, string os, string variantMapPath, string? reportOverride)
    {
        Require(Regex.IsMatch(commit, "^[0-9a-f]{40}$") && os is "windows" or "linux", "An exact commit and windows/linux OS are required.");
        var root = Path.GetFullPath(osRoot);
        var reportPath = reportOverride is null ? Path.Combine(root, "check-report.json") : Path.GetFullPath(reportOverride);
        Require(!File.Exists(reportPath), "A check report already exists; checks never overwrite evidence.");
        var summaryBytes = ReadFile(root, "summary.json");
        using var summaryDocument = JsonDocument.Parse(summaryBytes, DocumentOptions);
        var summary = summaryDocument.RootElement;
        Require(Int(summary, "schemaVersion") == 1 && Str(summary, "evidenceKind") == "bluetusk-performance-summary" &&
            Str(summary, "release") == "1.1.0" && Str(summary, "scope") == "Core" && Str(summary, "sourceCommit") == commit &&
            Str(summary, "os") == os && Str(summary, "architecture") == "x64" && !Bool(summary, "leadershipGatePassed"),
            "Summary identity, release, scope, commit, OS or gate status is invalid.");
        var diagnostic = Bool(summary, "diagnostic");
        var synthetic = Bool(summary, "synthetic");
        Require(!synthetic || diagnostic, "Synthetic summaries must be diagnostic.");

        var statistics = summary.GetProperty("statistics");
        var seed = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SeedPrefix + commit)));
        Require(Str(statistics, "method") == Method && Dbl(statistics, "confidenceLevel") == 0.95 && Dbl(statistics, "intervalConfidenceLevel") == 0.99 &&
            Int(statistics, "resamples") == Resamples && Str(statistics, "seed") == seed,
            "Summary statistics method, confidence, resample count or commit-bound seed differs from the specification.");

        var mapBytes = File.ReadAllBytes(variantMapPath);
        using var mapDocument = JsonDocument.Parse(mapBytes, DocumentOptions);
        Require(Str(summary.GetProperty("variantMap"), "sha256") == Convert.ToHexStringLower(SHA256.HashData(mapBytes)),
            "Summary was generated with a different variant map.");

        var rawBinding = summary.GetProperty("rawSamples");
        Require(Str(rawBinding, "path") == "raw-samples.json", "Raw-sample manifest path is not canonical.");
        var rawBytes = ReadFile(root, "raw-samples.json");
        Require(Convert.ToHexStringLower(SHA256.HashData(rawBytes)) == Str(rawBinding, "sha256") &&
            rawBytes.LongLength == Long(rawBinding, "bytes"), "Raw-sample manifest differs from its summary binding.");
        using var rawDocument = JsonDocument.Parse(rawBytes, DocumentOptions);
        var raw = rawDocument.RootElement;
        Require(Int(raw, "schemaVersion") == 1 && Str(raw, "evidenceKind") == "bluetusk-performance-raw-samples" &&
            Str(raw, "sourceCommit") == commit && Str(raw, "os") == os && Bool(raw, "diagnostic") == diagnostic &&
            Bool(raw, "synthetic") == synthetic, "Raw-sample manifest identity differs from the summary.");

        // Every file under raw/ must be bound exactly once, with matching bytes and digest.
        var bound = new Dictionary<string, string>(StringComparer.Ordinal);
        var inputs = raw.GetProperty("inputs").EnumerateArray().ToArray();
        Require(inputs.Length > 0, "No raw inputs are bound.");
        foreach (var input in inputs)
        {
            var id = Str(input, "id");
            Require(Regex.IsMatch(id, "^[a-z0-9][a-z0-9.-]{0,63}$"), "Raw input identifiers must be lowercase names.");
            foreach (var file in input.GetProperty("files").EnumerateArray())
            {
                var path = Str(file, "path");
                Require(path.StartsWith($"raw/{id}/", StringComparison.Ordinal), $"File '{path}' is outside its input directory.");
                var bytes = ReadFile(root, path);
                var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                Require(hash == Str(file, "sha256") && bytes.LongLength == Long(file, "bytes"), $"Raw file '{path}' differs from its binding.");
                Require(bound.TryAdd(path, hash), $"Raw file '{path}' is bound twice.");
            }
        }
        var actual = Directory.EnumerateFiles(Path.Combine(root, "raw"), "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        Require(actual.SetEquals(bound.Keys), "Raw directory contains unbound files or the manifest names files that are absent.");
        Require(diagnostic == inputs.Any(input => Bool(input, "diagnostic")) && synthetic == inputs.Any(input => Bool(input, "synthetic")),
            "Summary diagnostic/synthetic labels do not match the raw inputs.");

        var trials = new List<TrialMetrics>();
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            var id = Str(input, "id");
            var format = Str(input, "format");
            var indexPath = Str(input, "index");
            Require(bound.ContainsKey(indexPath), $"Input '{id}' index is not bound.");
            using var index = JsonDocument.Parse(ReadFile(root, indexPath), DocumentOptions);
            var produced = format switch
            {
                "provider-request-capture-index/1" => ReadProvider(root, id, index.RootElement, input, commit, os, mapDocument.RootElement, bound),
                "performance-trial-index/1" => ReadTrials(root, id, index.RootElement, input, commit, os, bound),
                _ => throw new CheckFailedException($"Input '{id}' has unknown format '{format}'."),
            };
            foreach (var trial in produced)
            {
                Require(!owner.TryGetValue(trial.Key, out var other) || other == id, $"Workload '{trial.Key}' comes from two inputs.");
                owner[trial.Key] = id;
            }
            trials.AddRange(produced);
        }
        Require(trials.Select(trial => trial.Sha256).Distinct(StringComparer.Ordinal).Count() == trials.Count,
            "Two trials share identical raw content; copied captures are not separate measurements.");

        Require(Dbl(statistics, "tailProbability") == TailProbability, "Statistics tail probability differs from 0.005.");
        var plans = new Dictionary<int, int[]>();
        var comparisons = summary.GetProperty("comparisons").EnumerateArray().ToArray();
        var groups = trials.GroupBy(trial => trial.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        Require(comparisons.Length == groups.Count, $"Summary has {comparisons.Length} comparisons; raw evidence supports {groups.Count}.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var comparison in comparisons)
        {
            var key = Str(comparison, "workloadKey");
            Require(seen.Add(key) && groups.ContainsKey(key), $"Comparison '{key}' is duplicated or has no raw trials.");
            var group = groups[key];
            var parts = key.Split('|');
            Require(parts.Length >= 3 && parts[0] == os && Families.Contains(parts[1]) && Str(comparison, "family") == parts[1],
                $"Comparison '{key}' has an invalid OS or family.");
            var mode = parts[2] == "primary-hot-path" && parts.Length == 3 ? "unique-primary" : parts[1] switch
            {
                "Provider" => "same-runtime",
                "Streams" or "Sync" or "Live" => "cross-runtime",
                "ControlPlane" => "unique",
                _ => throw new CheckFailedException($"Unknown family in '{key}'."),
            };
            var candidate = group.Where(trial => trial.Role == "candidate").OrderBy(trial => trial.Trial).ToArray();
            var reference = group.Where(trial => trial.Role == "reference").OrderBy(trial => trial.Trial).ToArray();
            var n = candidate.Length;
            Require(Str(comparison, "mode") == mode && Dbl(comparison, "confidenceLevel") == 0.95 && Int(comparison, "trials") == n &&
                Str(comparison, "inputId") == owner[key], $"Comparison '{key}' mode, confidence, trial count or input differs.");
            Require(n == reference.Length && n + reference.Length == group.Length && n >= (diagnostic ? 3 : 30) && n <= 50 &&
                candidate.Select(trial => trial.Trial).SequenceEqual(Enumerable.Range(0, n)) &&
                reference.Select(trial => trial.Trial).SequenceEqual(Enumerable.Range(0, n)),
                $"Comparison '{key}' does not have complete matched trials.");
            CompareTrials(key, "candidate", candidate, comparison.GetProperty("candidateTrials"));
            CompareTrials(key, "reference", reference, comparison.GetProperty("referenceTrials"));

            Require(Dbl(comparison, "tailProbability") == TailProbability,
                $"Comparison '{key}' tail probability differs from 0.005.");
            if (!plans.TryGetValue(n, out var plan))
            {
                plan = Plan(seed, n);
                plans[n] = plan;
            }
            var metrics = comparison.GetProperty("metrics");
            Require(metrics.EnumerateObject().Count() == Metrics.Length, $"Comparison '{key}' must carry exactly the eight required metrics.");
            foreach (var metric in Metrics)
            {
                var value = metrics.GetProperty(metric);
                foreach (var (role, roleTrials) in new[] { ("candidate", candidate), ("reference", reference) })
                {
                    var (point, lower, upper) = Bootstrap(roleTrials.Select(trial => trial.Values[metric]).ToArray(), plan);
                    Near(Dbl(value, role), point, $"{key} {metric} {role}");
                    Near(Dbl(value, role + "CiLower"), lower, $"{key} {metric} {role} lower bound");
                    Near(Dbl(value, role + "CiUpper"), upper, $"{key} {metric} {role} upper bound");
                }
            }
        }

        var summaryHash = Convert.ToHexStringLower(SHA256.HashData(summaryBytes));
        var report = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = 1,
            ["evidenceKind"] = "bluetusk-performance-check",
            ["checker"] = "eng/PerformanceEvidenceChecker (independent; BCL only)",
            ["sourceCommit"] = commit,
            ["os"] = os,
            ["diagnostic"] = diagnostic,
            ["synthetic"] = synthetic,
            ["summary"] = new Dictionary<string, object> { ["path"] = "summary.json", ["sha256"] = summaryHash, ["bytes"] = summaryBytes.LongLength },
            ["rawSamples"] = new Dictionary<string, object> { ["path"] = "raw-samples.json", ["sha256"] = Str(rawBinding, "sha256"), ["bytes"] = rawBytes.LongLength },
            ["variantMapSha256"] = Convert.ToHexStringLower(SHA256.HashData(mapBytes)),
            ["inputs"] = inputs.Length,
            ["rawFiles"] = bound.Count,
            ["trials"] = trials.Count,
            ["comparisons"] = comparisons.Length,
            ["tolerance"] = "relative 1e-9 on every recomputed trial value, point estimate and bound",
            ["result"] = "consistent",
            ["checkedUtc"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        };
        using (var stream = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, report, WriteOptions);
        }
        return $"Independent check passed for {os}: {comparisons.Length} comparisons recomputed from {trials.Count} raw trials in " +
            $"{bound.Count} bound files{(diagnostic ? " (DIAGNOSTIC" + (synthetic ? ", SYNTHETIC" : string.Empty) + ")" : string.Empty)}.";
    }

    private static List<TrialMetrics> ReadProvider(string root, string id, JsonElement index, JsonElement input, string commit,
        string os, JsonElement map, Dictionary<string, string> bound)
    {
        var variant = Str(index, "variant");
        var profile = Str(index, "captureProfile");
        Require(Int(index, "schemaVersion") == 1 && Str(index, "sourceCommit") == commit && Str(index, "os") == os &&
            Str(input, "family") == "Provider" && Str(input, "variant") == variant && Str(input, "captureProfile") == profile &&
            Bool(index, "diagnostic") == Bool(input, "diagnostic") && OptionalBool(index, "synthetic") == Bool(input, "synthetic") &&
            Int(index, "trials") == Int(input, "trials") && !Bool(index, "leadershipGatePassed"),
            $"Provider input '{id}' identity differs from the raw-sample manifest.");
        // Independent variant-map resolution.
        var entry = Str(map.GetProperty("variants").GetProperty(os), variant);
        var resolved = entry == "crossOsProfile" ? Str(map, "crossOsProfile") : entry;
        Require(resolved != "unresolved" && resolved == profile, $"Provider input '{id}' variant '{variant}' does not resolve to '{profile}'.");
        var definition = map.GetProperty("profiles").GetProperty(profile);
        Require(definition.GetProperty("hostOs").EnumerateArray().Any(host => host.GetString() == os), $"Profile '{profile}' cannot run on {os}.");
        var clientOs = Str(definition, "clientOs") switch
        {
            "host" => os,
            "other" => os == "windows" ? "linux" : "windows",
            var value => value,
        };
        Require(Str(index, "clientOs") == clientOs && Bool(index, "requireTls") == Bool(definition, "tls"),
            $"Provider input '{id}' client OS or TLS requirement differs from profile '{profile}'.");
        var networkName = definition.GetProperty("networkProfile");
        var networkProfile = index.TryGetProperty("networkProfile", out var declared) && declared.ValueKind == JsonValueKind.String
            ? declared.GetString() : null;
        if (networkName.ValueKind == JsonValueKind.Null)
        {
            Require(networkProfile is null, $"Provider input '{id}' declares network shaping its profile does not use.");
        }
        else
        {
            var file = $"raw/{id}/network-profile.json";
            Require(networkProfile == networkName.GetString() + "@sha256:" + (bound.TryGetValue(file, out var hash) ? hash : "missing"),
                $"Provider input '{id}' does not retain its observed network profile.");
        }
        var image = Str(index, "postgreSqlImage");
        var images = input.GetProperty("containerImageDigests").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Require(images.Contains(image) && images.All(item => Regex.IsMatch(item, "^\\S+@sha256:[0-9a-f]{64}$")),
            $"Provider input '{id}' image evidence is not digest pinned.");
        var warmup = Dbl(index, "warmupSeconds");
        var window = Dbl(index, "measurementSeconds");
        var result = new List<TrialMetrics>();
        var identities = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var record in index.GetProperty("records").EnumerateArray())
        {
            var key = Str(record, "workloadKey");
            var provider = Str(record, "provider");
            var path = $"raw/{id}/{Str(record, "path")}";
            Require(bound.TryGetValue(path, out var hash) && hash == Str(record, "sha256"), $"Provider record '{path}' is not bound by hash.");
            var scan = Scan(ReadFile(root, path));
            var feature = scan.Text("feature");
            var concurrency = scan.Long("concurrency");
            Require(scan.Long("schemaVersion") == 1 && scan.Text("evidenceKind") == "provider-individual-request-capture" &&
                scan.Text("sourceCommit") == commit && scan.Text("provider") == provider && provider is "bluetusk" or "npgsql" &&
                scan.Bool("diagnostic") == Bool(index, "diagnostic") &&
                key == $"{os}|Provider|{feature}|c={concurrency.ToString(CultureInfo.InvariantCulture)}|variant={variant}" &&
                scan.Double("warmupSeconds") == warmup && scan.Double("measurementSeconds") == window &&
                scan.Text("environment.os") == clientOs && scan.Text("environment.architecture") == "x64" &&
                scan.Text("environment.postgreSqlImage") == image && scan.Bool("environment.tlsActive") == Bool(index, "requireTls") &&
                (scan.TryText("method.networkShaping") ?? "not configured by this adapter") == (networkProfile ?? "not configured by this adapter") &&
                scan.Workers.Count == concurrency && scan.Long("measurement.completedOperations") == Long(record, "completedOperations"),
                $"Provider capture '{path}' identity, transport, window or coverage differs from its index.");
            var frequency = scan.Long("method.frequency");
            Require(scan.Long("measurement.elapsedTicks") >= window * frequency, $"Provider capture '{path}' is shorter than its declared window.");
            // Matched trials of one workload must share their environment and method description.
            var identity = scan.Prefix("environment.") + "\n" + scan.Prefix("method.");
            Require(!identities.TryGetValue(key, out var previous) || previous == identity, $"Workload '{key}' mixes environments or methods.");
            identities[key] = identity;
            result.Add(new(key, provider == "bluetusk" ? "candidate" : "reference", (int)Long(record, "trial"), path, hash!, Derive(scan, frequency, path)));
        }
        return result;
    }

    private static List<TrialMetrics> ReadTrials(string root, string id, JsonElement index, JsonElement input, string commit,
        string os, Dictionary<string, string> bound)
    {
        var family = Str(index, "family");
        Require(Int(index, "schemaVersion") == 1 && Str(index, "evidenceKind") == "bluetusk-performance-trial-index" &&
            Families.Contains(family) && Str(input, "family") == family && Str(index, "sourceCommit") == commit && Str(index, "os") == os &&
            Bool(index, "diagnostic") == Bool(input, "diagnostic") && Bool(index, "synthetic") == Bool(input, "synthetic") &&
            Int(index, "trials") == Int(input, "trials") && !Bool(index, "leadershipGatePassed"),
            $"Trial input '{id}' identity differs from the raw-sample manifest.");
        var images = index.GetProperty("containerImageDigests").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Require(images.Length > 0 && images.All(item => Regex.IsMatch(item, "^\\S+@sha256:[0-9a-f]{64}$")) &&
            images.SequenceEqual(input.GetProperty("containerImageDigests").EnumerateArray().Select(item => item.GetString()!)),
            $"Trial input '{id}' image evidence is not digest pinned or differs from the manifest.");
        var result = new List<TrialMetrics>();
        foreach (var record in index.GetProperty("records").EnumerateArray())
        {
            var key = Str(record, "workloadKey");
            var role = Str(record, "role");
            var trial = Int(record, "trial");
            var path = $"raw/{id}/{Str(record, "path")}";
            Require(bound.TryGetValue(path, out var hash) && hash == Str(record, "sha256"), $"Trial record '{path}' is not bound by hash.");
            var scan = Scan(ReadFile(root, path));
            Require(scan.Long("schemaVersion") == 1 && scan.Text("evidenceKind") == "bluetusk-performance-trial" &&
                scan.Text("family") == family && scan.Text("workloadKey") == key && scan.Text("role") == role && role is "candidate" or "reference" &&
                scan.Long("trial") == trial && scan.Text("sourceCommit") == commit && scan.Bool("diagnostic") == Bool(index, "diagnostic") &&
                scan.Bool("synthetic") == Bool(index, "synthetic") &&
                scan.Text("implementation") == Str(index, role == "candidate" ? "candidateImplementation" : "referenceImplementation") &&
                scan.Text("environment.os") == os && scan.Text("environment.architecture") == "x64" &&
                key.StartsWith($"{os}|{family}|", StringComparison.Ordinal),
                $"Trial capture '{path}' identity differs from its index.");
            result.Add(new(key, role, trial, path, hash!, Derive(scan, scan.Long("frequency"), path)));
        }
        return result;
    }

    private static Dictionary<string, double> Derive(RawScan scan, long frequency, string path)
    {
        var elapsed = scan.Long("measurement.elapsedTicks");
        var operations = scan.Long("measurement.completedOperations");
        var allocated = scan.Long("measurement.allocatedBytes");
        var cpu = scan.Double("measurement.cpuMilliseconds");
        var rss = scan.Long("measurement.peakWorkingSetBytes");
        var gen0 = scan.Long("measurement.gcCollections[0]");
        Require(frequency > 0 && elapsed > 0 && operations > 0 && allocated >= 0 && cpu >= 0 && double.IsFinite(cpu) && rss > 0 &&
            gen0 >= 0 && scan.Long("measurement.gcCollections[1]") >= 0 && scan.Long("measurement.gcCollections[2]") >= 0,
            $"Capture '{path}' has invalid counters.");
        long total = 0;
        for (var worker = 0; worker < scan.Workers.Count; worker++)
        {
            var samples = scan.Workers[worker];
            Require(samples.Length > 0 && samples.Length == scan.Long($"measurement.workers[{worker}].count"),
                $"Capture '{path}' worker {worker} sample count differs from its recorded count.");
            total += samples.Length;
        }
        Require(total == operations, $"Capture '{path}' samples do not equal completed operations.");
        var sorted = new long[total];
        var offset = 0;
        foreach (var samples in scan.Workers)
        {
            samples.CopyTo(sorted, offset);
            offset += samples.Length;
        }
        Array.Sort(sorted);
        Require(sorted[0] > 0 && sorted[^1] <= elapsed, $"Capture '{path}' has unresolvable or out-of-window samples.");
        Int128 sum = 0;
        foreach (var ticks in sorted) sum += ticks;
        var microseconds = 1_000_000d / frequency;
        long Rank(int percent) => sorted[(int)((percent * total + 99) / 100) - 1];
        return new(StringComparer.Ordinal)
        {
            ["throughput"] = total / ((double)elapsed / frequency),
            ["mean"] = (double)sum / total * microseconds,
            ["p95"] = Rank(95) * microseconds,
            ["p99"] = Rank(99) * microseconds,
            ["allocatedBytes"] = (double)allocated / total,
            ["cpuPerEvent"] = cpu * 1000 / total,
            ["peakRss"] = rss,
            ["gcCounters"] = gen0 * 1000d / total,
        };
    }

    private static void CompareTrials(string key, string role, TrialMetrics[] expected, JsonElement declared)
    {
        var items = declared.EnumerateArray().ToArray();
        Require(items.Length == expected.Length, $"{key} {role} trial count differs.");
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            Require(Int(item, "trial") == expected[index].Trial && Str(item, "path") == expected[index].Path &&
                Str(item, "sha256") == expected[index].Sha256, $"{key} {role} trial {index} is bound to another raw file.");
            var values = item.GetProperty("values");
            Require(values.EnumerateObject().Count() == Metrics.Length, $"{key} {role} trial {index} must carry exactly eight metrics.");
            foreach (var metric in Metrics)
            {
                Near(Dbl(values, metric), expected[index].Values[metric], $"{key} {role} trial {index} {metric}");
            }
        }
    }

    private static int[] Plan(string seedHex, int n)
    {
        // index[b, j] = UInt64LE(SHA-256(seed || Int32LE(n) || Int32LE(b) || Int32LE(j))[0..8]) mod n
        var seed = Convert.FromHexString(seedHex);
        var plan = new int[Resamples * n];
        var message = new byte[44];
        Buffer.BlockCopy(seed, 0, message, 0, 32);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(32), n);
        for (var b = 0; b < Resamples; b++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(36), b);
            for (var j = 0; j < n; j++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(40), j);
                var digest = SHA256.HashData(message);
                plan[b * n + j] = (int)(BinaryPrimitives.ReadUInt64LittleEndian(digest) % (ulong)n);
            }
        }
        return plan;
    }

    private static (double Point, double Lower, double Upper) Bootstrap(double[] values, int[] plan)
    {
        var n = values.Length;
        Require(n >= 2 && n <= 50 && plan.Length == n * Resamples && values.All(value => double.IsFinite(value) && value >= 0) &&
            plan.All(index => index >= 0 && index < n), "Invalid studentized bootstrap inputs.");
        if (values.All(value => value == values[0])) return (values[0], values[0], values[0]);
        var point = values.Sum() / n;
        Require(double.IsFinite(point), "Trial mean overflowed.");
        double Error(double[] sample)
        {
            // Independent implementation: Welford's online sample variance.
            double mean = 0, squared = 0;
            for (var i = 0; i < sample.Length; i++)
            {
                var difference = sample[i] - mean;
                mean += difference / (i + 1);
                squared += difference * (sample[i] - mean);
            }
            var error = Math.Sqrt(squared / (sample.Length * (sample.Length - 1.0)));
            Require(double.IsFinite(error), "Trial standard error overflowed.");
            return error;
        }
        var originalError = Error(values);
        Require(originalError > 0, "Nonconstant trial standard error underflowed.");
        var pivots = new double[Resamples];
        var sample = new double[n];
        for (var b = 0; b < Resamples; b++)
        {
            var total = 0d;
            for (var j = 0; j < n; j++)
            {
                sample[j] = values[plan[b * n + j]];
                total += sample[j];
            }
            var mean = total / n;
            var error = Error(sample);
            pivots[b] = error == 0
                ? mean == point ? 0 : Math.CopySign(double.PositiveInfinity, mean - point)
                : (mean - point) / error;
        }
        Array.Sort(pivots);
        var lowerIndex = (int)Math.Floor(Resamples * TailProbability);
        var upperIndex = (int)Math.Ceiling(Resamples * (1 - TailProbability)) - 1;
        var lower = point - pivots[upperIndex] * originalError;
        var upper = point - pivots[lowerIndex] * originalError;
        Require(double.IsFinite(lower) && double.IsFinite(upper), "Studentized interval is unbounded; more independent trials are required.");
        return (point, Math.Max(0, Math.Min(lower, point)), Math.Max(upper, point));
    }

    // Hart (1968) / West (2005) double-precision cumulative normal.
    private static double NormalCdf(double x)
    {
        var absolute = Math.Abs(x);
        double result;
        if (absolute > 37)
        {
            result = 0;
        }
        else
        {
            var exponential = Math.Exp(-absolute * absolute / 2);
            if (absolute < 7.07106781186547)
            {
                var numerator = 3.52624965998911E-02 * absolute + 0.700383064443688;
                numerator = numerator * absolute + 6.37396220353165;
                numerator = numerator * absolute + 33.912866078383;
                numerator = numerator * absolute + 112.079291497871;
                numerator = numerator * absolute + 221.213596169931;
                numerator = numerator * absolute + 220.206867912376;
                var denominator = 8.83883476483184E-02 * absolute + 1.75566716318264;
                denominator = denominator * absolute + 16.064177579207;
                denominator = denominator * absolute + 86.7807322029461;
                denominator = denominator * absolute + 296.564248779674;
                denominator = denominator * absolute + 637.333633378831;
                denominator = denominator * absolute + 793.826512519948;
                denominator = denominator * absolute + 440.413735824752;
                result = exponential * numerator / denominator;
            }
            else
            {
                var fraction = absolute + 0.65;
                fraction = absolute + 4 / fraction;
                fraction = absolute + 3 / fraction;
                fraction = absolute + 2 / fraction;
                fraction = absolute + 1 / fraction;
                result = exponential / fraction / 2.506628274631;
            }
        }
        return x > 0 ? 1 - result : result;
    }

    public static string SelfTest()
    {
        // 1. Re-derive every tabulated t value: integrate the t density from 0 to t(0.975, df) by Simpson's rule.
        for (var df = 1; df <= 49; df++)
        {
            var t = StudentT975[df - 1];
            var logConstant = LogGammaHalf(df + 1) - LogGammaHalf(df) - 0.5 * Math.Log(df * Math.PI);
            const int Steps = 200_000;
            var h = t / Steps;
            double Density(double x) => Math.Exp(logConstant - (df + 1) / 2.0 * Math.Log(1 + x * x / df));
            var area = Density(0) + Density(t);
            for (var step = 1; step < Steps; step++) area += (step % 2 == 0 ? 2 : 4) * Density(step * h);
            var cdf = 0.5 + area * h / 3;
            Require(Math.Abs(cdf - 0.975) < 2e-7, $"Tabulated t(0.975, {df}) = {t} integrates to {cdf}.");
        }
        // 2. Normal CDF reference values.
        Require(Math.Abs(NormalCdf(-1.959963984540054) - 0.025) < 1e-12 && Math.Abs(NormalCdf(1) - 0.841344746068543) < 1e-12 &&
            Math.Abs(NormalCdf(-3) - 0.0013498980316301) < 1e-14, "Cumulative normal reference values differ.");
        // 3. Percentile arithmetic and nearest rank.
        var plan = Plan(new string('0', 64), 10);
        Require(plan.All(index => index is >= 0 and < 10), "Plan indices must be in range.");
        var (point, lower, upper) = Bootstrap(Enumerable.Range(1, 10).Select(value => (double)value).ToArray(), plan);
        Require(point == 5.5 && lower < 5.5 && upper > 5.5 && lower >= 0, "Bootstrap bounds must bracket the mean.");
        var constant = Bootstrap(Enumerable.Repeat(5d, 10).ToArray(), plan);
        Require(constant == (5, 5, 5), "Constant samples need degenerate bounds.");
        var scan = Scan(Encoding.UTF8.GetBytes(
            "{\"measurement\":{\"workers\":[{\"requestTicks\":[3,1,2],\"count\":3},{\"requestTicks\":[4],\"count\":1}],\"gcCollections\":[1,0,0]," +
            "\"elapsedTicks\":10,\"completedOperations\":4,\"allocatedBytes\":8,\"cpuMilliseconds\":0.5,\"peakWorkingSetBytes\":9}}"));
        var derived = Derive(scan, 1_000_000, "inline");
        Require(derived["mean"] == 2.5 && derived["p95"] == 4 && derived["p99"] == 4 && Math.Abs(derived["throughput"] - 400_000) < 1e-6 &&
            derived["gcCounters"] == 250, "Raw derivation of a known capture is wrong.");
        try
        {
            _ = Scan(Encoding.UTF8.GetBytes("{\"a\":1,\"a\":2}"));
            throw new InvalidOperationException("Duplicate JSON properties were accepted.");
        }
        catch (CheckFailedException)
        {
        }
        return "Independent checker self-test passed: 49 tabulated t values re-derived by numerical integration, normal references, plan bounds, bootstrap bracketing, nearest-rank derivation and duplicate-property rejection.";
    }

    private static double LogGammaHalf(int twiceArgument)
    {
        // log Gamma(k / 2) for positive integer k, by exact recurrence from Gamma(1/2) = sqrt(pi) and Gamma(1) = 1.
        var value = twiceArgument % 2 == 0 ? 0 : 0.5 * Math.Log(Math.PI);
        for (var k = twiceArgument % 2 == 0 ? 2 : 1; k < twiceArgument; k += 2) value += Math.Log(k / 2.0);
        return value;
    }

    private sealed class RawScan
    {
        public Dictionary<string, string> Scalars { get; } = new(StringComparer.Ordinal);
        public List<long[]> Workers { get; } = [];
        public string Text(string path) => Scalars.TryGetValue(path, out var value) ? value : throw new CheckFailedException($"Raw capture is missing '{path}'.");
        public string? TryText(string path) => Scalars.TryGetValue(path, out var value) ? value : null;
        public long Long(string path) => long.Parse(Text(path), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        public double Double(string path) => double.Parse(Text(path), NumberStyles.Float, CultureInfo.InvariantCulture);
        public bool Bool(string path) => Text(path) switch { "true" => true, "false" => false, _ => throw new CheckFailedException($"'{path}' is not a boolean.") };
        public string Prefix(string prefix) => string.Join('\n', Scalars.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));
    }

    private sealed class Frame(string path, bool isArray)
    {
        public string Path { get; } = path;
        public bool IsArray { get; } = isArray;
        public int Index { get; set; }
        public HashSet<string> Names { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Single-pass streaming scan: request-tick arrays go straight into long buffers, all other scalars by path.</summary>
    private static RawScan Scan(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
        var scan = new RawScan();
        var stack = new Stack<Frame>();
        string? property = null;
        string Child()
        {
            if (stack.Count == 0) return string.Empty;
            var top = stack.Peek();
            if (top.IsArray) return top.Path + "[" + top.Index.ToString(CultureInfo.InvariantCulture) + "]";
            Require(property is not null, "Malformed JSON object.");
            return top.Path.Length == 0 ? property! : top.Path + "." + property;
        }
        void Advance()
        {
            property = null;
            if (stack.Count > 0 && stack.Peek().IsArray) stack.Peek().Index++;
        }
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    property = reader.GetString();
                    Require(stack.Peek().Names.Add(property!), $"Duplicate JSON property '{property}'.");
                    break;
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    {
                        var path = Child();
                        if (reader.TokenType == JsonTokenType.StartArray && Regex.IsMatch(path, "^measurement\\.workers\\[\\d+\\]\\.requestTicks$"))
                        {
                            var buffer = new List<long>();
                            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                            {
                                long ticks = 0;
                                Require(reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out ticks), "Request ticks must be integers.");
                                buffer.Add(ticks);
                            }
                            var worker = int.Parse(path["measurement.workers[".Length..path.IndexOf(']', StringComparison.Ordinal)], CultureInfo.InvariantCulture);
                            Require(worker == scan.Workers.Count, "Worker sample arrays must appear in order.");
                            scan.Workers.Add(buffer.ToArray());
                            Advance();
                            break;
                        }
                        stack.Push(new Frame(path, reader.TokenType == JsonTokenType.StartArray));
                        property = null;
                        break;
                    }
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    stack.Pop();
                    Advance();
                    break;
                default:
                    {
                        var path = Child();
                        var value = reader.TokenType switch
                        {
                            JsonTokenType.String => reader.GetString()!,
                            JsonTokenType.True => "true",
                            JsonTokenType.False => "false",
                            JsonTokenType.Null => "null",
                            _ => Encoding.UTF8.GetString(reader.ValueSpan),
                        };
                        Require(scan.Scalars.Count < 200_000 && scan.Scalars.TryAdd(path, value), $"Raw capture scalar '{path}' is duplicated or excessive.");
                        Advance();
                        break;
                    }
            }
        }
        return scan;
    }

    private static byte[] ReadFile(string root, string relative)
    {
        Require(!Path.IsPathRooted(relative) && !relative.Contains('\\') && !relative.Contains(':') &&
            relative.Split('/').All(segment => segment is not ("" or "." or "..")), $"Path '{relative}' is not normalized and relative.");
        var path = root;
        foreach (var segment in relative.Split('/'))
        {
            path = Path.Combine(path, segment);
            Require(File.Exists(path) || Directory.Exists(path), $"'{relative}' is missing.");
            Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, $"'{relative}' traverses a link or junction.");
        }
        var bytes = File.ReadAllBytes(path);
        Require(bytes.Length > 0, $"'{relative}' is empty.");
        return bytes;
    }

    private static void Near(double declared, double recomputed, string what) =>
        Require(double.IsFinite(declared) && Math.Abs(declared - recomputed) <= Tolerance * Math.Max(Math.Abs(declared), Math.Abs(recomputed)) + 1e-12,
            $"{what}: summary {declared.ToString("R", CultureInfo.InvariantCulture)} differs from recomputed {recomputed.ToString("R", CultureInfo.InvariantCulture)}.");

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new CheckFailedException($"'{name}' must be a string.");
    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new CheckFailedException($"'{name}' must be a boolean.");
    private static bool OptionalBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out _) && Bool(element, name);
    private static double Dbl(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : throw new CheckFailedException($"'{name}' must be a number.");
    private static long Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : throw new CheckFailedException($"'{name}' must be an integer.");
    private static int Int(JsonElement element, string name) => checked((int)Long(element, name));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new CheckFailedException(message);
    }
}
