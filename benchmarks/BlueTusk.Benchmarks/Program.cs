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
_ = BenchmarkSwitcher
    .FromAssembly(typeof(ProtocolParserBenchmarks).Assembly)
    .Run(args, configuration);
