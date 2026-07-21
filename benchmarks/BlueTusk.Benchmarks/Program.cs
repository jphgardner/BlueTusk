using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using BlueTusk.Benchmarks;

var artifactsPath = Environment.GetEnvironmentVariable("BLUETUSK_BENCHMARK_ARTIFACTS");
if (string.IsNullOrWhiteSpace(artifactsPath))
{
    artifactsPath = Path.Combine(Environment.CurrentDirectory, "artifacts", "benchmarks");
}

var configuration = ManualConfig
    .Create(DefaultConfig.Instance)
    .WithArtifactsPath(Path.GetFullPath(artifactsPath));
_ = BenchmarkSwitcher
    .FromAssembly(typeof(ProtocolParserBenchmarks).Assembly)
    .Run(args, configuration);
