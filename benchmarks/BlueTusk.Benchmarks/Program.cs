using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using BlueTusk.Benchmarks;

if (args is ["--transport-tls-smoke"])
{
    using var benchmark = new TransportPipelineSocketBenchmarks
    {
        Mode = TransportLoopbackMode.Tls,
    };
    benchmark.Setup();
    Console.WriteLine($"current-sync={benchmark.CurrentArrayPoolSocketSync()}");
    Console.WriteLine($"prototype-sync={benchmark.PipelinesPrototypeSocketBlockingSync()}");
    Console.WriteLine($"current-async={await benchmark.CurrentArrayPoolSocketAsync()}");
    Console.WriteLine($"prototype-async={await benchmark.PipelinesPrototypeSocketAsync()}");
    return;
}

if (args is ["--multiplexing-paired-evidence", var pairedEvidencePath])
{
    await MultiplexingPairedEvidenceWriter.CaptureAsync(pairedEvidencePath);
    return;
}

if (args is ["--provider-paired-evidence", var providerPairedEvidencePath])
{
    await ProviderPairedEvidenceWriter.CaptureAsync(providerPairedEvidencePath);
    return;
}

if (args is ["--provider-extended-paired-evidence", var extendedProviderEvidencePath])
{
    await ProviderPairedEvidenceWriter.CaptureAsync(
        extendedProviderEvidencePath,
        includeExtendedWorkloads: true);
    return;
}

if (args is ["--provider-critical-paired-evidence", var criticalProviderEvidencePath])
{
    await ProviderPairedEvidenceWriter.CaptureCriticalAsync(criticalProviderEvidencePath);
    return;
}

if (args is ["--provider-copy-paired-evidence", var copyProviderEvidencePath])
{
    await ProviderPairedEvidenceWriter.CaptureCopyAsync(copyProviderEvidencePath);
    return;
}

if (args is ["--provider-validation-smoke"])
{
    await ProviderValidationSmoke.RunAsync();
    return;
}

if (args is ["--provider-batch-phase-profile", var batchPhaseIterationText] &&
    int.TryParse(batchPhaseIterationText, out var batchPhaseIterations) &&
    batchPhaseIterations > 0)
{
    await using var benchmark = new ProviderComparisonBenchmarks();
    await benchmark.Setup();
    await benchmark.ProfileBatchPhasesAsync(batchPhaseIterations);
    return;
}

