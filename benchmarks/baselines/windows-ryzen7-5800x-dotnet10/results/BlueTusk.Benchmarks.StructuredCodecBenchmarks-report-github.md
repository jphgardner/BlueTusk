```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1
WarmupCount=3

```
| Method                    | Mean      | Error      | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|-------------------------- |----------:|-----------:|----------:|------:|--------:|-------:|----------:|------------:|
| ReadInt32ArrayBinary      | 155.29 ns | 235.954 ns | 12.933 ns |  1.00 |    0.10 | 0.0229 |     384 B |        1.00 |
| ReadEnumBinary            |  23.37 ns |   1.393 ns |  0.076 ns |  0.15 |    0.01 | 0.0029 |      48 B |        0.12 |
| ReadInt32RangeBinary      |  30.26 ns |  10.264 ns |  0.563 ns |  0.20 |    0.01 | 0.0029 |      48 B |        0.12 |
| ReadCompositeBinary       | 101.71 ns |  29.819 ns |  1.634 ns |  0.66 |    0.05 | 0.0210 |     352 B |        0.92 |
| EncodeInt32ArrayParameter | 187.84 ns | 110.327 ns |  6.047 ns |  1.22 |    0.09 | 0.0229 |     384 B |        1.00 |
| EncodeCompositeParameter  |  76.64 ns |  40.390 ns |  2.214 ns |  0.50 |    0.04 | 0.0033 |      56 B |        0.15 |
