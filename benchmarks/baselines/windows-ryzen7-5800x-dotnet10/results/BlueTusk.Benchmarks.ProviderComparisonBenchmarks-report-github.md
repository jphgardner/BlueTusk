```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.303
  [Host]   : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1
WarmupCount=3

```
| Method                                   | Categories                  | Mean             | Error            | StdDev         | P95              | P99 (us)  | Op/s          | Ratio    | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------------------------------- |---------------------------- |-----------------:|-----------------:|---------------:|-----------------:|----------:|--------------:|---------:|--------:|-------:|----------:|------------:|
| BlueTuskBatch16ParameterizedScalarsAsync | Batch16ParameterizedScalars |    670,977.26 ns |     79,581.35 ns |   4,362.122 ns |    674,652.10 ns |    674.92 |      1,490.36 |     1.00 |    0.01 |      - |   15712 B |        1.00 |
| NpgsqlBatch16ParameterizedScalarsAsync   | Batch16ParameterizedScalars |    585,382.16 ns |     69,474.64 ns |   3,808.139 ns |    589,135.62 ns |    589.61 |      1,708.29 |     0.87 |    0.01 |      - |    9124 B |        0.58 |
|                                          |                             |                  |                  |                |                  |           |               |          |         |        |           |             |
| BlueTuskBeginRollbackTransactionAsync    | BeginRollbackTransaction    |         65.99 ns |         10.69 ns |       0.586 ns |         66.40 ns |      0.07 | 15,154,858.13 |     1.00 |    0.01 | 0.0029 |      48 B |        1.00 |
| NpgsqlBeginRollbackTransactionAsync      | BeginRollbackTransaction    |    436,309.03 ns |    137,627.44 ns |   7,543.824 ns |    443,510.89 ns |    444.28 |      2,291.95 | 6,612.55 |  111.41 |      - |    1017 B |       21.19 |
|                                          |                             |                  |                  |                |                  |           |               |          |         |        |           |             |
| BlueTuskBinaryCopyExport1000RowsAsync    | BinaryCopyExport1000Rows    |  1,090,443.88 ns |    264,389.61 ns |  14,492.087 ns |  1,102,294.32 ns |  1,103.09 |        917.06 |     1.00 |    0.02 | 1.9531 |   51746 B |        1.00 |
| NpgsqlBinaryCopyExport1000RowsAsync      | BinaryCopyExport1000Rows    |  1,076,567.25 ns |     37,389.87 ns |   2,049.465 ns |  1,078,433.28 ns |  1,078.60 |        928.88 |     0.99 |    0.01 | 1.9531 |   49958 B |        0.97 |
|                                          |                             |                  |                  |                |                  |           |               |          |         |        |           |             |
| BlueTuskBinaryCopyImport1000RowsAsync    | BinaryCopyImport1000Rows    |  3,066,610.29 ns |     43,887.66 ns |   2,405.631 ns |  3,068,655.20 ns |  3,068.81 |        326.09 |     1.00 |    0.00 |      - |    5256 B |        1.00 |
| NpgsqlBinaryCopyImport1000RowsAsync      | BinaryCopyImport1000Rows    |  3,153,602.67 ns |  1,160,629.14 ns |  63,618.000 ns |  3,216,133.95 ns |  3,224.87 |        317.10 |     1.03 |    0.02 |      - |    3021 B |        0.57 |
|                                          |                             |                  |                  |                |                  |           |               |          |         |        |           |             |
| BlueTuskEfCompiledQueryAsync             | EfCompiledQuery             |    624,328.94 ns |     14,243.21 ns |     780.718 ns |    624,918.67 ns |    624.95 |      1,601.72 |     1.00 |    0.00 | 1.9531 |   42387 B |        1.00 |
| NpgsqlEfCompiledQueryAsync               | EfCompiledQuery             |    637,792.81 ns |    139,411.60 ns |   7,641.620 ns |    645,155.47 ns |    645.97 |      1,567.91 |     1.02 |    0.01 | 1.9531 |   37007 B |        0.87 |
|                                          |                             |                  |                  |                |                  |           |               |          |         |        |           |             |
| BlueTuskEfInsertOneAsync                 | EfInsertOne                 |  1,957,551.56 ns |  1,311,303.79 ns |  71,876.986 ns |  2,007,926.72 ns |  2,009.81 |        510.84 |     1.00 |    0.05 |      - |   64675 B |        1.00 |
| NpgsqlEfInsertOneAsync                   | EfInsertOne                 |  2,005,207.68 ns |    412,058.47 ns |  22,586.315 ns |  2,018,295.82 ns |  2,018.31 |        498.70 |     1.03 |    0.03 |      - |   52200 B |        0.81 |
|                                          |                             |                  |                  |                |                  |           |               |          |         |        |           |             |
| BlueTuskEfMaterialize100RowsAsync        | EfMaterialize100Rows        |    848,132.68 ns |    204,064.09 ns |  11,185.442 ns |    858,892.13 ns |    860.07 |      1,179.06 |     1.00 |    0.02 | 3.9063 |   88405 B |        1.00 |
| NpgsqlEfMaterialize100RowsAsync          | EfMaterialize100Rows        |    860,058.72 ns |    103,062.77 ns |   5,649.218 ns |    865,311.21 ns |    865.83 |      1,162.71 |     1.01 |    0.01 | 3.9063 |   76984 B |        0.87 |
|                                          |                             |                  |                  |                |                  |           |               |          |         |        |           |             |
| BlueTuskEfUpdateOneAsync                 | EfUpdateOne                 |  2,593,971.09 ns |  2,172,964.67 ns | 119,107.527 ns |  2,678,363.12 ns |  2,681.70 |        385.51 |     1.00 |    0.06 | 3.9063 |   72076 B |        1.00 |
| NpgsqlEfUpdateOneAsync                   | EfUpdateOne                 |  2,510,686.20 ns |    963,654.27 ns |  52,821.142 ns |  2,554,594.61 ns |  2,557.71 |        398.30 |     0.97 |    0.04 |      - |   56974 B |        0.79 |
|                                          |                             |                  |                  |                |                  |           |               |          |         |        |           |             |
| BlueTuskLargeObjectReadOneMegabyteAsync  | LargeObjectReadOneMegabyte  | 12,114,847.40 ns |  1,423,747.87 ns |  78,040.425 ns | 12,165,430.47 ns | 12,166.57 |         82.54 |     1.00 |    0.01 |      - |   22139 B |        1.00 |
| NpgsqlLargeObjectReadOneMegabyteAsync    | LargeObjectReadOneMegabyte  | 12,380,258.33 ns | 11,671,291.91 ns | 639,742.895 ns | 13,001,405.00 ns | 13,072.46 |         80.77 |     1.02 |    0.05 |      - |   23153 B |        1.05 |
|                                          |                             |                  |                  |                |                  |           |               |          |         |        |           |             |
| BlueTuskNotificationDeliveryAsync        | NotificationDelivery        |    528,777.02 ns |     48,421.51 ns |   2,654.147 ns |    531,103.28 ns |    531.30 |      1,891.16 |     1.00 |    0.01 |      - |    3488 B |        1.00 |
| NpgsqlNotificationDeliveryAsync          | NotificationDelivery        |    532,340.95 ns |     15,861.73 ns |     869.435 ns |    533,148.00 ns |    533.23 |      1,878.50 |     1.01 |    0.00 |      - |    1871 B |        0.54 |
|                                          |                             |                  |                  |                |                  |           |               |          |         |        |           |             |
| BlueTuskParameterizedScalarAsync         | ParameterizedScalar         |    424,985.04 ns |     48,961.08 ns |   2,683.722 ns |    427,630.75 ns |    427.99 |      2,353.02 |     1.00 |    0.01 |      - |    1567 B |        1.00 |
| NpgsqlParameterizedScalarAsync           | ParameterizedScalar         |    465,460.25 ns |    204,688.55 ns |  11,219.670 ns |    475,972.86 ns |    477.03 |      2,148.41 |     1.10 |    0.02 |      - |    2124 B |        1.36 |
|                                          |                             |                  |                  |                |                  |           |               |          |         |        |           |             |
| BlueTuskPoolCheckoutAsync                | PoolCheckout                |        199.80 ns |        129.02 ns |       7.072 ns |        206.77 ns |      0.21 |  5,004,994.09 |     1.00 |    0.04 | 0.0105 |     176 B |        1.00 |
| NpgsqlPoolCheckoutAsync                  | PoolCheckout                |        201.10 ns |         41.36 ns |       2.267 ns |        203.31 ns |      0.20 |  4,972,634.71 |     1.01 |    0.03 | 0.0110 |     184 B |        1.05 |
|                                          |                             |                  |                  |                |                  |           |               |          |         |        |           |             |
| BlueTuskPreparedScalarAsync              | PreparedScalar              |    462,599.04 ns |    171,394.20 ns |   9,394.695 ns |    471,626.49 ns |    472.61 |      2,161.70 |     1.00 |    0.02 |      - |     776 B |        1.00 |
| NpgsqlPreparedScalarAsync                | PreparedScalar              |    439,679.90 ns |     81,273.79 ns |   4,454.891 ns |    444,056.50 ns |    444.59 |      2,274.38 |     0.95 |    0.02 |      - |    1100 B |        1.42 |
|                                          |                             |                  |                  |                |                  |           |               |          |         |        |           |             |
| BlueTuskPreparedTypedRowRoundTripAsync   | PreparedTypedRowRoundTrip   |    449,451.82 ns |    311,817.71 ns |  17,091.781 ns |    466,023.20 ns |    467.91 |      2,224.93 |     1.00 |    0.05 |      - |    1848 B |        1.00 |
| NpgsqlPreparedTypedRowRoundTripAsync     | PreparedTypedRowRoundTrip   |    441,177.47 ns |     39,957.90 ns |   2,190.227 ns |    442,722.58 ns |    442.78 |      2,266.66 |     0.98 |    0.03 |      - |    1374 B |        0.74 |
|                                          |                             |                  |                  |                |                  |           |               |          |         |        |           |             |
| BlueTuskSequential1000RowsAsync          | Sequential1000Rows          |    608,111.95 ns |     25,875.23 ns |   1,418.308 ns |    609,505.63 ns |    609.68 |      1,644.43 |     1.00 |    0.00 |      - |    1278 B |        1.00 |
| NpgsqlSequential1000RowsAsync            | Sequential1000Rows          |    644,077.64 ns |    104,015.66 ns |   5,701.449 ns |    647,401.67 ns |    647.41 |      1,552.61 |     1.06 |    0.01 |      - |    1509 B |        1.18 |
|                                          |                             |                  |                  |                |                  |           |               |          |         |        |           |             |
| BlueTuskSequentialOneMegabyteByteaAsync  | SequentialOneMegabyteBytea  |  6,012,525.65 ns |  4,419,955.48 ns | 242,272.675 ns |  6,251,054.77 ns |  6,283.88 |        166.32 |     1.00 |    0.05 |      - |    1513 B |        1.00 |
| NpgsqlSequentialOneMegabyteByteaAsync    | SequentialOneMegabyteBytea  |  6,403,900.52 ns |  3,970,474.67 ns | 217,635.115 ns |  6,539,386.72 ns |  6,541.40 |        156.15 |     1.07 |    0.05 |      - |   15245 B |       10.08 |
