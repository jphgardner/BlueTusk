using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace BlueTusk.Benchmarks;

/// <summary>
/// No-database tests for the performance-evidence generator and its statistics. Every fixture
/// written here is SYNTHETIC and labelled diagnostic: it exercises guards, never performance.
/// </summary>
internal static class PerformanceEvidenceSelfTests
{
    private const string Commit = "2222222222222222222222222222222222222222";
    private const string PostgreSqlImage = "postgres:synthetic@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ToxiproxyImage = "toxiproxy:synthetic@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ReferenceImage = "reference:synthetic@sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static async Task RunAsync(string contractPath, string variantMapPath)
    {
        TestStatistics();
        var coverage = TestCoverage();
        await TestGeneratorAsync(contractPath, variantMapPath);
        Console.WriteLine("Performance evidence self-tests passed: Student-t and normal references, deterministic SHA-256 plans, " +
            $"expanded-percentile bounds, simulated coverage (normal {coverage.Normal:0.000}, lognormal {coverage.Lognormal:0.000} at n=10), " +
            "and generator rejection of unresolved variants, copies, partial pairs, short runs, unbound files, out-of-contract keys and unlabelled synthetic data.");
    }

    private static void TestStatistics()
    {
        // NIST 0.975 Student-t critical values, rounded to three decimals.
        foreach (var (df, value) in new[] { (1, 12.706), (2, 4.303), (5, 2.571), (9, 2.262), (29, 2.045), (49, 2.010) })
        {
            Assert(Math.Abs(PerformanceEvidenceStatistics.StudentTQuantile(0.975, df) - value) < 6e-4, $"t quantile df={df} is wrong.");
        }
        Assert(Math.Abs(PerformanceEvidenceStatistics.NormalCdf(-1.959963984540054) - 0.025) < 1e-9, "Normal tail is wrong.");
        Assert(Math.Abs(PerformanceEvidenceStatistics.NormalCdf(1) - 0.841344746068543) < 1e-9, "Normal CDF is wrong.");
        Assert(Math.Abs(PerformanceEvidenceStatistics.NormalCdf(0) - 0.5) < 1e-12, "Normal median is wrong.");
        var alpha10 = PerformanceEvidenceStatistics.ExpandedAlpha(10);
        var alpha50 = PerformanceEvidenceStatistics.ExpandedAlpha(50);
        Assert(alpha10 is > 0.015 and < 0.02 && alpha50 is > 0.04 and < 0.05 && alpha10 < alpha50,
            "Expanded alpha must widen small-sample intervals and approach the nominal level.");

        var seed = PerformanceEvidenceStatistics.DeriveSeed(Commit);
        Assert(seed == PerformanceEvidenceStatistics.DeriveSeed(Commit) && seed != PerformanceEvidenceStatistics.DeriveSeed(new string('3', 40)),
            "Seeds must be deterministic and bound to the commit.");
        var plan = PerformanceEvidenceStatistics.CreatePlan(seed, 10);
        Assert(plan.SequenceEqual(PerformanceEvidenceStatistics.CreatePlan(seed, 10)), "Resampling plans must be deterministic.");
        Assert(!plan.SequenceEqual(PerformanceEvidenceStatistics.CreatePlan(PerformanceEvidenceStatistics.DeriveSeed(new string('3', 40)), 10)),
            "Different commits must use different plans.");
        var counts = new int[10];
        foreach (var index in plan) counts[index]++;
        Assert(counts.All(count => Math.Abs(count - 10_000) < 600), "Resampling plan draws must be approximately uniform.");

        var constant = PerformanceEvidenceStatistics.Estimate(Enumerable.Repeat(7d, 10).ToArray(), plan, alpha10);
        Assert(constant.Point == 7 && constant.Lower == 7 && constant.Upper == 7, "Constant trials need a degenerate interval.");
        var ramp = PerformanceEvidenceStatistics.Estimate(Enumerable.Range(1, 10).Select(value => (double)value).ToArray(), plan, alpha10);
        Assert(ramp.Point == 5.5 && ramp.Lower < 5.5 && ramp.Upper > 5.5 && ramp.Lower >= 1 && ramp.Upper <= 10,
            "Bootstrap bounds must bracket the trial mean.");
        Reject(() => PerformanceEvidenceStatistics.Estimate([1, double.NaN], PerformanceEvidenceStatistics.CreatePlan(seed, 2), 0.01));
        Reject(() => PerformanceEvidenceStatistics.Estimate([1, -1], PerformanceEvidenceStatistics.CreatePlan(seed, 2), 0.01));
        Reject(() => PerformanceEvidenceStatistics.Estimate([1, 2, 3], plan, alpha10));
    }

