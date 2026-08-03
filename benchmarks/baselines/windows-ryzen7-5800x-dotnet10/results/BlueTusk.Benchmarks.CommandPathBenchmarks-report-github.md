```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1
WarmupCount=3

```
| Method                                    | Mean       | Error    | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------------------ |-----------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| ExecuteInt32ParameterAndScalar            |   399.8 ns | 152.5 ns |  8.36 ns |  1.00 |    0.03 | 0.0620 |   1.02 KB |        1.00 |
| ExecuteTextParameterAndScalar             |   531.4 ns | 414.5 ns | 22.72 ns |  1.33 |    0.05 | 0.0849 |   1.39 KB |        1.36 |
| ExecuteReaderAndReadOneHundredInt32Values | 2,285.2 ns | 364.0 ns | 19.95 ns |  5.72 |    0.11 | 0.1526 |    2.5 KB |        2.44 |
| ExecuteInt32ParameterAndScalarAsync       |   457.7 ns | 219.8 ns | 12.05 ns |  1.15 |    0.03 | 0.0801 |   1.32 KB |        1.29 |
