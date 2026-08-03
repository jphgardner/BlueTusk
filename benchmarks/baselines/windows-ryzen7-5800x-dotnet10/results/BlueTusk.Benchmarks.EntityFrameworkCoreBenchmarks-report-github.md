```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1
WarmupCount=3

```
| Method                                   | Mean     | Error     | StdDev    | P95      | Gen0   | Allocated |
|----------------------------------------- |---------:|----------:|----------:|---------:|-------:|----------:|
| CompileAndExecuteParameterizedQueryAsync | 2.940 ms | 1.0986 ms | 0.0602 ms | 2.978 ms | 7.8125 | 128.95 KB |
| MaterializeOneHundredOrdersAsync         | 1.452 ms | 3.0665 ms | 0.1681 ms | 1.617 ms | 7.8125 | 160.82 KB |
| InsertOrdersAsync                        | 1.512 ms | 0.1747 ms | 0.0096 ms | 1.519 ms |      - |  26.82 KB |
| LoadAndUpdateOrdersAsync                 | 2.091 ms | 0.3065 ms | 0.0168 ms | 2.106 ms |      - |  36.78 KB |