    private static (double Normal, double Lognormal) TestCoverage()
    {
        // Simulated coverage of the true mean at the qualification minimum of ten trials.
        const int Trials = 10, Repetitions = 600;
        var plan = PerformanceEvidenceStatistics.CreatePlan(PerformanceEvidenceStatistics.DeriveSeed(Commit), Trials);
        var alpha = PerformanceEvidenceStatistics.ExpandedAlpha(Trials);
        var random = new Random(20261003);
        double Gaussian() => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
        int normal = 0, lognormal = 0;
        var lognormalMean = Math.Exp(0.5 * 0.5 * 0.5);
        for (var repetition = 0; repetition < Repetitions; repetition++)
        {
            var a = PerformanceEvidenceStatistics.Estimate(
                Enumerable.Range(0, Trials).Select(_ => 100 + 10 * Gaussian()).ToArray(), plan, alpha);
            if (a.Lower <= 100 && a.Upper >= 100) normal++;
            var b = PerformanceEvidenceStatistics.Estimate(
                Enumerable.Range(0, Trials).Select(_ => Math.Exp(0.5 * Gaussian())).ToArray(), plan, alpha);
            if (b.Lower <= lognormalMean && b.Upper >= lognormalMean) lognormal++;
        }
        var result = ((double)normal / Repetitions, (double)lognormal / Repetitions);
        Assert(result.Item1 >= 0.92, $"Normal-data coverage {result.Item1:0.000} is too low for a 95% claim.");
        Assert(result.Item2 >= 0.88, $"Skewed-data coverage {result.Item2:0.000} is unexpectedly low.");
        return result;
    }

    private static async Task TestGeneratorAsync(string contractPath, string variantMapPath)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "bluetusk-performance-generator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var adoptedMap = Path.Combine(temporary, "adopted-variant-map.json");
            await File.WriteAllTextAsync(adoptedMap, (await File.ReadAllTextAsync(variantMapPath))
                .Replace("\"crossOsProfile\": \"unresolved\"", "\"crossOsProfile\": \"synthetic-foreign-client\"", StringComparison.Ordinal)
                .Replace("\"profiles\": {", "\"profiles\": {\n    \"synthetic-foreign-client\": { \"clientOs\": \"other\", \"tls\": false, \"networkProfile\": null, \"hostOs\": [\"windows\", \"linux\"] },", StringComparison.Ordinal));
            var serial = 0;
            async Task<string> CaseAsync(Func<string, bool> filter, int trials = 3)
            {
                var root = Path.Combine(temporary, $"case-{serial++}");
                await SynthesizeAsync(root, Commit, "windows", "win", contractPath, adoptedMap, filter, trials);
                return root;
            }
            static bool Small(string key) => key.Contains("|parameterized-scalar|c=1|", StringComparison.Ordinal) ||
                key.StartsWith("windows|Streams|changes=1|", StringComparison.Ordinal) || key.EndsWith("|Live|primary-hot-path", StringComparison.Ordinal);

            var valid = await CaseAsync(Small);
            var result = await PerformanceEvidenceGenerator.GenerateAsync(valid, Commit, "windows", contractPath, adoptedMap, allowSynthetic: true);
            Assert(result.Comparisons == 4 + 4 + 1 && result.Diagnostic && result.Synthetic, "Synthetic generation must stay diagnostic and complete.");
            await RejectAsync(() => PerformanceEvidenceGenerator.GenerateAsync(valid, Commit, "windows", contractPath, adoptedMap, true));

