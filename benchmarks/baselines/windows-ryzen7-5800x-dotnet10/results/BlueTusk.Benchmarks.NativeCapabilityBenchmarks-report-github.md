```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host] : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3
LaunchCount=1  WarmupCount=3

```
| Method                    | Mean      | Error    | StdDev   | P95       | P99 (us) | Op/s         | Gen0   | Allocated |
|-------------------------- |----------:|---------:|---------:|----------:|---------:|-------------:|-------:|----------:|
| EncodeBinaryCopyInt32     |  53.36 ns | 31.60 ns | 1.732 ns |  54.87 ns |     0.05 | 18,739,452.1 | 0.0052 |      88 B |
| DecodeNotification        | 100.76 ns | 38.15 ns | 2.091 ns | 102.58 ns |     0.10 |  9,924,574.9 | 0.0081 |     136 B |
| ReadLargeObjectChunkAsync | 113.81 ns | 69.09 ns | 3.787 ns | 116.63 ns |     0.12 |  8,786,229.1 |      - |         - |
