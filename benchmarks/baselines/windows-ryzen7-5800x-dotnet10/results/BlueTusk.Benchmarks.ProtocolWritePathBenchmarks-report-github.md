```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1
WarmupCount=3

```
| Method             | Mean      | Error     | StdDev   | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------- |----------:|----------:|---------:|------:|--------:|----------:|------------:|
| WriteSimpleQuery   |  19.27 ns |  15.59 ns | 0.855 ns |  1.00 |    0.05 |         - |          NA |
| WriteExtendedQuery | 193.91 ns | 162.84 ns | 8.926 ns | 10.08 |    0.55 |         - |          NA |
