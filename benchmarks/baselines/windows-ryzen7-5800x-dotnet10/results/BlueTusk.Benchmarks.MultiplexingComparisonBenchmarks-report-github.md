```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.303
  [Host] : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3

Job=MediumRun  Toolchain=InProcessEmitToolchain  IterationCount=15
LaunchCount=2  WarmupCount=10

```
| Method                                   | Categories                  | Mean      | Error    | StdDev   | P95       | P99 (us) | Op/s     | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------------------------------- |---------------------------- |----------:|---------:|---------:|----------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| BlueTuskConcurrentScalarBurstAsync       | ConcurrentMultiplexedScalar |  15.94 μs | 0.320 μs | 0.459 μs |  16.70 μs |    16.92 | 62,750.7 |  1.00 |    0.04 | 0.0610 |    1497 B |        1.00 |
| NpgsqlConcurrentScalarBurstAsync         | ConcurrentMultiplexedScalar |  18.73 μs | 0.438 μs | 0.656 μs |  19.74 μs |    20.43 | 53,400.1 |  1.18 |    0.05 | 0.0916 |    1738 B |        1.16 |
| BlueTuskPooledConcurrentScalarBurstAsync | ConcurrentMultiplexedScalar |  97.50 μs | 1.932 μs | 2.831 μs | 102.73 μs |   103.72 | 10,256.9 |  6.12 |    0.24 | 0.1221 |    2337 B |        1.56 |
| NpgsqlPooledConcurrentScalarBurstAsync   | ConcurrentMultiplexedScalar |  98.57 μs | 1.066 μs | 1.459 μs | 101.14 μs |   102.79 | 10,145.3 |  6.19 |    0.19 | 0.1221 |    2825 B |        1.89 |
|                                          |                             |           |          |          |           |          |          |       |         |        |           |             |
| BlueTuskReusedScalarBurstAsync           | ReusedMultiplexedScalar     |  15.43 μs | 0.439 μs | 0.644 μs |  16.34 μs |    16.47 | 64,806.0 |  1.00 |    0.06 | 0.0305 |     621 B |        1.00 |
| NpgsqlReusedScalarBurstAsync             | ReusedMultiplexedScalar     |  18.84 μs | 0.393 μs | 0.577 μs |  19.58 μs |    19.98 | 53,077.0 |  1.22 |    0.06 | 0.0305 |     794 B |        1.28 |
| BlueTuskPooledReusedScalarBurstAsync     | ReusedMultiplexedScalar     |  96.64 μs | 1.041 μs | 1.526 μs |  99.22 μs |    99.41 | 10,347.8 |  6.27 |    0.27 |      - |    1489 B |        2.40 |
| NpgsqlPooledReusedScalarBurstAsync       | ReusedMultiplexedScalar     | 100.29 μs | 1.632 μs | 2.442 μs | 104.93 μs |   105.43 |  9,971.1 |  6.51 |    0.31 |      - |    1883 B |        3.03 |
