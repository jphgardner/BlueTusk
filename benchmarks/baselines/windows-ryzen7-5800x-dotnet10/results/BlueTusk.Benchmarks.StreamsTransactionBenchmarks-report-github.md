```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host] : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

The assembly row is the 2026-08-07 in-process ShortRun. The spool row is the
final out-of-process MediumRun with two launches, ten warmups and fifteen
measured iterations.

```
| Method                                   | Mean            | Error             | StdDev          | P95             | P99 (us)  | Op/s         | Gen0     | Gen1     | Gen2     | Allocated |
|----------------------------------------- |----------------:|------------------:|----------------:|----------------:|----------:|-------------:|---------:|---------:|---------:|----------:|
| AssembleAndMaterializeOneThousandInserts |        349.5 ns |          29.67 ns |         1.63 ns |        350.9 ns |      0.35 | 2,860,869.01 |   0.0244 |   0.0117 |        - |     413 B |
| SpillAndStreamFourMiBTransaction         | 24,043,596.9 ns |     1,074,860.28 ns | 1,575,516.39 ns | 26,737,721.9 ns | 28,587.29 |        41.59 |        - |        - |        - |  142444 B |