            var unresolved = await CaseAsync(Small);
            await RejectAsync(() => PerformanceEvidenceGenerator.GenerateAsync(unresolved, Commit, "windows", contractPath, variantMapPath, true));
            var unlabelled = await CaseAsync(Small);
            await RejectAsync(() => PerformanceEvidenceGenerator.GenerateAsync(unlabelled, Commit, "windows", contractPath, adoptedMap, false));
            var wrongCommit = await CaseAsync(Small);
            await RejectAsync(() => PerformanceEvidenceGenerator.GenerateAsync(wrongCommit, new string('3', 40), "windows", contractPath, adoptedMap, true));
            var wrongOs = await CaseAsync(Small);
            await RejectAsync(() => PerformanceEvidenceGenerator.GenerateAsync(wrongOs, Commit, "linux", contractPath, adoptedMap, true));
            var shortRun = await CaseAsync(Small, trials: 2);
            await RejectAsync(() => PerformanceEvidenceGenerator.GenerateAsync(shortRun, Commit, "windows", contractPath, adoptedMap, true));

            // A same-OS capture copied into the cross-OS slot is rejected even when relabelled.
            var copied = await CaseAsync(Small);
            var native = Path.Combine(copied, "raw", "provider-windows");
            var foreign = Path.Combine(copied, "raw", "provider-linux");
            Directory.Delete(foreign, recursive: true);
            Directory.CreateDirectory(foreign);
            foreach (var file in Directory.GetFiles(native))
            {
                var text = (await File.ReadAllTextAsync(file)).Replace("|variant=windows", "|variant=linux", StringComparison.Ordinal)
                    .Replace("\"variant\":\"windows\"", "\"variant\":\"linux\"", StringComparison.Ordinal)
                    .Replace("\"captureProfile\":\"native\"", "\"captureProfile\":\"synthetic-foreign-client\"", StringComparison.Ordinal)
                    .Replace("\"clientOs\":\"windows\"", "\"clientOs\":\"linux\"", StringComparison.Ordinal);
                await File.WriteAllTextAsync(Path.Combine(foreign, Path.GetFileName(file)), text);
            }
            await RejectAsync(() => PerformanceEvidenceGenerator.GenerateAsync(copied, Commit, "windows", contractPath, adoptedMap, true));

            var partial = await CaseAsync(Small);
            var streams = Path.Combine(partial, "raw", "streams");
            var index = JsonSerializer.Deserialize<PerformanceEvidenceGenerator.TrialIndex>(
                await File.ReadAllBytesAsync(Path.Combine(streams, PerformanceEvidenceGenerator.TrialIndexName)), JsonOptions)!;
            var dropped = index.Records.First(record => record.Role == "reference");
            File.Delete(Path.Combine(streams, dropped.Path));
            await SaveAsync(Path.Combine(streams, PerformanceEvidenceGenerator.TrialIndexName),
                index with { Records = index.Records.Where(record => record != dropped).ToArray() });
            await RejectAsync(() => PerformanceEvidenceGenerator.GenerateAsync(partial, Commit, "windows", contractPath, adoptedMap, true));

            var unbound = await CaseAsync(Small);
            await File.WriteAllTextAsync(Path.Combine(unbound, "raw", "streams", "notes.json"), "{}");
            await RejectAsync(() => PerformanceEvidenceGenerator.GenerateAsync(unbound, Commit, "windows", contractPath, adoptedMap, true));

            var tampered = await CaseAsync(Small);
            var tamperedFile = Directory.GetFiles(Path.Combine(tampered, "raw", "streams"), "*-reference.json")[0];
            await File.AppendAllTextAsync(tamperedFile, " ");
            await RejectAsync(() => PerformanceEvidenceGenerator.GenerateAsync(tampered, Commit, "windows", contractPath, adoptedMap, true));

