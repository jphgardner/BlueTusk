```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host] : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3
LaunchCount=1  WarmupCount=3

```
| Method                    | Mean      | Error     | StdDev   | P95       | P99 (us) | Op/s         | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|-------------------------- |----------:|----------:|---------:|----------:|---------:|-------------:|------:|--------:|-------:|----------:|------------:|
| ReadInt32ArrayBinary      |  37.06 ns |  5.660 ns | 0.310 ns |  37.33 ns |     0.04 | 26,982,419.4 |  1.00 |    0.01 | 0.0024 |      40 B |        1.00 |
| ReadInt32ArrayBinaryTyped |  36.84 ns |  5.908 ns | 0.324 ns |  37.10 ns |     0.04 | 27,142,196.6 |  0.99 |    0.01 | 0.0024 |      40 B |        1.00 |
| ReadEnumBinary            |  27.35 ns |  2.380 ns | 0.130 ns |  27.47 ns |     0.03 | 36,558,173.4 |  0.74 |    0.01 | 0.0029 |      48 B |        1.20 |
| ReadInt32RangeBinary      |  27.17 ns |  0.142 ns | 0.008 ns |  27.17 ns |     0.03 | 36,808,962.7 |  0.73 |    0.01 |      - |         - |        0.00 |
| ReadInt32RangeBinaryTyped |  23.71 ns | 72.117 ns | 3.953 ns |  27.61 ns |     0.03 | 42,168,036.3 |  0.64 |    0.09 |      - |         - |        0.00 |
| ReadCompositeBinary       | 107.88 ns | 14.009 ns | 0.768 ns | 108.64 ns |     0.11 |  9,269,535.9 |  2.91 |    0.03 | 0.0210 |     352 B |        8.80 |
| EncodeInt32ArrayParameter |  80.55 ns | 26.380 ns | 1.446 ns |  81.97 ns |     0.08 | 12,414,972.2 |  2.17 |    0.04 | 0.0048 |      80 B |        2.00 |
| EncodeCompositeParameter  |  95.59 ns | 15.007 ns | 0.823 ns |  96.20 ns |     0.10 | 10,461,531.5 |  2.58 |    0.03 | 0.0033 |      56 B |        1.40 |
