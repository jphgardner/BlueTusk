```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.303
  [Host]   : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1
WarmupCount=3

```
| Method                                   | Categories                  | Mean             | Error           | StdDev         | P95              | P99 (us)  | Op/s          | Ratio    | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------------------------------- |---------------------------- |-----------------:|----------------:|---------------:|-----------------:|----------:|--------------:|---------:|--------:|-------:|----------:|------------:|
| BlueTuskBatch16ParameterizedScalarsAsync | Batch16ParameterizedScalars |    516,704.48 ns |    89,044.07 ns |   4,880.806 ns |    521,005.13 ns |    521.37 |      1,935.34 |     1.00 |    0.01 |      - |    8913 B |        1.00 |
| NpgsqlBatch16ParameterizedScalarsAsync   | Batch16ParameterizedScalars |    597,259.47 ns |    49,064.96 ns |   2,689.416 ns |    599,870.40 ns |    600.17 |      1,674.31 |     1.16 |    0.01 |      - |    9071 B |        1.02 |
|                                          |                             |                  |                 |                |                  |           |               |          |         |        |           |             |
| BlueTuskBeginRollbackTransactionAsync    | BeginRollbackTransaction    |         62.29 ns |        24.25 ns |       1.329 ns |         63.59 ns |      0.06 | 16,054,177.07 |     1.00 |    0.03 | 0.0029 |      48 B |        1.00 |
| NpgsqlBeginRollbackTransactionAsync      | BeginRollbackTransaction    |    430,869.87 ns |    54,676.88 ns |   2,997.024 ns |    433,812.47 ns |    434.23 |      2,320.89 | 6,919.34 |  133.00 |      - |    1033 B |       21.52 |
|                                          |                             |                  |                 |                |                  |           |               |          |         |        |           |             |
| BlueTuskBinaryCopyExport1000RowsAsync    | BinaryCopyExport1000Rows    |  1,111,938.48 ns |   228,016.24 ns |  12,498.340 ns |  1,124,268.20 ns |  1,125.90 |        899.33 |     1.00 |    0.01 | 1.9531 |   49487 B |        1.00 |
| NpgsqlBinaryCopyExport1000RowsAsync      | BinaryCopyExport1000Rows    |  1,110,187.17 ns |   517,695.24 ns |  28,376.623 ns |  1,131,615.47 ns |  1,132.72 |        900.75 |     1.00 |    0.02 | 1.9531 |   50048 B |        1.01 |
|                                          |                             |                  |                 |                |                  |           |               |          |         |        |           |             |
| BlueTuskBinaryCopyImport1000RowsAsync    | BinaryCopyImport1000Rows    |  3,134,087.24 ns |   470,146.95 ns |  25,770.341 ns |  3,157,265.12 ns |  3,159.32 |        319.07 |     1.00 |    0.01 |      - |    1686 B |        1.00 |
| NpgsqlBinaryCopyImport1000RowsAsync      | BinaryCopyImport1000Rows    |  3,146,391.67 ns |   784,379.19 ns |  42,994.470 ns |  3,188,803.83 ns |  3,194.44 |        317.82 |     1.00 |    0.01 |      - |    3034 B |        1.80 |
|                                          |                             |                  |                 |                |                  |           |               |          |         |        |           |             |
| BlueTuskEfCompiledQueryAsync             | EfCompiledQuery             |    632,203.71 ns |    62,240.23 ns |   3,411.597 ns |    635,226.69 ns |    635.48 |      1,581.77 |     1.00 |    0.01 | 1.9531 |   34626 B |        1.00 |
| NpgsqlEfCompiledQueryAsync               | EfCompiledQuery             |    629,179.92 ns |    40,422.63 ns |   2,215.701 ns |    631,345.91 ns |    631.60 |      1,589.37 |     1.00 |    0.01 | 1.9531 |   37006 B |        1.07 |
|                                          |                             |                  |                 |                |                  |           |               |          |         |        |           |             |
| BlueTuskEfInsertOneAsync                 | EfInsertOne                 |  1,960,260.55 ns |   933,971.33 ns |  51,194.120 ns |  2,000,406.41 ns |  2,002.78 |        510.14 |     1.00 |    0.03 |      - |   51033 B |        1.00 |
| NpgsqlEfInsertOneAsync                   | EfInsertOne                 |  2,035,565.62 ns |   986,973.44 ns |  54,099.345 ns |  2,088,905.66 ns |  2,096.10 |        491.26 |     1.04 |    0.03 |      - |   52057 B |        1.02 |
|                                          |                             |                  |                 |                |                  |           |               |          |         |        |           |             |
| BlueTuskEfMaterialize100RowsAsync        | EfMaterialize100Rows        |    825,121.03 ns |   601,027.88 ns |  32,944.366 ns |    853,049.04 ns |    855.14 |      1,211.94 |     1.00 |    0.05 | 3.9063 |   74979 B |        1.00 |
| NpgsqlEfMaterialize100RowsAsync          | EfMaterialize100Rows        |    855,093.55 ns |   348,585.23 ns |  19,107.133 ns |    872,498.55 ns |    874.10 |      1,169.46 |     1.04 |    0.04 | 3.9063 |   76945 B |        1.03 |
|                                          |                             |                  |                 |                |                  |           |               |          |         |        |           |             |
| BlueTuskEfUpdateOneAsync                 | EfUpdateOne                 |  2,611,416.02 ns |   331,532.20 ns |  18,172.398 ns |  2,629,343.59 ns |  2,631.72 |        382.93 |     1.00 |    0.01 |      - |   55387 B |        1.00 |
| NpgsqlEfUpdateOneAsync                   | EfUpdateOne                 |  2,551,117.19 ns | 2,057,369.53 ns | 112,771.367 ns |  2,658,667.62 ns |  2,670.11 |        391.99 |     0.98 |    0.04 |      - |   57065 B |        1.03 |
|                                          |                             |                  |                 |                |                  |           |               |          |         |        |           |             |
| BlueTuskLargeObjectReadOneMegabyteAsync  | LargeObjectReadOneMegabyte  | 10,998,691.67 ns | 1,383,128.66 ns |  75,813.949 ns | 11,061,242.34 ns | 11,065.58 |         90.92 |     1.00 |    0.01 |      - |   13445 B |        1.00 |
| NpgsqlLargeObjectReadOneMegabyteAsync    | LargeObjectReadOneMegabyte  | 11,767,771.35 ns | 1,508,889.54 ns |  82,707.328 ns | 11,849,189.53 ns | 11,860.41 |         84.98 |     1.07 |    0.01 |      - |   22954 B |        1.71 |
|                                          |                             |                  |                 |                |                  |           |               |          |         |        |           |             |
| BlueTuskNotificationDeliveryAsync        | NotificationDelivery        |    514,448.67 ns |    29,972.66 ns |   1,642.902 ns |    515,613.82 ns |    515.66 |      1,943.83 |     1.00 |    0.00 |      - |    1548 B |        1.00 |
| NpgsqlNotificationDeliveryAsync          | NotificationDelivery        |    570,993.68 ns |   764,777.19 ns |  41,920.018 ns |    612,321.91 ns |    617.61 |      1,751.33 |     1.11 |    0.07 |      - |    1852 B |        1.20 |
|                                          |                             |                  |                 |                |                  |           |               |          |         |        |           |             |
| BlueTuskParameterizedScalarAsync         | ParameterizedScalar         |    424,209.03 ns |   153,325.76 ns |   8,404.302 ns |    432,451.69 ns |    433.45 |      2,357.33 |     1.00 |    0.02 |      - |    1414 B |        1.00 |
| NpgsqlParameterizedScalarAsync           | ParameterizedScalar         |    447,380.08 ns |    79,193.07 ns |   4,340.840 ns |    451,056.48 ns |    451.33 |      2,235.24 |     1.05 |    0.02 |      - |    2147 B |        1.52 |
|                                          |                             |                  |                 |                |                  |           |               |          |         |        |           |             |
| BlueTuskPoolCheckoutAsync                | PoolCheckout                |        199.53 ns |        53.52 ns |       2.934 ns |        202.42 ns |      0.20 |  5,011,762.57 |     1.00 |    0.02 | 0.0105 |     176 B |        1.00 |
| NpgsqlPoolCheckoutAsync                  | PoolCheckout                |        228.66 ns |       421.24 ns |      23.090 ns |        251.30 ns |      0.25 |  4,373,316.22 |     1.15 |    0.10 | 0.0110 |     184 B |        1.05 |
|                                          |                             |                  |                 |                |                  |           |               |          |         |        |           |             |
| BlueTuskPreparedScalarAsync              | PreparedScalar              |    415,783.22 ns |     6,900.99 ns |     378.267 ns |    416,049.84 ns |    416.06 |      2,405.10 |     1.00 |    0.00 |      - |     825 B |        1.00 |
| NpgsqlPreparedScalarAsync                | PreparedScalar              |    454,998.07 ns |    86,325.86 ns |   4,731.812 ns |    459,617.41 ns |    460.16 |      2,197.81 |     1.09 |    0.01 |      - |    1110 B |        1.35 |
|                                          |                             |                  |                 |                |                  |           |               |          |         |        |           |             |
| BlueTuskPreparedTypedRowRoundTripAsync   | PreparedTypedRowRoundTrip   |    441,890.17 ns |     2,530.55 ns |     138.708 ns |    441,976.56 ns |    441.98 |      2,263.01 |     1.00 |    0.00 |      - |    1152 B |        1.00 |
| NpgsqlPreparedTypedRowRoundTripAsync     | PreparedTypedRowRoundTrip   |    447,843.54 ns |   136,995.62 ns |   7,509.192 ns |    453,507.59 ns |    453.80 |      2,232.92 |     1.01 |    0.01 |      - |    1373 B |        1.19 |
|                                          |                             |                  |                 |                |                  |           |               |          |         |        |           |             |
| BlueTuskSequential1000RowsAsync          | Sequential1000Rows          |    617,262.16 ns |    81,768.40 ns |   4,482.002 ns |    620,637.20 ns |    620.81 |      1,620.06 |     1.00 |    0.01 |      - |    1154 B |        1.00 |
| NpgsqlSequential1000RowsAsync            | Sequential1000Rows          |    643,801.17 ns |    83,433.92 ns |   4,573.295 ns |    647,595.35 ns |    647.86 |      1,553.27 |     1.04 |    0.01 |      - |    1505 B |        1.30 |
|                                          |                             |                  |                 |                |                  |           |               |          |         |        |           |             |
| BlueTuskSequentialOneMegabyteByteaAsync  | SequentialOneMegabyteBytea  |  6,013,255.47 ns | 3,680,992.69 ns | 201,767.631 ns |  6,212,088.20 ns |  6,239.14 |        166.30 |     1.00 |    0.04 |      - |    1310 B |        1.00 |
| NpgsqlSequentialOneMegabyteByteaAsync    | SequentialOneMegabyteBytea  |  6,234,911.20 ns | 4,979,934.19 ns | 272,966.998 ns |  6,462,661.09 ns |  6,478.97 |        160.39 |     1.04 |    0.05 |      - |   15219 B |       11.62 |
