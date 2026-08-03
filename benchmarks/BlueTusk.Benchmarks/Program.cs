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

var artifactsPath = Environment.GetEnvironmentVariable("BLUETUSK_BENCHMARK_ARTIFACTS");
if (string.IsNullOrWhiteSpace(artifactsPath))
{
    artifactsPath = Path.Combine(Environment.CurrentDirectory, "artifacts", "benchmarks");
}

var configuration = ManualConfig
    .Create(DefaultConfig.Instance)
    .WithArtifactsPath(Path.GetFullPath(artifactsPath))
    .AddColumn(StatisticColumn.P95);

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
