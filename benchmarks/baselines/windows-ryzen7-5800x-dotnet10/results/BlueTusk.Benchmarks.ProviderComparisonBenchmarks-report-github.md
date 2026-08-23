```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.303
  [Host] : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3

Job=MediumRun  Toolchain=InProcessEmitToolchain  IterationCount=15
LaunchCount=2  WarmupCount=10

```
| Method                                  | Categories                 | Mean           | Error        | StdDev        | P95            | P99 (us) | Op/s        | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------------------------------------- |--------------------------- |---------------:|-------------:|--------------:|---------------:|---------:|------------:|------:|--------:|-------:|----------:|------------:|
| BlueTuskParameterizedScalarAsync        | ParameterizedScalar        |   295,273.8 ns |  2,837.21 ns |   3,977.38 ns |   301,447.3 ns |   302.89 |     3,386.7 |  1.00 |    0.02 |      - |    1773 B |        1.00 |
| NpgsqlParameterizedScalarAsync          | ParameterizedScalar        |   320,809.2 ns |  4,200.91 ns |   6,287.72 ns |   330,604.4 ns |   333.84 |     3,117.1 |  1.09 |    0.03 |      - |    2138 B |        1.21 |
|                                         |                            |                |              |               |                |          |             |       |         |        |           |             |
| BlueTuskPoolCheckoutAsync               | PoolCheckout               |       211.1 ns |      4.15 ns |       6.21 ns |       221.9 ns |     0.23 | 4,737,692.8 |  1.00 |    0.04 | 0.0100 |     168 B |        1.00 |
| NpgsqlPoolCheckoutAsync                 | PoolCheckout               |       227.6 ns |      8.10 ns |      12.13 ns |       252.0 ns |     0.26 | 4,394,359.7 |  1.08 |    0.06 | 0.0110 |     184 B |        1.10 |
|                                         |                            |                |              |               |                |          |             |       |         |        |           |             |
| BlueTuskPreparedScalarAsync             | PreparedScalar             |   291,129.0 ns |  2,805.38 ns |   4,112.10 ns |   296,279.6 ns |   299.06 |     3,434.9 |  1.00 |    0.02 |      - |     898 B |        1.00 |
| NpgsqlPreparedScalarAsync               | PreparedScalar             |   290,879.5 ns |  2,364.15 ns |   3,465.33 ns |   295,051.6 ns |   300.17 |     3,437.8 |  1.00 |    0.02 |      - |    1125 B |        1.25 |
|                                         |                            |                |              |               |                |          |             |       |         |        |           |             |
| BlueTuskSequential1000RowsAsync         | Sequential1000Rows         |   480,223.1 ns |  3,445.01 ns |   5,049.65 ns |   488,090.2 ns |   488.80 |     2,082.4 |  1.00 |    0.01 |      - |    1585 B |        1.00 |
| NpgsqlSequential1000RowsAsync           | Sequential1000Rows         |   508,494.8 ns |  7,026.69 ns |  10,299.63 ns |   528,007.8 ns |   534.67 |     1,966.6 |  1.06 |    0.02 |      - |    1615 B |        1.02 |
|                                         |                            |                |              |               |                |          |             |       |         |        |           |             |
| BlueTuskSequentialOneMegabyteByteaAsync | SequentialOneMegabyteBytea | 2,236,289.0 ns | 56,968.24 ns |  85,267.38 ns | 2,396,624.6 ns | 2,419.98 |       447.2 |  1.00 |    0.05 |      - |    1585 B |        1.00 |
| NpgsqlSequentialOneMegabyteByteaAsync   | SequentialOneMegabyteBytea | 2,291,289.6 ns | 91,718.39 ns | 134,439.64 ns | 2,550,526.2 ns | 2,673.66 |       436.4 |  1.03 |    0.07 |      - |    8906 B |        5.62 |
