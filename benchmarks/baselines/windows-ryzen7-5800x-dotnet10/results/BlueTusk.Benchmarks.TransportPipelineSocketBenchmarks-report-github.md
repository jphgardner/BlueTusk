```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                               | Mode     | Mean       | Error      | StdDev    | P95        | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------------- |--------- |-----------:|-----------:|----------:|-----------:|------:|--------:|----------:|------------:|
| **CurrentArrayPoolSocketSync**           | **PlainTcp** |   **795.6 ns** | **1,565.0 ns** |  **85.79 ns** |   **870.2 ns** |  **1.01** |    **0.13** |         **-** |          **NA** |
| PipelinesPrototypeSocketBlockingSync | PlainTcp |   773.6 ns |   208.4 ns |  11.43 ns |   783.5 ns |  0.98 |    0.09 |         - |          NA |
| CurrentArrayPoolSocketAsync          | PlainTcp |   747.7 ns |   649.6 ns |  35.61 ns |   782.6 ns |  0.95 |    0.10 |         - |          NA |
| PipelinesPrototypeSocketAsync        | PlainTcp | 1,319.8 ns |   428.8 ns |  23.50 ns | 1,338.8 ns |  1.67 |    0.16 |         - |          NA |
|                                      |          |            |            |           |            |       |         |           |             |
| **CurrentArrayPoolSocketSync**           | **Tls**      | **1,073.3 ns** |   **603.3 ns** |  **33.07 ns** | **1,096.3 ns** |  **1.00** |    **0.04** |         **-** |          **NA** |
| PipelinesPrototypeSocketBlockingSync | Tls      | 1,235.4 ns | 2,343.3 ns | 128.44 ns | 1,357.6 ns |  1.15 |    0.11 |         - |          NA |
| CurrentArrayPoolSocketAsync          | Tls      | 1,149.0 ns | 1,370.6 ns |  75.12 ns | 1,221.1 ns |  1.07 |    0.07 |         - |          NA |
| PipelinesPrototypeSocketAsync        | Tls      | 1,117.4 ns |   667.6 ns |  36.60 ns | 1,151.8 ns |  1.04 |    0.04 |         - |          NA |
