```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8894/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.110
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method              | Mean       | Error     | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|-------------------- |-----------:|----------:|----------:|------:|--------:|-------:|----------:|------------:|
| ReadTimestampBinary |   8.454 ns |  2.049 ns | 0.1123 ns |  1.00 |    0.02 |      - |         - |          NA |
| ReadNumericBinary   | 122.759 ns | 62.356 ns | 3.4179 ns | 14.52 |    0.39 | 0.0019 |      32 B |          NA |
| ReadJsonbBinary     |  17.702 ns |  5.666 ns | 0.3106 ns |  2.09 |    0.04 | 0.0029 |      48 B |          NA |
