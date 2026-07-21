```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8894/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.110
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method      | Mean       | Error      | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------ |-----------:|-----------:|----------:|------:|--------:|-------:|----------:|------------:|
| ReadBinary  |  8.4272 ns |  4.0587 ns | 0.2225 ns |  1.00 |    0.03 |      - |         - |          NA |
| ReadText    | 21.4341 ns | 28.8428 ns | 1.5810 ns |  2.54 |    0.17 | 0.0019 |      32 B |          NA |
| WriteBinary |  0.8038 ns |  1.6020 ns | 0.0878 ns |  0.10 |    0.01 |      - |         - |          NA |
| WriteText   |  8.8588 ns | 20.3535 ns | 1.1156 ns |  1.05 |    0.12 |      - |         - |          NA |
