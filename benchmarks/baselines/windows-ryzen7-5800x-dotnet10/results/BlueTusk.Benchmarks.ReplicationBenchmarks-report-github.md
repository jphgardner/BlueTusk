```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1
WarmupCount=3

```
| Method                           | Mean     | Error     | StdDev   | Gen0   | Allocated |
|--------------------------------- |---------:|----------:|---------:|-------:|----------:|
| DecodeOneKilobyteXLogData        | 18.78 ns | 15.695 ns | 0.860 ns | 0.0038 |      64 B |
| PullOneThousandBoundedXLogFrames | 18.37 ns |  9.028 ns | 0.495 ns | 0.0038 |      64 B |
