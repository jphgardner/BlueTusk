```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host] : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=MediumRun  Toolchain=InProcessEmitToolchain  IterationCount=15
LaunchCount=2  WarmupCount=10

```
| Method                                   | Categories                  | Mean      | Error    | StdDev   | P95       | P99 (us) | Op/s     | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------------------------------- |---------------------------- |----------:|---------:|---------:|----------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| BlueTuskConcurrentScalarBurstAsync       | ConcurrentMultiplexedScalar |  19.83 μs | 0.451 μs | 0.647 μs |  20.93 μs |    21.06 | 50,423.0 |  1.00 |    0.05 | 0.0916 |    1733 B |        1.00 |
| NpgsqlConcurrentScalarBurstAsync         | ConcurrentMultiplexedScalar |  20.57 μs | 0.587 μs | 0.879 μs |  22.26 μs |    22.51 | 48,614.1 |  1.04 |    0.05 | 0.0916 |    1738 B |        1.00 |
| BlueTuskPooledConcurrentScalarBurstAsync | ConcurrentMultiplexedScalar | 197.61 μs | 2.949 μs | 4.414 μs | 205.63 μs |   207.14 |  5,060.5 |  9.97 |    0.38 |      - |    3987 B |        2.30 |
| NpgsqlPooledConcurrentScalarBurstAsync   | ConcurrentMultiplexedScalar | 106.15 μs | 1.398 μs | 1.959 μs | 109.53 μs |   110.18 |  9,420.4 |  5.36 |    0.20 | 0.1221 |    2826 B |        1.63 |
|                                          |                             |           |          |          |           |          |          |       |         |        |           |             |
| BlueTuskReusedScalarBurstAsync           | ReusedMultiplexedScalar     |  17.41 μs | 0.683 μs | 1.001 μs |  19.34 μs |    20.04 | 57,426.9 |  1.00 |    0.08 | 0.0610 |    1143 B |        1.00 |
| NpgsqlReusedScalarBurstAsync             | ReusedMultiplexedScalar     |  20.01 μs | 0.648 μs | 0.950 μs |  21.53 μs |    21.69 | 49,975.6 |  1.15 |    0.08 | 0.0305 |     794 B |        0.69 |
| BlueTuskPooledReusedScalarBurstAsync     | ReusedMultiplexedScalar     | 193.08 μs | 3.236 μs | 4.320 μs | 199.28 μs |   204.97 |  5,179.2 | 11.12 |    0.64 |      - |    3391 B |        2.97 |
| NpgsqlPooledReusedScalarBurstAsync       | ReusedMultiplexedScalar     | 105.20 μs | 2.610 μs | 3.907 μs | 111.28 μs |   114.82 |  9,506.0 |  6.06 |    0.39 |      - |    1880 B |        1.64 |
