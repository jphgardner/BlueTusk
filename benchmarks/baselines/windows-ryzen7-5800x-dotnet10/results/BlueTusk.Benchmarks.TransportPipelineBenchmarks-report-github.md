```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Workload           | Mean         | Error        | StdDev       | P95          | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |------------------- |-------------:|-------------:|-------------:|-------------:|------:|--------:|----------:|------------:|
| **CurrentArrayPoolSync**           | **ByteFragmentedRows** | **187,690.7 ns** | **154,874.2 ns** |  **8,489.18 ns** | **193,783.1 ns** |  **1.00** |    **0.06** |         **-** |          **NA** |
| PipelinesPrototypeBlockingSync | ByteFragmentedRows | 204,688.9 ns | 298,664.0 ns | 16,370.78 ns | 219,134.8 ns |  1.09 |    0.09 |         - |          NA |
| CurrentArrayPoolAsync          | ByteFragmentedRows | 260,948.1 ns | 601,545.0 ns | 32,972.71 ns | 287,009.5 ns |  1.39 |    0.16 |         - |          NA |
| PipelinesPrototypeAsync        | ByteFragmentedRows | 158,513.5 ns | 117,928.3 ns |  6,464.05 ns | 162,854.5 ns |  0.85 |    0.05 |         - |          NA |
|                                |                    |              |              |              |              |       |         |           |             |
| **CurrentArrayPoolSync**           | **LargeField**         |  **30,204.5 ns** |  **22,832.2 ns** |  **1,251.51 ns** |  **30,997.6 ns** |  **1.00** |    **0.05** |         **-** |          **NA** |
| PipelinesPrototypeBlockingSync | LargeField         |  61,594.6 ns |  85,577.7 ns |  4,690.80 ns |  66,054.0 ns |  2.04 |    0.15 |      96 B |          NA |
| CurrentArrayPoolAsync          | LargeField         |  30,889.7 ns |  10,575.2 ns |    579.66 ns |  31,458.7 ns |  1.02 |    0.04 |         - |          NA |
| PipelinesPrototypeAsync        | LargeField         |  59,216.6 ns |  25,886.8 ns |  1,418.94 ns |  60,564.0 ns |  1.96 |    0.08 |      96 B |          NA |
|                                |                    |              |              |              |              |       |         |           |             |
| **CurrentArrayPoolSync**           | **CopyStream**         |  **42,520.9 ns** |  **12,037.2 ns** |    **659.80 ns** |  **43,026.1 ns** |  **1.00** |    **0.02** |         **-** |          **NA** |
| PipelinesPrototypeBlockingSync | CopyStream         |  60,201.1 ns |   4,958.6 ns |    271.80 ns |  60,418.2 ns |  1.42 |    0.02 |         - |          NA |
| CurrentArrayPoolAsync          | CopyStream         |  56,119.2 ns |  78,498.1 ns |  4,302.74 ns |  59,379.2 ns |  1.32 |    0.09 |         - |          NA |
| PipelinesPrototypeAsync        | CopyStream         |  56,699.0 ns |  63,720.5 ns |  3,492.74 ns |  60,128.4 ns |  1.33 |    0.07 |         - |          NA |
|                                |                    |              |              |              |              |       |         |           |             |
| **CurrentArrayPoolSync**           | **CancellationDrain**  |     **800.9 ns** |   **1,515.6 ns** |     **83.07 ns** |     **881.0 ns** |  **1.01** |    **0.13** |         **-** |          **NA** |
| PipelinesPrototypeBlockingSync | CancellationDrain  |     617.9 ns |     216.5 ns |     11.87 ns |     628.1 ns |  0.78 |    0.07 |         - |          NA |
| CurrentArrayPoolAsync          | CancellationDrain  |     975.6 ns |   1,741.3 ns |     95.45 ns |   1,068.5 ns |  1.23 |    0.15 |         - |          NA |
| PipelinesPrototypeAsync        | CancellationDrain  |     811.7 ns |   1,002.1 ns |     54.93 ns |     864.2 ns |  1.02 |    0.11 |         - |          NA |
