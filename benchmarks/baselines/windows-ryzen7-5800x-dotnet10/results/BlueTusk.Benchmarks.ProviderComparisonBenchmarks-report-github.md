```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1
WarmupCount=3

```
| Method                                  | Categories                 | Mean            | Error          | StdDev        | P95             | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------------------------------------- |--------------------------- |----------------:|---------------:|--------------:|----------------:|------:|--------:|-------:|----------:|------------:|
| BlueTuskParameterizedScalarAsync        | ParameterizedScalar        |    474,189.0 ns |   146,055.6 ns |   8,005.80 ns |    479,475.0 ns |  1.00 |    0.02 |      - |    4912 B |        1.00 |
| NpgsqlParameterizedScalarAsync          | ParameterizedScalar        |    342,621.9 ns |   167,983.9 ns |   9,207.76 ns |    350,276.9 ns |  0.72 |    0.02 |      - |    2141 B |        0.44 |
|                                         |                            |                 |                |               |                 |       |         |        |           |             |
| BlueTuskPoolCheckoutAsync               | PoolCheckout               |    460,423.8 ns |   127,783.8 ns |   7,004.26 ns |    467,307.7 ns | 1.000 |    0.02 |      - |    6553 B |        1.00 |
| NpgsqlPoolCheckoutAsync                 | PoolCheckout               |        240.7 ns |       153.6 ns |       8.42 ns |        246.6 ns | 0.001 |    0.00 | 0.0110 |     184 B |        0.03 |
|                                         |                            |                 |                |               |                 |       |         |        |           |             |
| BlueTuskPreparedScalarAsync             | PreparedScalar             |    448,816.4 ns |   159,793.0 ns |   8,758.79 ns |    456,912.6 ns |  1.00 |    0.02 |      - |    4312 B |        1.00 |
| NpgsqlPreparedScalarAsync               | PreparedScalar             |    299,601.7 ns |   115,521.3 ns |   6,332.11 ns |    305,638.9 ns |  0.67 |    0.02 |      - |    1147 B |        0.27 |
|                                         |                            |                 |                |               |                 |       |         |        |           |             |
| BlueTuskSequential1000RowsAsync         | Sequential1000Rows         | 14,563,772.9 ns | 4,067,921.7 ns | 222,976.52 ns | 14,783,362.5 ns |  1.00 |    0.02 |      - |  235704 B |       1.000 |
| NpgsqlSequential1000RowsAsync           | Sequential1000Rows         |    519,152.7 ns |    74,640.3 ns |   4,091.29 ns |    522,055.0 ns |  0.04 |    0.00 |      - |    1558 B |       0.007 |
|                                         |                            |                 |                |               |                 |       |         |        |           |             |
| BlueTuskSequentialOneMegabyteByteaAsync | SequentialOneMegabyteBytea | 14,684,578.6 ns | 7,546,363.2 ns | 413,641.63 ns | 14,955,229.2 ns |  1.00 |    0.03 |      - |   33279 B |        1.00 |
| NpgsqlSequentialOneMegabyteByteaAsync   | SequentialOneMegabyteBytea |  9,631,126.0 ns | 6,526,302.3 ns | 357,728.65 ns |  9,983,769.1 ns |  0.66 |    0.03 |      - |    8857 B |        0.27 |
