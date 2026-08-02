```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Mean     | Error      | StdDev    | P95      | Gen0    | Gen1   | Allocated |
|------------------------------- |---------:|-----------:|----------:|---------:|--------:|-------:|----------:|
| RawPreparedGraphTraversalAsync | 1.088 ms |  0.3607 ms | 0.0198 ms | 1.102 ms |  9.7656 | 1.9531 | 183.53 KB |
| TypedEfGraphTraversalAsync     | 2.984 ms | 18.9078 ms | 1.0364 ms | 4.003 ms | 39.0625 | 7.8125 | 669.79 KB |
