```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.303
  [Host] : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3

Job=MediumRun  Toolchain=InProcessEmitToolchain  IterationCount=15
LaunchCount=2  WarmupCount=10

```
| Method                                  | Categories                 | Mean           | Error        | StdDev       | Median         | P95            | P99 (us) | Op/s        | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------------------------------------- |--------------------------- |---------------:|-------------:|-------------:|---------------:|---------------:|---------:|------------:|------:|--------:|-------:|----------:|------------:|
| BlueTuskParameterizedScalarAsync        | ParameterizedScalar        |   297,686.5 ns |  2,632.36 ns |  3,858.47 ns |   297,453.8 ns |   304,668.3 ns |   306.62 |     3,359.2 |  1.00 |    0.02 |      - |    1652 B |        1.00 |
| NpgsqlParameterizedScalarAsync          | ParameterizedScalar        |   328,194.7 ns |  8,005.27 ns | 11,734.02 ns |   323,284.6 ns |   350,963.6 ns |   355.82 |     3,047.0 |  1.10 |    0.04 |      - |    2140 B |        1.30 |
|                                         |                            |                |              |              |                |                |          |             |       |         |        |           |             |
| BlueTuskPoolCheckoutAsync               | PoolCheckout               |       213.8 ns |      3.09 ns |      4.63 ns |       213.2 ns |       220.5 ns |     0.22 | 4,677,051.8 |  1.00 |    0.03 | 0.0100 |     168 B |        1.00 |
| NpgsqlPoolCheckoutAsync                 | PoolCheckout               |       235.2 ns |      5.07 ns |      7.59 ns |       234.6 ns |       246.1 ns |     0.25 | 4,251,398.2 |  1.10 |    0.04 | 0.0110 |     184 B |        1.10 |
|                                         |                            |                |              |              |                |                |          |             |       |         |        |           |             |
| BlueTuskPreparedScalarAsync             | PreparedScalar             |   291,522.6 ns |  3,476.71 ns |  4,873.87 ns |   291,935.2 ns |   299,032.1 ns |   299.97 |     3,430.3 |  1.00 |    0.02 |      - |     785 B |        1.00 |
| NpgsqlPreparedScalarAsync               | PreparedScalar             |   297,467.4 ns |  3,731.41 ns |  5,469.45 ns |   297,753.3 ns |   306,844.9 ns |   307.85 |     3,361.7 |  1.02 |    0.03 |      - |    1123 B |        1.43 |
|                                         |                            |                |              |              |                |                |          |             |       |         |        |           |             |
| BlueTuskSequential1000RowsAsync         | Sequential1000Rows         |   487,347.6 ns |  5,279.53 ns |  7,571.75 ns |   486,616.8 ns |   499,721.2 ns |   501.60 |     2,051.9 |  1.00 |    0.02 |      - |    1195 B |        1.00 |
| NpgsqlSequential1000RowsAsync           | Sequential1000Rows         |   528,428.4 ns |  5,386.07 ns |  7,724.55 ns |   528,657.1 ns |   541,150.4 ns |   545.91 |     1,892.4 |  1.08 |    0.02 |      - |    1569 B |        1.31 |
|                                         |                            |                |              |              |                |                |          |             |       |         |        |           |             |
| BlueTuskSequentialOneMegabyteByteaAsync | SequentialOneMegabyteBytea | 2,182,848.1 ns | 65,160.61 ns | 97,529.34 ns | 2,126,151.3 ns | 2,340,999.9 ns | 2,397.65 |       458.1 |  1.00 |    0.06 |      - |    1466 B |        1.00 |
| NpgsqlSequentialOneMegabyteByteaAsync   | SequentialOneMegabyteBytea | 2,223,267.7 ns | 59,106.77 ns | 86,637.95 ns | 2,200,855.9 ns | 2,402,037.4 ns | 2,436.65 |       449.8 |  1.02 |    0.06 |      - |    9031 B |        6.16 |
