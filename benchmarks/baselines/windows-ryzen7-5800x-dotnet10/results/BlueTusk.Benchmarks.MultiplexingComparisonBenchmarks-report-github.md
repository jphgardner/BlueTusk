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
| BlueTuskConcurrentScalarBurstAsync       | ConcurrentMultiplexedScalar |  16.93 μs | 0.466 μs | 0.668 μs |  18.06 μs |    18.65 | 59,061.1 |  1.00 |    0.05 | 0.0610 |    1429 B |        1.00 |
| NpgsqlConcurrentScalarBurstAsync         | ConcurrentMultiplexedScalar |  18.81 μs | 0.486 μs | 0.728 μs |  20.06 μs |    20.21 | 53,156.2 |  1.11 |    0.06 | 0.0916 |    1738 B |        1.22 |
| BlueTuskPooledConcurrentScalarBurstAsync | ConcurrentMultiplexedScalar |  95.98 μs | 1.655 μs | 2.477 μs |  99.07 μs |   100.53 | 10,419.3 |  5.68 |    0.26 | 0.1221 |    2127 B |        1.49 |
| NpgsqlPooledConcurrentScalarBurstAsync   | ConcurrentMultiplexedScalar | 101.55 μs | 1.800 μs | 2.638 μs | 106.51 μs |   107.24 |  9,847.3 |  6.01 |    0.27 | 0.1221 |    2830 B |        1.98 |
|                                          |                             |           |          |          |           |          |          |       |         |        |           |             |
| BlueTuskReusedScalarBurstAsync           | ReusedMultiplexedScalar     |  15.49 μs | 0.327 μs | 0.469 μs |  16.52 μs |    16.64 | 64,542.8 |  1.00 |    0.04 | 0.0305 |     622 B |        1.00 |
| NpgsqlReusedScalarBurstAsync             | ReusedMultiplexedScalar     |  19.06 μs | 0.523 μs | 0.766 μs |  20.32 μs |    20.34 | 52,459.6 |  1.23 |    0.06 | 0.0305 |     794 B |        1.28 |
| BlueTuskPooledReusedScalarBurstAsync     | ReusedMultiplexedScalar     |  95.10 μs | 1.913 μs | 2.804 μs |  98.99 μs |   102.55 | 10,515.5 |  6.14 |    0.25 |      - |    1343 B |        2.16 |
| NpgsqlPooledReusedScalarBurstAsync       | ReusedMultiplexedScalar     | 100.72 μs | 1.300 μs | 1.946 μs | 104.37 μs |   104.88 |  9,929.0 |  6.51 |    0.22 |      - |    1873 B |        3.01 |
