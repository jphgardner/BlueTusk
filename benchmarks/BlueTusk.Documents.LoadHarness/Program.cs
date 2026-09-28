using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using BenchmarkDotNet.Running;
using BlueTusk.Data;

namespace BlueTusk.Documents.LoadHarness;

internal static partial class Program
{
    internal const string Application = "BlueTuskDocumentsLoad";
    internal static readonly DocumentCollectionDefinition<PayloadDocument> Collection = new("records", HarnessJson.Default.PayloadDocument);

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--microbenchmarks")
        { BenchmarkSwitcher.FromAssembly(typeof(DocumentStagingBenchmarks).Assembly).Run(args[1..]); return 0; }
        if (args.Length == 1 && args[0] == "--filesystem-observer-smoke")
        { await FilesystemObserverSmokeAsync(); return 0; }
        if (args.Length == 2 && args[0] == "--crash-child") { await CrashChildAsync(args[1]); return 0; }
        var raw = Environment.GetEnvironmentVariable("BLUETUSK_DOCUMENTS_LOAD_CONNECTION_STRING")
            ?? throw new InvalidOperationException("A dedicated disposable Documents workload database must be explicitly configured.");
        var budget = StorageBudget.Read();
        var cellSeconds = ReadSeconds("BLUETUSK_DOCUMENTS_LOAD_CELL_SECONDS", 3, 30);
        var sustainedSeconds = ReadSeconds("BLUETUSK_DOCUMENTS_LOAD_SECONDS", 60, 600);
        var output = Path.GetFullPath(Environment.GetEnvironmentVariable("BLUETUSK_DOCUMENTS_LOAD_REPORT") ?? "artifacts/documents-load/report.json");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15 * (cellSeconds + 3) + sustainedSeconds + budget.IdleDrainSeconds + 360));
        await using var observer = CreateSource(raw, Application + "Observer", 2);
        EnvironmentReport? environment = null;
        var stage = "capture-environment";
        var scenarios = new List<ScenarioReport>();
        try
        {
            environment = await CaptureEnvironmentAsync(observer, deadline.Token);
            foreach (var bytes in new[] { 1024, 64 * 1024, 1024 * 1024 })
                foreach (var dimension in new (int Tenants, int Writers)[] { (1, 1), (8, 1), (8, 8), (32, 8), (32, 32) })
                {
                    stage = $"matrix-{bytes}-{dimension.Tenants}-{dimension.Writers}";
                    var result = await RunScenarioAsync(raw, observer, bytes, dimension.Tenants, dimension.Writers, cellSeconds, false, budget, deadline.Token);
                    scenarios.Add(result);
                    Console.WriteLine($"Documents {bytes}B/{dimension.Tenants} tenants/{dimension.Writers} writers: {result.CommittedDocumentsPerSecond:F1} docs/s, save p99 {result.SaveLatency.P99:F2} ms; verified.");
                }
            stage = "sustained";
            scenarios.Add(await RunScenarioAsync(raw, observer, 64 * 1024, 8, 32, sustainedSeconds, true, budget, deadline.Token));
            _ = await budget.ReadFilesystemAsync(deadline.Token);
            stage = "process-kill-recovery";
            var recovery = await RunRecoveryAsync(raw, observer, deadline.Token);
            _ = await budget.ReadFilesystemAsync(deadline.Token);
            stage = "write-report";
            var report = new CampaignReport(environment, DateTimeOffset.UtcNow, cellSeconds, sustainedSeconds,
                "Local bounded measured campaign with fixture-only maintenance profile and sampled physical/host limits. Latencies include harness work and instrumentation. Percentiles are upper histogram-bin bounds (128 bins per power of two). Allocation/CPU/GC describe this process, including its observer. WAL/statistics are cluster/database scoped and may lag; use the owned dedicated fixture. Parent relation bytes already include its TOAST child. This dirty working-tree evidence is not immutable release or production qualification.",
                scenarios.ToArray(), recovery,
                new ResourcePolicyReport(budget.Profile.ToString(), budget.MaximumDatabaseBytes, budget.MinimumFilesystemAvailableBytes,
                    budget.IdleDrainSeconds, StorageBudget.MaximumFilesystemSampleAgeSeconds, StorageBudget.MaximumMaintenanceGapSeconds,
                    StorageBudget.MaximumFilesystemReadAttempts, StorageBudget.MaximumFilesystemSamples));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllBytesAsync(output, JsonSerializer.SerializeToUtf8Bytes(report, HarnessJson.Default.CampaignReport), deadline.Token);
            Console.WriteLine("Documents workload and actual child-process recovery verified; report written.");
            return 0;
        }
        catch (Exception exception)
        {
            var cleanup = exception as OwnedSchemaCleanupException;
            var status = exception switch
            {
                StorageBudgetException => "resource-guard-stopped",
                StorageObservationException => "observer-guard-stopped",
                OwnedSchemaCleanupException => "owned-schema-cleanup-failed",
                _ => "workload-failed"
            };
            var code = exception switch
            {
                StorageBudgetException limit => limit.Limit,
                StorageObservationException observation => observation.Code,
                OwnedSchemaCleanupException => "owned-schema-drop-failed",
                TimeoutException => "workload-timeout",
                OperationCanceledException => "workload-deadline-or-cancellation",
                _ => "unexpected-campaign-exception"
            };
            var operation = cleanup is not null ? "schema-cleanup-after-" + cleanup.PreviousOperation
                : exception.Data["DocumentsOperation"] as string ?? stage;
            var stack = exception.StackTrace;
            if (stack is { Length: > 8192 }) { stack = stack[..8192]; }
            var failure = new CampaignFailureReport(status, budget.Profile.ToString(), code,
                (exception as StorageBudgetException)?.Observed, (exception as StorageBudgetException)?.Configured,
                scenarios.Count, DateTimeOffset.UtcNow, environment?.SourceFingerprint ?? Environment.GetEnvironmentVariable("BLUETUSK_DOCUMENTS_LOAD_SOURCE_FINGERPRINT") ?? "unbound-working-tree",
                environment?.Assemblies ?? CaptureAssemblyIdentities(), (exception as StorageBudgetException)?.Snapshot,
                operation, exception.GetType().FullName ?? exception.GetType().Name, (exception as StorageObservationException)?.Diagnostics,
                exception.InnerException?.GetType().FullName, cleanup?.PreviousFailure?.GetType().FullName, stack, cleanup?.Diagnostics);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllBytesAsync(Path.ChangeExtension(output, ".failure.json"),
                JsonSerializer.SerializeToUtf8Bytes(failure, HarnessJson.Default.CampaignFailureReport));
            Console.Error.WriteLine($"Documents campaign stopped: {status}, {code}, {operation}, {exception.GetType().Name}.");
            return 2;
        }
    }

    internal static BlueTuskDataSource CreateSource(string raw, string application, int poolSize)
    {
        var settings = new BlueTuskConnectionStringBuilder(raw) { ApplicationName = application, MaximumPoolSize = poolSize, MinimumPoolSize = 0 };
        return BlueTuskDataSource.Create(settings.ConnectionString);
    }
    internal static void Check(bool valid, string contract)
    { if (!valid) { throw new InvalidOperationException("Documents workload invariant failed: " + contract); } }
    private static int ReadSeconds(string name, int fallback, int maximum)
    {
        var value = int.Parse(Environment.GetEnvironmentVariable(name) ?? fallback.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        if (value < 1 || value > maximum) { throw new ArgumentOutOfRangeException(name); }
        return value;
    }
    internal static string Payload(int bytes)
    {
        // Deterministic synthetic low-compressibility content; never use this fixture generator for secrets.
        var data = new byte[(bytes * 3 + 3) / 4]; new Random(17041 + bytes).NextBytes(data);
        return Convert.ToBase64String(data)[..bytes];
    }
    private static async Task<EnvironmentReport> CaptureEnvironmentAsync(BlueTuskDataSource observer, CancellationToken token)
    {
        await using var connection = await observer.OpenConnectionAsync(token);
        await using var command = new BlueTuskCommand("SELECT version(),current_setting('shared_buffers'),current_setting('max_connections'),current_setting('synchronous_commit'),current_setting('full_page_writes'),current_setting('track_io_timing')", connection);
        await using var reader = await command.ExecuteReaderAsync(token); Check(await reader.ReadAsync(token), "server settings");
        var assemblies = CaptureAssemblyIdentities();
        return new(RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription, RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount, Environment.GetEnvironmentVariable("BLUETUSK_DOCUMENTS_LOAD_HOST") ?? "unnamed-local-host",
            Environment.GetEnvironmentVariable("BLUETUSK_DOCUMENTS_LOAD_SOURCE_FINGERPRINT") ?? "unbound-working-tree",
            Environment.GetEnvironmentVariable("BLUETUSK_DOCUMENTS_LOAD_IMAGE") ?? "unrecorded", reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5), assemblies);
    }
    private static AssemblyIdentity[] CaptureAssemblyIdentities() =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, "BlueTusk.*.dll")
            .Order(StringComparer.Ordinal).Select(path =>
            {
                var assembly = System.Reflection.AssemblyName.GetAssemblyName(path);
                return new AssemblyIdentity(assembly.Name!, assembly.Version?.ToString() ?? "unknown",
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            }).ToArray();
}