if (args is ["--provider-ef-profile", var provider, var workload, var iterationText] &&
    int.TryParse(iterationText, out var iterations) &&
    iterations > 0)
{
    await using var benchmark = new ProviderComparisonBenchmarks();
    await benchmark.Setup();
    Func<Task<int>> operation = (provider.ToLowerInvariant(), workload.ToLowerInvariant()) switch
    {
        ("bluetusk", "compiled") => benchmark.BlueTuskEfCompiledQueryAsync,
        ("npgsql", "compiled") => benchmark.NpgsqlEfCompiledQueryAsync,
        ("bluetusk", "materialize") => benchmark.BlueTuskEfMaterialize100RowsAsync,
        ("npgsql", "materialize") => benchmark.NpgsqlEfMaterialize100RowsAsync,
        ("bluetusk", "insert") => benchmark.BlueTuskEfInsertOneAsync,
        ("npgsql", "insert") => benchmark.NpgsqlEfInsertOneAsync,
        ("bluetusk", "update") => benchmark.BlueTuskEfUpdateOneAsync,
        ("npgsql", "update") => benchmark.NpgsqlEfUpdateOneAsync,
        ("bluetusk", "batch") => benchmark.BlueTuskBatch16ParameterizedScalarsAsync,
        ("npgsql", "batch") => benchmark.NpgsqlBatch16ParameterizedScalarsAsync,
        ("bluetusk", "typed") => benchmark.BlueTuskPreparedTypedRowRoundTripAsync,
        ("npgsql", "typed") => benchmark.NpgsqlPreparedTypedRowRoundTripAsync,
        ("bluetusk", "notification") => benchmark.BlueTuskNotificationDeliveryAsync,
        ("npgsql", "notification") => benchmark.NpgsqlNotificationDeliveryAsync,
        ("bluetusk", "sequential-bytea") => async () =>
            checked((int)await benchmark.BlueTuskSequentialOneMegabyteByteaAsync()),
        ("npgsql", "sequential-bytea") => async () =>
            checked((int)await benchmark.NpgsqlSequentialOneMegabyteByteaAsync()),
        ("bluetusk", "copy-import") => async () =>
            checked((int)await benchmark.BlueTuskBinaryCopyImport1000RowsAsync()),
        ("npgsql", "copy-import") => async () =>
            checked((int)await benchmark.NpgsqlBinaryCopyImport1000RowsAsync()),
        ("bluetusk", "copy-export") => async () =>
            checked((int)await benchmark.BlueTuskBinaryCopyExport1000RowsAsync()),
        ("npgsql", "copy-export") => async () =>
            checked((int)await benchmark.NpgsqlBinaryCopyExport1000RowsAsync()),
        ("bluetusk", "large-object") => async () =>
            checked((int)await benchmark.BlueTuskLargeObjectReadOneMegabyteAsync()),
        ("npgsql", "large-object") => async () =>
            checked((int)await benchmark.NpgsqlLargeObjectReadOneMegabyteAsync()),
        _ => throw new ArgumentException(
            "Provider must be 'bluetusk' or 'npgsql' and workload must be " +
            "'compiled', 'materialize', 'insert', 'update', 'batch', 'typed', " +
            "'notification', 'sequential-bytea', " +
            "'copy-import', 'copy-export' or 'large-object'."),
    };

    var checksum = 0;
    var profileStarted = System.Diagnostics.Stopwatch.GetTimestamp();
    for (var iteration = 0; iteration < iterations; iteration++)
    {
        checksum += await operation();
    }

    var profileElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(profileStarted);
    Console.WriteLine(
        $"provider={provider}; workload={workload}; iterations={iterations}; " +
        $"elapsed-ms={profileElapsed.TotalMilliseconds:F3}; " +
        $"mean-us={profileElapsed.TotalMicroseconds / iterations:F3}; checksum={checksum}");
    return;
}

var artifactsPath = Environment.GetEnvironmentVariable("BLUETUSK_BENCHMARK_ARTIFACTS");
if (string.IsNullOrWhiteSpace(artifactsPath))
{
    artifactsPath = Path.Combine(Environment.CurrentDirectory, "artifacts", "benchmarks");
}

var configuration = ManualConfig
    .Create(DefaultConfig.Instance)
    .WithArtifactsPath(Path.GetFullPath(artifactsPath))
    .AddColumn(
        StatisticColumn.P95,
        Percentile99Column.Instance,
        StatisticColumn.OperationsPerSecond);

var competitiveConnectionString = Environment.GetEnvironmentVariable(
    ProviderComparisonBenchmarks.ConnectionStringEnvironmentVariable);
var liveBenchmarkTypes = new HashSet<Type>
{
    typeof(ContinuousGraphBenchmarks),
    typeof(EntityFrameworkCoreBenchmarks),
    typeof(MultiplexingComparisonBenchmarks),
    typeof(ProviderComparisonBenchmarks),
    typeof(SqlPgqBenchmarks),
};
var requestsLiveBenchmark = args.Any(
    argument => liveBenchmarkTypes.Any(
        type => argument.Contains(type.Name, StringComparison.OrdinalIgnoreCase)));
if (string.IsNullOrWhiteSpace(competitiveConnectionString) && requestsLiveBenchmark)
{
    throw new InvalidOperationException(
        $"{ProviderComparisonBenchmarks.ConnectionStringEnvironmentVariable} must be configured " +
        "to run live PostgreSQL benchmarks.");
}

var benchmarkTypes = typeof(ProtocolParserBenchmarks).Assembly
    .GetTypes()
    .Where(type => type.IsPublic &&
        !type.IsGenericType &&
        type.GetMethods().Any(
            method => method.IsDefined(typeof(BenchmarkAttribute), inherit: true)) &&
        (!liveBenchmarkTypes.Contains(type) ||
            !string.IsNullOrWhiteSpace(competitiveConnectionString)))
    .ToArray();
_ = BenchmarkSwitcher
    .FromTypes(benchmarkTypes)
    .Run(args, configuration);
