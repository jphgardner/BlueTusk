using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using BlueTusk.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BlueTusk.Benchmarks;

/// <summary>A diagnostic for selecting bounded EF defaults; not part of the 16-feature release matrix.</summary>
internal static class EfBatchCapture
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    internal static void Validate(ProviderRequestCapture.Options options, int rows, int? maxBatchSize)
    {
        options.Validate();
        if (!options.Diagnostic || options.Feature != "ef-update" || options.Concurrency != 1 ||
            rows is not (1 or 100 or 1000) || maxBatchSize is < 1 or > 1000)
        {
            throw new ArgumentException("EF batching probes require diagnostic ef-update/C1, 1/100/1000 rows, and a default or 1..1000 batch limit.");
        }
    }

    public static async Task RunAsync(string optionsPath, int rows, int? maxBatchSize)
    {
        var options = JsonSerializer.Deserialize<ProviderRequestCapture.Options>(
            await File.ReadAllTextAsync(optionsPath), JsonOptions) ?? throw new ArgumentException("Missing options.");
        Validate(options, rows, maxBatchSize);
        var outputPath = Path.GetFullPath(options.OutputPath);
        if (File.Exists(outputPath)) { throw new IOException("Probe output already exists."); }
        var connectionString = Environment.GetEnvironmentVariable(ProviderComparisonBenchmarks.ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString)) { throw new InvalidOperationException("A dedicated benchmark connection string is required."); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.WarmupSeconds + options.MeasurementSeconds + 180));
        await using var fixture = await ProviderRequestFixture.CreateAsync(options, connectionString, timeout.Token);
        var builder = new DbContextOptionsBuilder<ProviderRequestFixture.OrderContext>(fixture.EfOptions!);
        if (maxBatchSize.HasValue)
        {
            if (fixture.IsBlueTusk) { builder.UseBlueTusk((BlueTuskDataSource)fixture.Source, ef => ef.MaxBatchSize(maxBatchSize.Value)); }
            else { builder.UseNpgsql((NpgsqlDataSource)fixture.Source, ef => ef.MaxBatchSize(maxBatchSize.Value)); }
        }
        var contextOptions = builder.Options;
        async Task ExecuteAsync(CancellationToken token)
        {
            await using var context = new ProviderRequestFixture.OrderContext(contextOptions, fixture.Schema);
            await using var transaction = await context.Database.BeginTransactionAsync(token);
            var entities = await context.Orders.Where(row => row.Id <= rows).OrderBy(row => row.Id).ToListAsync(token);
            if (entities.Count != rows) { throw new InvalidOperationException("Fixture row count differs."); }
            foreach (var entity in entities) { entity.Customer = "updated"; }
            if (await context.SaveChangesAsync(token) != rows) { throw new InvalidOperationException("Updated row count differs."); }
            await transaction.RollbackAsync(token);
        }
        Func<CancellationToken, Task>[] operations = [ExecuteAsync];
        _ = await ProviderRequestCapture.MeasureWindowAsync(operations, options.WarmupSeconds, 0, timeout.Token);
        var measurement = await ProviderRequestCapture.MeasureWindowAsync(operations, options.MeasurementSeconds,
            options.MaximumSamplesPerWorker, timeout.Token);
        measurement = measurement with
        {
            Workers = measurement.Workers.Select(worker => new ProviderRequestCapture.WorkerSamples(
                worker.RequestTicks.AsSpan(0, worker.Count).ToArray(), worker.Count)).ToArray(),
        };
        // Verify stored state outside timing; neither arm is permitted to commit the probe's updates.
        await using (var verify = new ProviderRequestFixture.OrderContext(contextOptions, fixture.Schema))
        {
            if (await verify.Orders.AnyAsync(row => row.Customer == "updated", timeout.Token))
            {
                throw new InvalidOperationException("The probe left committed updates.");
            }
        }
        var report = new
        {
            schemaVersion = 1,
            evidenceKind = "ef-batch-diagnostic; not release-performance evidence",
            diagnostic = true,
            releaseGatePassed = false,
            options.SourceCommit,
            options.Provider,
            rowsPerOperation = rows,
            maxBatchSize,
            options.WarmupSeconds,
            options.MeasurementSeconds,
            capturedUtc = DateTimeOffset.UtcNow,
            frequency = Stopwatch.Frequency,
            environment = new
            {
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                runtime = RuntimeInformation.FrameworkDescription,
                fixture.ServerVersion,
                options.PostgreSqlImage,
                fixture.TlsActive,
                fixture.CertificatePolicy,
                harnessAssembly = typeof(EfBatchCapture).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                candidateAssembly = typeof(BlueTuskConnection).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                referenceAssembly = typeof(NpgsqlConnection).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            },
            boundaries = "New context; load tracked rows; update; SaveChanges; rollback; dispose. Client counters include harness overhead. RSS includes setup; excludes PostgreSQL. C1 closed-loop. No commit durability or release verdict.",
            measurement,
            metrics = ProviderRequestStatistics.Derive(measurement, Stopwatch.Frequency),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(output, report, JsonOptions, timeout.Token);
        Console.WriteLine($"Captured {measurement.CompletedOperations} EF operations: {options.Provider}, {rows} rows, batch {maxBatchSize?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "default"}.");
    }
}
