```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                           | Mean       | Error      | StdDev    | P95        | Gen0    | Gen1    | Allocated |
|--------------------------------- |-----------:|-----------:|----------:|-----------:|--------:|--------:|----------:|
| CompileGraphRegistrationAsync    |   988.4 μs | 1,709.4 μs |  93.70 μs | 1,077.6 μs |  5.8594 |  1.9531 | 101.02 KB |
| AuthoritativeGraphRequeryAsync   | 2,826.9 μs | 1,469.3 μs |  80.54 μs | 2,880.7 μs | 39.0625 |  7.8125 | 650.44 KB |
| AffectedGraphRefreshAndDiffAsync | 4,224.7 μs | 2,774.5 μs | 152.08 μs | 4,374.6 μs | 46.8750 | 15.6250 | 867.34 KB |
