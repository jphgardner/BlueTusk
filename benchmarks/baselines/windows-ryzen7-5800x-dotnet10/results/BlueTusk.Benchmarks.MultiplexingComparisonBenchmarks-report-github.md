```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host] : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=MediumRun  Toolchain=InProcessEmitToolchain  IterationCount=15
LaunchCount=2  WarmupCount=10

```
| Method                             | Categories                  | Mean     | Error    | StdDev   | P95      | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------------------------- |---------------------------- |---------:|---------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| BlueTuskConcurrentScalarBurstAsync | ConcurrentMultiplexedScalar | 21.71 μs | 1.057 μs | 1.516 μs | 24.63 μs |  1.00 |    0.09 | 0.0916 |    1727 B |        1.00 |
| NpgsqlConcurrentScalarBurstAsync   | ConcurrentMultiplexedScalar | 24.33 μs | 1.877 μs | 2.692 μs | 29.00 μs |  1.13 |    0.14 | 0.0916 |    1738 B |        1.01 |
|                                    |                             |          |          |          |          |       |         |        |           |             |
| BlueTuskReusedScalarBurstAsync     | ReusedMultiplexedScalar     | 18.84 μs | 0.683 μs | 0.958 μs | 20.77 μs |  1.00 |    0.07 | 0.0610 |    1127 B |        1.00 |
| NpgsqlReusedScalarBurstAsync       | ReusedMultiplexedScalar     | 27.66 μs | 2.700 μs | 4.042 μs | 34.48 μs |  1.47 |    0.22 |      - |     794 B |        0.70 |