            var outside = await CaseAsync(Small);
            var outsideIndexPath = Path.Combine(outside, "raw", "streams", PerformanceEvidenceGenerator.TrialIndexName);
            var outsideIndex = JsonSerializer.Deserialize<PerformanceEvidenceGenerator.TrialIndex>(await File.ReadAllBytesAsync(outsideIndexPath), JsonOptions)!;
            var renamed = new List<PerformanceEvidenceGenerator.TrialEntry>();
            foreach (var record in outsideIndex.Records)
            {
                var path = Path.Combine(outside, "raw", "streams", record.Path);
                var json = (await File.ReadAllTextAsync(path)).Replace("scenario=snapshot", "scenario=replay", StringComparison.Ordinal);
                await File.WriteAllTextAsync(path, json);
                renamed.Add(record with
                {
                    WorkloadKey = record.WorkloadKey.Replace("scenario=snapshot", "scenario=replay", StringComparison.Ordinal),
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))),
                });
            }
            await SaveAsync(outsideIndexPath, outsideIndex with { Records = renamed.ToArray() });
            await RejectAsync(() => PerformanceEvidenceGenerator.GenerateAsync(outside, Commit, "windows", contractPath, adoptedMap, true));
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    /// <summary>
    /// Writes SYNTHETIC raw inputs for one OS: Provider captures in the request-capture format and every other
    /// Core family in the family-neutral trial format. "win" makes the candidate about 30% cheaper; "tie" makes
    /// both identical. Used only by self-tests; the indexes are labelled synthetic and diagnostic.
    /// </summary>
    internal static async Task SynthesizeAsync(string osRoot, string commit, string os, string outcome, string contractPath,
        string variantMapPath, Func<string, bool>? filter = null, int trials = 3)
    {
        if (outcome is not ("win" or "tie"))
        {
            throw new ArgumentException("Synthetic outcome must be 'win' or 'tie'.");
        }
        var root = Path.GetFullPath(osRoot);
        if (Directory.Exists(root) || File.Exists(root))
        {
            throw new IOException("Synthetic fixtures need a new directory.");
        }
        using var contract = JsonDocument.Parse(await File.ReadAllBytesAsync(contractPath));
        using var map = JsonDocument.Parse(await File.ReadAllBytesAsync(variantMapPath));
        var expected = PerformanceEvidenceGenerator.ExpectedCoreWorkloads(contract.RootElement, os)
            .Where(pair => filter?.Invoke(pair.Key) ?? true).Select(pair => pair.Key).ToArray();
        var random = new Random(os == "windows" ? 101 : 202);
        var factor = outcome == "win" ? 0.7 : 1.0;

        foreach (var variantGroup in expected.Where(key => key.Split('|') is [_, "Provider", _, _, _])
            .GroupBy(key => key.Split('|')[4]["variant=".Length..]))
        {
            var variant = variantGroup.Key;
            var profile = PerformanceEvidenceGenerator.ResolveProfile(map.RootElement, os, variant);
            var directory = Path.Combine(root, "raw", "provider-" + variant);
            Directory.CreateDirectory(directory);
            string? networkProfile = null;
            var images = new List<string> { PostgreSqlImage };
            if (profile.NetworkProfile is not null)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    Profile = profile.NetworkProfile,
                    ToxiproxyImage,
                    Synthetic = true,
                    Toxics = new[] { new { Name = "latency-downstream", Type = "latency" } },
                }, JsonOptions);
                await File.WriteAllBytesAsync(Path.Combine(directory, PerformanceEvidenceGenerator.NetworkProfileName), bytes);
                networkProfile = profile.NetworkProfile + "@sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
                images.Add(ToxiproxyImage);
            }
            var entries = new List<ProviderRequestAnalysis.Entry>();
            foreach (var key in variantGroup)
            {
                var parts = key.Split('|');
                var feature = parts[2];
                var concurrency = int.Parse(parts[3]["c=".Length..], CultureInfo.InvariantCulture);
                var perWorker = Math.Max(2, 64 / concurrency);
                var environment = JsonSerializer.SerializeToElement(new
                {
                    os = profile.ClientOs,
                    architecture = "x64",
                    postgreSqlImage = PostgreSqlImage,
                    tlsActive = profile.Tls,
                    referenceAssembly = "10.0.3",
                    harnessAssembly = "1.1.0+" + commit,
                    candidateAssembly = "1.1.0+" + commit,
                    runtime = "synthetic-test-only",
                });
                var method = JsonSerializer.SerializeToElement(new
                {
                    frequency = 1_000_000,
                    sampleBufferCapacityBytes = (long)perWorker * concurrency * sizeof(long),
                    networkShaping = networkProfile ?? "not configured by this adapter",
                });
                for (var trial = 0; trial < trials; trial++)
                {
                    foreach (var provider in new[] { "bluetusk", "npgsql" })
                    {
                        var window = SyntheticWindow(random, concurrency, perWorker, provider == "bluetusk" ? factor : 1.0);
                        var capture = new ProviderRequestAnalysis.Capture(1, "provider-individual-request-capture", true, commit,
                            provider, feature, concurrency, 0.1, 0.1, perWorker, environment, method, window);
                        var name = $"{feature}-c{concurrency}-trial{trial}-{provider}.json";
                        var hash = await SaveAsync(Path.Combine(directory, name), capture);
                        entries.Add(new(key, trial, provider, name, hash, window.CompletedOperations));
                    }
                }
            }
            await SaveAsync(Path.Combine(directory, PerformanceEvidenceGenerator.ProviderIndexName), new ProviderRequestAnalysis.Index(
                1, commit, true, true, new string('a', 64), os, variant, PostgreSqlImage, trials, 0.1, 0.1, entries.ToArray(), false,
                profile.Name, profile.ClientOs, profile.Tls, networkProfile, images.ToArray(), Synthetic: true));
        }

        foreach (var familyGroup in expected.Where(key => !(key.Split('|') is [_, "Provider", _, _, _]))
            .GroupBy(key => key.Split('|')[1]))
        {
            var family = familyGroup.Key;
            var directory = Path.Combine(root, "raw", family == "Provider" ? "provider-primary-hot-path" : family.ToLowerInvariant());
            Directory.CreateDirectory(directory);
            var records = new List<PerformanceEvidenceGenerator.TrialEntry>();
            var serial = 0;
            foreach (var key in familyGroup)
            {
                for (var trial = 0; trial < trials; trial++)
                {
                    foreach (var role in new[] { "candidate", "reference" })
                    {
                        var file = new PerformanceEvidenceGenerator.TrialFile(1, "bluetusk-performance-trial", family, key, role, trial,
                            commit, true, true, role == "candidate" ? "BlueTusk synthetic" : "Reference synthetic",
                            JsonSerializer.SerializeToElement(new { os, architecture = "x64", runtime = "synthetic-test-only" }),
                            1_000_000, SyntheticWindow(random, 4, 16, role == "candidate" ? factor : 1.0));
                        var name = $"w{serial:D4}-trial{trial}-{role}.json";
                        var hash = await SaveAsync(Path.Combine(directory, name), file);
                        records.Add(new(key, trial, role, name, hash));
                    }
                }
                serial++;
            }
            await SaveAsync(Path.Combine(directory, PerformanceEvidenceGenerator.TrialIndexName), new PerformanceEvidenceGenerator.TrialIndex(
                1, "bluetusk-performance-trial-index", family, commit, os, true, true, trials, "BlueTusk synthetic", "Reference synthetic",
                [PostgreSqlImage, ReferenceImage], records.ToArray(), false));
        }
    }

    private static ProviderRequestCapture.Window SyntheticWindow(Random random, int workers, int perWorker, double factor)
    {
        // Trial-level jitter of about 2%, plus a spread of request latencies inside each trial.
        double Jitter() => 1 + (random.NextDouble() - 0.5) * 0.04;
        var level = factor * Jitter();
        var samples = Enumerable.Range(0, workers).Select(worker => new ProviderRequestCapture.WorkerSamples(
            Enumerable.Range(0, perWorker).Select(index => Math.Max(1L, (long)Math.Round((100 + 2 * index + worker % 7) * level))).ToArray(),
            perWorker)).ToArray();
        var operations = (long)workers * perWorker;
        return new((long)Math.Round(150_000 * factor * Jitter()), operations, (long)Math.Round(operations * 1_000 * level),
            operations * 0.05 * level, (long)Math.Round(100_000_000 * level), [(int)Math.Round(operations * level), 0, 0], samples);
    }

    private static async Task<string> SaveAsync<T>(string path, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        await File.WriteAllBytesAsync(path, bytes);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid statistics input was accepted.");
    }

    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidDataException or IOException) { return; }
        throw new InvalidOperationException("Invalid synthetic performance evidence was accepted by the generator.");
    }
}
