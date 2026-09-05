using System.Security.Cryptography;
using System.Text.Json;

namespace BlueTusk.Benchmarks;

internal static class ProviderRequestAnalysisSelfTests
{
    private const string Commit = "1111111111111111111111111111111111111111";
    private const string Image = "postgres:fixture@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Workload = "windows|Provider|parameterized-scalar|c=2|variant=windows";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static async Task RunAsync()
    {
        var window = CreateWindow();
        var metrics = ProviderRequestStatistics.Derive(window, 1_000_000);
        Near(metrics["meanUs"], 50.5);
        Near(metrics["p95Us"], 95);
        Near(metrics["p99Us"], 99);
        Near(metrics["allocatedBytesPerOperation"], 32);
        Near(metrics["operationsPerSecond"], 1000);
        Near(metrics["cpuUsPerOperation"], 20);
        Near(metrics["peakRssBytes"], 1_000_000);
        Reject(() => ProviderRequestStatistics.Derive(window with { CompletedOperations = 101 }, 1_000_000));
        Reject(() => ProviderRequestStatistics.Derive(window with { AllocatedBytes = -1 }, 1_000_000));
        Reject(() => ProviderRequestStatistics.Derive(window with { CpuMilliseconds = double.NaN }, 1_000_000));
        Reject(() => ProviderRequestStatistics.Derive(window with { ElapsedTicks = 50 }, 1_000_000));
        Reject(() => ProviderRequestStatistics.Derive(window with { GcCollections = [0, -1, 0] }, 1_000_000));
        Reject(() => ProviderRequestStatistics.Derive(window with
        {
            Workers = [new([1, 2], 1)],
            CompletedOperations = 1,
        }, 1_000_000));
        Reject(() => ProviderRequestStatistics.Derive(window, 0));

        double[] logs = [-0.22, -0.21, -0.20, -0.19, -0.18];
        var candidate = logs.Select(Math.Exp).ToArray();
        var reference = Enumerable.Repeat(1d, 5).ToArray();
        var known = ProviderRequestStatistics.Compare(candidate, reference);
        Near(known.GeometricPairedRatio!.Value, Math.Exp(-0.2));
        var radius = 2.777 * Math.Sqrt(0.00025 / 5);
        Near(known.RatioCiLower!.Value, Math.Exp(-0.2 - radius));
        Near(known.RatioCiUpper!.Value, Math.Exp(-0.2 + radius));
        Assert(ProviderRequestStatistics.MeetsLowerTarget(known, 0.98), "Known synthetic improvement must meet the numerical target.");
        var tied = ProviderRequestStatistics.Compare(reference, reference);
        Assert(!ProviderRequestStatistics.MeetsLowerTarget(tied, 0.98), "A statistical tie must not meet the target.");
        var boundary = ProviderRequestStatistics.Compare(logs.Select(log => Math.Exp(log + 0.2) * 0.979).ToArray(), reference);
        Assert(boundary.GeometricPairedRatio < 0.98 && !ProviderRequestStatistics.MeetsLowerTarget(boundary, 0.98),
            "A point estimate below the threshold must not override a crossing confidence interval.");
        var shortRun = ProviderRequestStatistics.Compare(candidate[..4], reference[..4]);
        Assert(shortRun.RatioCiUpper is null && !ProviderRequestStatistics.MeetsLowerTarget(shortRun, 0.98),
            "Fewer than five pairs must not receive a confidence verdict.");
        var zero = ProviderRequestStatistics.Compare(new double[5], reference);
        Assert(zero.GeometricPairedRatio is null && zero.RatioCiUpper is null,
            "Zero measurements must not be replaced by positive values for log-ratio inference.");
        Reject(() => ProviderRequestStatistics.Compare([double.NaN], [1]));
        Reject(() => ProviderRequestStatistics.Compare([1, 2], [1]));
        Reject(() => ProviderRequestStatistics.Compare(Enumerable.Repeat(1d, 51).ToArray(), Enumerable.Repeat(1d, 51).ToArray()));

        await TestEvidenceAsync();
        Console.WriteLine("Provider analysis self-tests passed: raw metric derivation, known confidence limits, ties, insufficient trials, zero counters, hashes, identity, pairing, paths and metadata mismatch.");
    }

