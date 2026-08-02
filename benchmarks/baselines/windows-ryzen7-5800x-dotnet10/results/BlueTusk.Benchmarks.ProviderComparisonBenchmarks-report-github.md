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
| BlueTuskParameterizedScalarAsync        | ParameterizedScalar        |   497,468.1 ns |   9,405.06 ns |  14,077.05 ns |   515,183.3 ns |  1.00 |    0.04 |      - |    1650 B |        1.00 |
| NpgsqlParameterizedScalarAsync          | ParameterizedScalar        |   486,700.3 ns |   8,380.95 ns |  12,019.71 ns |   500,515.9 ns |  0.98 |    0.04 |      - |    2084 B |        1.26 |
|                                         |                            |                |               |               |                |       |         |        |           |             |
| BlueTuskPoolCheckoutAsync               | PoolCheckout               |       296.3 ns |       6.60 ns |       9.88 ns |       310.2 ns |  1.00 |    0.05 | 0.0100 |     168 B |        1.00 |
| NpgsqlPoolCheckoutAsync                 | PoolCheckout               |       332.1 ns |      12.32 ns |      18.44 ns |       356.3 ns |  1.12 |    0.07 | 0.0110 |     184 B |        1.10 |
|                                         |                            |                |               |               |                |       |         |        |           |             |
| BlueTuskPreparedScalarAsync             | PreparedScalar             |   442,989.9 ns |   9,763.87 ns |  14,614.11 ns |   467,381.0 ns |  1.00 |    0.05 |      - |     792 B |        1.00 |
| NpgsqlPreparedScalarAsync               | PreparedScalar             |   444,004.7 ns |   8,891.43 ns |  13,308.27 ns |   466,093.7 ns |  1.00 |    0.04 |      - |    1089 B |        1.38 |
|                                         |                            |                |               |               |                |       |         |        |           |             |
| BlueTuskSequential1000RowsAsync         | Sequential1000Rows         |   677,522.7 ns |  15,646.08 ns |  22,933.83 ns |   708,629.1 ns |  1.00 |    0.05 |      - |    1389 B |        1.00 |
| NpgsqlSequential1000RowsAsync           | Sequential1000Rows         |   736,880.9 ns |  37,377.19 ns |  53,605.25 ns |   850,487.3 ns |  1.09 |    0.09 |      - |    1508 B |        1.09 |
|                                         |                            |                |               |               |                |       |         |        |           |             |
| BlueTuskSequentialOneMegabyteByteaAsync | SequentialOneMegabyteBytea | 4,395,401.2 ns | 117,454.79 ns | 172,163.72 ns | 4,647,984.1 ns |  1.00 |    0.05 |      - |    4132 B |        1.00 |
| NpgsqlSequentialOneMegabyteByteaAsync   | SequentialOneMegabyteBytea | 4,212,856.6 ns | 126,360.45 ns | 185,217.52 ns | 4,497,574.8 ns |  0.96 |    0.06 |      - |    8851 B |        2.14 |
