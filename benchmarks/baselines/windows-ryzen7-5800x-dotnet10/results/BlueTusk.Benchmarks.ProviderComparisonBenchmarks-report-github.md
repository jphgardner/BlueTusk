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
| BlueTuskParameterizedScalarAsync        | ParameterizedScalar        |   446,436.1 ns |  10,074.70 ns |  15,079.33 ns |   470,025.3 ns |  1.00 |    0.05 |      - |    1663 B |        1.00 |
| NpgsqlParameterizedScalarAsync          | ParameterizedScalar        |   486,766.7 ns |  10,092.76 ns |  15,106.37 ns |   512,441.7 ns |  1.09 |    0.05 |      - |    2094 B |        1.26 |
|                                         |                            |                |               |               |                |       |         |        |           |             |
| BlueTuskPoolCheckoutAsync               | PoolCheckout               |       287.5 ns |       6.51 ns |       9.74 ns |       299.3 ns |  1.00 |    0.05 | 0.0100 |     168 B |        1.00 |
| NpgsqlPoolCheckoutAsync                 | PoolCheckout               |       326.4 ns |       8.39 ns |      12.30 ns |       341.5 ns |  1.14 |    0.06 | 0.0110 |     184 B |        1.10 |
|                                         |                            |                |               |               |                |       |         |        |           |             |
| BlueTuskPreparedScalarAsync             | PreparedScalar             |   435,901.1 ns |   7,424.79 ns |  11,113.08 ns |   450,524.9 ns |  1.00 |    0.04 |      - |     796 B |        1.00 |
| NpgsqlPreparedScalarAsync               | PreparedScalar             |   445,264.2 ns |   9,662.23 ns |  14,461.98 ns |   469,877.9 ns |  1.02 |    0.04 |      - |    1099 B |        1.38 |
|                                         |                            |                |               |               |                |       |         |        |           |             |
| BlueTuskSequential1000RowsAsync         | Sequential1000Rows         |   671,915.8 ns |  14,717.68 ns |  22,028.73 ns |   711,970.0 ns |  1.00 |    0.05 |      - |    1400 B |        1.00 |
| NpgsqlSequential1000RowsAsync           | Sequential1000Rows         |   742,850.7 ns |  22,566.54 ns |  33,776.54 ns |   786,030.2 ns |  1.11 |    0.06 |      - |    1529 B |        1.09 |
|                                         |                            |                |               |               |                |       |         |        |           |             |
| BlueTuskSequentialOneMegabyteByteaAsync | SequentialOneMegabyteBytea | 4,389,873.2 ns | 144,038.95 ns | 215,590.73 ns | 4,742,412.3 ns |  1.00 |    0.07 |      - |    3900 B |        1.00 |
| NpgsqlSequentialOneMegabyteByteaAsync   | SequentialOneMegabyteBytea | 4,482,051.5 ns | 182,427.69 ns | 273,049.20 ns | 4,948,558.4 ns |  1.02 |    0.08 |      - |    8938 B |        2.29 |
