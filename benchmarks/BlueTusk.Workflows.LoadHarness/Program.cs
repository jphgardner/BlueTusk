using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueTusk.Data;

namespace BlueTusk.Workflows.LoadHarness;

internal static partial class Program
{
    internal const string ApplicationName = "BlueTuskJobsWorkflowLoadHarness";

    private static async Task<int> Main(string[] args)
    {
        string? connection = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connection))
        {
            Console.Error.WriteLine("BLUETUSK_TEST_CONNECTION_STRING is required; use a disposable PostgreSQL database.");
            return 2;
        }

        try
        {
            if (args is ["--fault-worker", var jobsSchema, var workflowSchema])
            {
                return await FaultWorkerAsync(connection, jobsSchema, workflowSchema);
            }

            if (args is ["--fault-workflow-worker", var workflowJobsSchema, var faultWorkflowSchema])
            {
                return await FaultWorkflowWorkerAsync(connection, workflowJobsSchema, faultWorkflowSchema);
            }

            string profile = args.Length == 0 ? "quick" : args[0];
            Check(profile is "quick" or "matrix" or "soak" or "faults" or "qualification" or "storage", "unknown profile");
            int seconds = profile == "storage" ? 600 : profile == "qualification" ? 120 : profile == "soak" ? 30 : profile == "matrix" ? 10 : 3;
            string? output = null;
            for (int index = 1; index < args.Length; index++)
            {
                if (args[index] == "--seconds" && ++index < args.Length)
                {
                    seconds = int.Parse(args[index], System.Globalization.CultureInfo.InvariantCulture);
                    Check(seconds is >= 1 and <= 3600, "duration outside bound");
                }
                else if (args[index] == "--output" && ++index < args.Length)
                {
                    output = Path.GetFullPath(args[index]);
                }
                else
                {
                    throw new InvalidOperationException("Unknown harness option.");
                }
            }

            DateTimeOffset started = DateTimeOffset.UtcNow;
            string fingerprint = SourceFingerprint();
            var results = new List<CaseResult>();
            if (profile is not "faults" and not "qualification")
            {
                Console.WriteLine("Warming Jobs and Workflows paths; warmup results are excluded.");
                _ = await RunCaseAsync(connection, new LoadCase("warmup-jobs", "Jobs", 50, 1, 4, 64, 0, 0));
                _ = await RunCaseAsync(connection, new LoadCase("warmup-workflows", "Workflows", 8, 1, 4, 64, 0, 0));
            }

            var overload = new List<OverloadResult>();
            if (profile is "qualification" or "storage")
            {
                overload.Add(await RunOverloadAsync(connection, seconds, workflow: false, physicalStorage: profile == "storage", sampleOutputPrefix: output));
                overload.Add(await RunOverloadAsync(connection, seconds, workflow: true, physicalStorage: profile == "storage", sampleOutputPrefix: output));
            }
            foreach (var scenario in Cases(profile, seconds))
            {
                Console.WriteLine("Running " + scenario.Name);
                var result = await RunCaseAsync(connection, scenario);
                results.Add(result);
                Console.WriteLine($"{scenario.Name}: {result.Completed} durable completions, {result.CompletedPerDrainSecond:F1}/s, p99 {result.DurableLatencyMilliseconds.P99:F1} ms");
            }

            Console.WriteLine("Running process-death and ambiguous-COMMIT recovery scenarios.");
            var faults = new List<FaultResult>
            {
                await ProcessDeathAsync(connection),
                await WorkflowProcessDeathAsync(connection),
                await AmbiguousCommitAsync(connection),
                await NetworkPartitionAsync(connection, workflow: false),
                await NetworkPartitionAsync(connection, workflow: true),
            };
            await using var metadataSource = Source(connection, poolSize: 4);
            await using var metadataConnection = await metadataSource.OpenConnectionAsync();
            await using var metadataCommand = new BlueTuskCommand("SELECT version()", metadataConnection);
            string server = (string)(await metadataCommand.ExecuteScalarAsync(CancellationToken.None))!;
            var report = new HarnessReport(started, profile, RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription,
                RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount, server, fingerprint,
                "Local bounded harness with instrumentation/collector overhead. WAL and pg_stat_database counters are cluster/database scoped and may include concurrent work; snapshots may lag. RSS and waits are sampled every 100 ms. Durable latency includes backlog enqueue time. No production qualification or comparative performance leadership is established.",
                results, faults, overload);
            output ??= Path.GetFullPath(Path.Combine("benchmarks", "BlueTusk.Workflows.LoadHarness", "reports", "local-" + started.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".json"));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, HarnessJsonContext.Default.HarnessReport));
            Console.WriteLine("Report: " + output);
            return results.All(result => result.Verified) && faults.All(fault => fault.Passed) && overload.All(result => result.Verified) ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Load harness failed (" + exception.GetType().Name + "); no parameters or credentials are logged.");
            if (Environment.GetEnvironmentVariable("BLUETUSK_HARNESS_DIAGNOSTICS") == "1")
            {
                Console.Error.WriteLine(exception.StackTrace);
            }
            return 1;
        }
    }

    internal static BlueTuskDataSource Source(string connection, int poolSize)
    {
        var settings = new BlueTuskConnectionStringBuilder(connection)
        {
            ApplicationName = ApplicationName,
            MaximumPoolSize = poolSize,
            MinimumPoolSize = 0,
        };
        return BlueTuskDataSource.Create(settings.ConnectionString);
    }

    internal static void Check(bool condition, string invariant)
    {
        if (!condition)
        {
            Console.Error.WriteLine("Harness invariant failed: " + invariant);
            throw new InvalidOperationException("Harness invariant failed: " + invariant);
        }
    }

    internal static async Task ExecuteAsync(BlueTuskDataSource source, string sql)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new BlueTuskCommand(sql, connection);
        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    internal static async Task WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan? timeout = null)
    {
        var elapsed = Stopwatch.StartNew();
        while (!await predicate())
        {
            Check(elapsed.Elapsed < (timeout ?? TimeSpan.FromSeconds(90)), "scenario deadline");
            await Task.Delay(20);
        }
    }

    internal static Percentiles Distribution(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            return new(0, 0, 0, 0, 0);
        }

        double Percentile(double percentile) => sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1)];
        return new(sorted[0], Percentile(0.5), Percentile(0.95), Percentile(0.99), sorted[^1]);
    }

    private static IEnumerable<LoadCase> Cases(string profile, int seconds)
    {
        if (profile is "faults" or "qualification" or "storage") { yield break; }
        if (profile == "quick")
        {
            yield return new("jobs-backlog", "Jobs", 512, 2, 4, 1024, 4000, 1);
            yield return new("workflows-dag", "Workflows", 64, 2, 4, 256, 0, 1);
        }
        else if (profile == "matrix")
        {
            foreach (int padding in new[] { 64, 4096, 65536 })
                foreach (int concurrency in new[] { 2, 8 })
                {
                    yield return new("jobs-p" + padding + "-c" + concurrency, "Jobs", 1000, 4, concurrency, padding, 10000, 1);
                }

            yield return new("jobs-hot-tenant", "Jobs", 2000, 4, 4, 1024, 10000, 2, HotTenantWeight: 10);
            yield return new("jobs-pool-pressure", "Jobs", 1000, 4, 8, 1024, 10000, 2, PoolSize: 12);
            foreach (int padding in new[] { 256, 4096 })
                foreach (int concurrency in new[] { 2, 8 })
                {
                    yield return new("workflows-p" + padding + "-c" + concurrency, "Workflows", 128, 4, concurrency, padding, 0, 1);
                }
        }

        yield return new("jobs-endurance", "Jobs", 200000, 4, 4, 1024, 10000, 1,
            DurationSeconds: seconds, AdmissionWindow: 256, HotTenantWeight: 4);
    }

    private static string SourceFingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        string[] directories = ["src/BlueTusk.Jobs", "src/BlueTusk.Workflows", "benchmarks/BlueTusk.Workflows.LoadHarness"];
        foreach (string directory in directories)
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".csproj", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(file.Replace('\\', '/')));
                hash.AppendData(File.ReadAllBytes(file));
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
