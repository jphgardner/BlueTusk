```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host] : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3
LaunchCount=1  WarmupCount=3

```
| Method                       | Mean     | Error    | StdDev   | P95      | P99 (us) | Op/s     | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------------------- |---------:|---------:|---------:|---------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| ReadThousandTypedInt32Rows   | 18.79 μs | 2.230 μs | 0.122 μs | 18.90 μs |    18.91 | 53,212.3 |  1.00 |    0.01 |      - |     224 B |        1.00 |
| ReadThousandGenericInt32Rows | 16.09 μs | 2.052 μs | 0.112 μs | 16.20 μs |    16.21 | 62,161.9 |  0.86 |    0.01 |      - |     224 B |        1.00 |
| ReadOneMegabyteByteaStream   | 12.04 μs | 9.881 μs | 0.542 μs | 12.57 μs |    12.65 | 83,058.1 |  0.64 |    0.03 | 0.0153 |     272 B |        1.21 |
| ReadOneMegabyteTextReader    | 59.43 μs | 1.357 μs | 0.074 μs | 59.50 μs |    59.51 | 16,827.1 |  3.16 |    0.02 | 0.1831 |    3552 B |       15.86 |
