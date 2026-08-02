```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]    : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  MediumRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=MediumRun  IterationCount=15  LaunchCount=2
WarmupCount=10

```
| Method                                  | Categories                 | Mean           | Error         | StdDev        | P95            | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------------------------------------- |--------------------------- |---------------:|--------------:|--------------:|---------------:|------:|--------:|-------:|----------:|------------:|
| BlueTuskParameterizedScalarAsync        | ParameterizedScalar        |   480,511.8 ns |  10,202.94 ns |  14,955.33 ns |   504,302.5 ns |  1.00 |    0.04 |      - |    1642 B |        1.00 |
| NpgsqlParameterizedScalarAsync          | ParameterizedScalar        |   480,799.8 ns |  10,197.50 ns |  15,263.14 ns |   505,675.7 ns |  1.00 |    0.04 |      - |    2079 B |        1.27 |
|                                         |                            |                |               |               |                |       |         |        |           |             |
| BlueTuskPoolCheckoutAsync               | PoolCheckout               |       303.0 ns |       7.57 ns |      11.34 ns |       318.7 ns |  1.00 |    0.05 | 0.0100 |     168 B |        1.00 |
| NpgsqlPoolCheckoutAsync                 | PoolCheckout               |       327.1 ns |      11.81 ns |      17.68 ns |       345.9 ns |  1.08 |    0.07 | 0.0110 |     184 B |        1.10 |
|                                         |                            |                |               |               |                |       |         |        |           |             |
| BlueTuskPreparedScalarAsync             | PreparedScalar             |   436,637.5 ns |  11,327.79 ns |  16,954.91 ns |   457,792.7 ns |  1.00 |    0.05 |      - |     772 B |        1.00 |
| NpgsqlPreparedScalarAsync               | PreparedScalar             |   439,543.6 ns |   9,031.54 ns |  13,517.98 ns |   455,052.2 ns |  1.01 |    0.05 |      - |    1074 B |        1.39 |
|                                         |                            |                |               |               |                |       |         |        |           |             |
| BlueTuskSequential1000RowsAsync         | Sequential1000Rows         |   767,362.1 ns |  18,631.41 ns |  27,886.61 ns |   818,840.6 ns |  1.00 |    0.05 |      - |    1558 B |        1.00 |
| NpgsqlSequential1000RowsAsync           | Sequential1000Rows         |   710,371.0 ns |  14,952.10 ns |  21,916.59 ns |   744,700.4 ns |  0.93 |    0.04 |      - |    1496 B |        0.96 |
|                                         |                            |                |               |               |                |       |         |        |           |             |
| BlueTuskSequentialOneMegabyteByteaAsync | SequentialOneMegabyteBytea | 4,660,317.5 ns | 156,971.15 ns | 234,947.04 ns | 4,947,639.7 ns |  1.00 |    0.07 |      - |    4288 B |        1.00 |
| NpgsqlSequentialOneMegabyteByteaAsync   | SequentialOneMegabyteBytea | 4,665,226.0 ns | 151,468.21 ns | 226,710.50 ns | 4,937,744.6 ns |  1.00 |    0.07 |      - |    8829 B |        2.06 |