    private static ProviderRequestCapture.Window CreateWindow() => new(100_000, 100, 3200, 2, 1_000_000,
        [1, 0, 0], [new(Enumerable.Range(1, 50).Select(value => (long)value).ToArray(), 50),
            new(Enumerable.Range(51, 50).Select(value => (long)value).ToArray(), 50)]);

    private static async Task TestEvidenceAsync()
    {
        // Synthetic verifier fixtures only. Never register them as measured performance evidence.
        var root = Path.Combine(Path.GetTempPath(), "bluetusk-analysis-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var candidatePath = Path.Combine(root, "candidate.json");
        var referencePath = Path.Combine(root, "reference.json");
        var indexPath = Path.Combine(root, "capture-index.json");
        try
        {
            var candidate = CreateCapture("bluetusk");
            var reference = CreateCapture("npgsql");
            var candidateHash = await SaveAsync(candidatePath, candidate);
            var referenceHash = await SaveAsync(referencePath, reference);
            ProviderRequestAnalysis.Entry[] entries =
            [
                new(Workload, 0, "bluetusk", "candidate.json", candidateHash, 100),
                new(Workload, 0, "npgsql", "reference.json", referenceHash, 100),
            ];
            var index = new ProviderRequestAnalysis.Index(1, Commit, true, true, new string('a', 64),
                "windows", "windows", Image, 1, 0.1, 0.1, entries, false);
            _ = await SaveAsync(indexPath, index);
            var analysis = await ProviderRequestAnalysis.AnalyzeAsync(indexPath, Commit);
            Assert(analysis.Trials.Length == 2 && analysis.Comparisons.Length == 1 &&
                !analysis.Comparisons[0].ObservedNumericalTargetMet, "One synthetic trial is machinery validation, never a leadership pass.");

            async Task RejectIndexAsync(ProviderRequestAnalysis.Index invalid)
            {
                _ = await SaveAsync(indexPath, invalid);
                await RejectAsync(() => ProviderRequestAnalysis.AnalyzeAsync(indexPath, Commit));
            }
            await RejectIndexAsync(index with { SourceCommit = new string('2', 40) });
            await RejectIndexAsync(index with { Diagnostic = false });
            await RejectIndexAsync(index with { LeadershipGatePassed = true });
            await RejectIndexAsync(index with { Records = [entries[0]] });
            await RejectIndexAsync(index with { Records = [entries[0], entries[0]] });
            await RejectIndexAsync(index with { Records = [entries[0] with { Sha256 = new string('0', 64) }, entries[1]] });
            await RejectIndexAsync(index with { Records = [entries[0] with { CompletedOperations = 99 }, entries[1]] });
            await RejectIndexAsync(index with { Records = [entries[0] with { Trial = 1 }, entries[1]] });
            await RejectIndexAsync(index with { Records = [entries[0] with { Path = "../candidate.json" }, entries[1]] });
            await RejectIndexAsync(index with { Records = [entries[0] with { Path = candidatePath }, entries[1]] });

            // Even rehashing inconsistent raw data must not make it acceptable.
            var badHash = await SaveAsync(referencePath, reference with { Measurement = CreateWindow() with { CompletedOperations = 99 } });
            await RejectIndexAsync(index with { Records = [entries[0], entries[1] with { Sha256 = badHash, CompletedOperations = 99 }] });
            var badEnvironment = JsonSerializer.SerializeToElement(new
            {
                os = "windows",
                architecture = "x64",
                postgreSqlImage = Image,
                tlsActive = false,
                referenceAssembly = "10.0.3",
                harnessAssembly = "1.2.0+" + Commit,
                candidateAssembly = "1.2.0+" + Commit,
                runtime = "different-runtime",
            });
            badHash = await SaveAsync(referencePath, reference with { Environment = badEnvironment });
            await RejectIndexAsync(index with { Records = [entries[0], entries[1] with { Sha256 = badHash }] });
            badHash = await SaveAsync(referencePath, reference with { WarmupSeconds = 1 });
            await RejectIndexAsync(index with { Records = [entries[0], entries[1] with { Sha256 = badHash }] });
            badHash = await SaveAsync(referencePath, reference with
            {
                Method = JsonSerializer.SerializeToElement(new { frequency = 0, sampleBufferCapacityBytes = 1600 }),
            });
            await RejectIndexAsync(index with { Records = [entries[0], entries[1] with { Sha256 = badHash }] });

            foreach (var feature in ProviderRequestFixture.ContentionFeatures)
            {
                var requestedMultiplexing = feature.StartsWith("multiplexed-", StringComparison.Ordinal);
                async Task<ProviderRequestAnalysis.Index> SaveContentionPairAsync(int poolSize, bool multiplexing)
                {
                    var environment = JsonSerializer.SerializeToElement(new
                    {
                        os = "windows", architecture = "x64", postgreSqlImage = Image,
                        tlsActive = false, referenceAssembly = "10.0.3",
                        harnessAssembly = "1.2.0+" + Commit, candidateAssembly = "1.2.0+" + Commit,
                        runtime = "synthetic-test-only", poolSize, multiplexingConfigured = multiplexing,
                    });
                    var method = JsonSerializer.SerializeToElement(new
                    {
                        frequency = 1_000_000, sampleBufferCapacityBytes = 1600, contentionProbe = true,
                    });
                    var blueHash = await SaveAsync(candidatePath, candidate with
                    {
                        Feature = feature, Environment = environment, Method = method,
                    });
                    var npgHash = await SaveAsync(referencePath, reference with
                    {
                        Feature = feature, Environment = environment, Method = method,
                    });
                    var key = $"windows|Provider|{feature}|c=2|variant=windows";
                    var contentionIndex = index with
                    {
                        Records = [entries[0] with { WorkloadKey = key, Sha256 = blueHash },
                            entries[1] with { WorkloadKey = key, Sha256 = npgHash }],
                    };
                    _ = await SaveAsync(indexPath, contentionIndex);
                    return contentionIndex;
                }
                _ = await SaveContentionPairAsync(4, requestedMultiplexing);
                var contentionAnalysis = await ProviderRequestAnalysis.AnalyzeAsync(indexPath, Commit);
                Assert(contentionAnalysis.Comparisons.Length == 1, "Recognized contention pairs must be analyzable.");
                // Both providers agree on this incorrect metadata: pairing equality
                // alone must not allow an unsaturated/wrong-mode result to pass.
                await RejectIndexAsync(await SaveContentionPairAsync(64, requestedMultiplexing));
                await RejectIndexAsync(await SaveContentionPairAsync(4, !requestedMultiplexing));
            }
        }
        finally
        {
            // Delete only the three files created above and then the empty owned directory.
            File.Delete(candidatePath);
            File.Delete(referencePath);
            File.Delete(indexPath);
            Directory.Delete(root);
        }
    }

    private static ProviderRequestAnalysis.Capture CreateCapture(string provider) => new(1,
        "provider-individual-request-capture", true, Commit, provider, "parameterized-scalar", 2, 0.1, 0.1, 100,
        JsonSerializer.SerializeToElement(new
        {
            os = "windows",
            architecture = "x64",
            postgreSqlImage = Image,
            tlsActive = false,
            referenceAssembly = "10.0.3",
            harnessAssembly = "1.2.0+" + Commit,
            candidateAssembly = "1.2.0+" + Commit,
            runtime = "synthetic-test-only",
        }), JsonSerializer.SerializeToElement(new { frequency = 1_000_000, sampleBufferCapacityBytes = 1600 }), CreateWindow());

    private static async Task<string> SaveAsync<T>(string path, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        await File.WriteAllBytesAsync(path, bytes);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static void Near(double actual, double expected) =>
        Assert(Math.Abs(actual - expected) < 1e-10, $"Derived value {actual} did not match {expected}.");

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid synthetic measurement was accepted.");
    }

    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid synthetic evidence was accepted.");
    }
}
