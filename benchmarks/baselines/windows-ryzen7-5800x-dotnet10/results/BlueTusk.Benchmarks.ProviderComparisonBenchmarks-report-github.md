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
| BlueTuskParameterizedScalarAsync        | ParameterizedScalar        |    499,283.1 ns |   233,500.2 ns |  12,798.93 ns |    511,803.6 ns |  1.00 |    0.03 |      - |    4392 B |        1.00 |
| NpgsqlParameterizedScalarAsync          | ParameterizedScalar        |    355,921.7 ns |    41,825.5 ns |   2,292.60 ns |    358,180.1 ns |  0.71 |    0.02 |      - |    2114 B |        0.48 |
|                                         |                            |                 |                |               |                 |       |         |        |           |             |
| BlueTuskPoolCheckoutAsync               | PoolCheckout               |        403.6 ns |       123.6 ns |       6.77 ns |        408.9 ns |  1.00 |    0.02 | 0.0176 |     296 B |        1.00 |
| NpgsqlPoolCheckoutAsync                 | PoolCheckout               |        262.4 ns |       302.6 ns |      16.58 ns |        275.7 ns |  0.65 |    0.04 | 0.0110 |     184 B |        0.62 |
|                                         |                            |                 |                |               |                 |       |         |        |           |             |
| BlueTuskPreparedScalarAsync             | PreparedScalar             |    451,030.1 ns |   194,814.8 ns |  10,678.46 ns |    461,537.3 ns |  1.00 |    0.03 |      - |    2696 B |        1.00 |
| NpgsqlPreparedScalarAsync               | PreparedScalar             |    319,408.1 ns |   253,740.5 ns |  13,908.37 ns |    332,037.9 ns |  0.71 |    0.03 |      - |    1111 B |        0.41 |
|                                         |                            |                 |                |               |                 |       |         |        |           |             |
| BlueTuskSequential1000RowsAsync         | Sequential1000Rows         |    727,794.2 ns |   408,200.9 ns |  22,374.87 ns |    748,200.5 ns |  1.00 |    0.04 |      - |    5212 B |        1.00 |
| NpgsqlSequential1000RowsAsync           | Sequential1000Rows         |    554,243.0 ns |   162,420.1 ns |   8,902.79 ns |    563,023.9 ns |  0.76 |    0.02 |      - |    1556 B |        0.30 |
|                                         |                            |                 |                |               |                 |       |         |        |           |             |
| BlueTuskSequentialOneMegabyteByteaAsync | SequentialOneMegabyteBytea | 14,938,824.0 ns | 2,487,315.5 ns | 136,338.16 ns | 15,073,091.1 ns |  1.00 |    0.01 |      - |   27320 B |        1.00 |
| NpgsqlSequentialOneMegabyteByteaAsync   | SequentialOneMegabyteBytea | 10,224,999.0 ns | 5,343,786.4 ns | 292,910.97 ns | 10,513,963.3 ns |  0.68 |    0.02 |      - |    8837 B |        0.32 |
