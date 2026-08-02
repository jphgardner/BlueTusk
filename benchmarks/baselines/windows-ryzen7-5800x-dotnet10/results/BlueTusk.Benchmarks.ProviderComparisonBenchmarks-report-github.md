```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                  | Categories                 | Mean            | Error           | StdDev        | P95             | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------------------------------------- |--------------------------- |----------------:|----------------:|--------------:|----------------:|------:|--------:|-------:|----------:|------------:|
| BlueTuskParameterizedScalarAsync        | ParameterizedScalar        |    482,378.3 ns |   262,793.39 ns |  14,404.59 ns |    496,382.8 ns |  1.00 |    0.04 |      - |    2064 B |        1.00 |
| NpgsqlParameterizedScalarAsync          | ParameterizedScalar        |    340,448.0 ns |   112,898.45 ns |   6,188.35 ns |    345,517.8 ns |  0.71 |    0.02 |      - |    2113 B |        1.02 |
|                                         |                            |                 |                 |               |                 |       |         |        |           |             |
| BlueTuskPoolCheckoutAsync               | PoolCheckout               |        199.4 ns |        76.74 ns |       4.21 ns |        203.6 ns |  1.00 |    0.03 | 0.0100 |     168 B |        1.00 |
| NpgsqlPoolCheckoutAsync                 | PoolCheckout               |        209.6 ns |       182.50 ns |      10.00 ns |        219.1 ns |  1.05 |    0.05 | 0.0110 |     184 B |        1.10 |
|                                         |                            |                 |                 |               |                 |       |         |        |           |             |
| BlueTuskPreparedScalarAsync             | PreparedScalar             |    451,998.0 ns |   231,715.87 ns |  12,701.13 ns |    462,645.3 ns |  1.00 |    0.03 |      - |     992 B |        1.00 |
| NpgsqlPreparedScalarAsync               | PreparedScalar             |    302,133.4 ns |    10,347.03 ns |     567.16 ns |    302,678.6 ns |  0.67 |    0.02 |      - |    1132 B |        1.14 |
|                                         |                            |                 |                 |               |                 |       |         |        |           |             |
| BlueTuskSequential1000RowsAsync         | Sequential1000Rows         |    712,902.9 ns |    63,303.75 ns |   3,469.89 ns |    716,238.1 ns |  1.00 |    0.01 |      - |    5519 B |        1.00 |
| NpgsqlSequential1000RowsAsync           | Sequential1000Rows         |    555,466.1 ns |   184,852.06 ns |  10,132.37 ns |    561,860.7 ns |  0.78 |    0.01 |      - |    1600 B |        0.29 |
|                                         |                            |                 |                 |               |                 |       |         |        |           |             |
| BlueTuskSequentialOneMegabyteByteaAsync | SequentialOneMegabyteBytea | 13,973,687.5 ns | 2,680,317.28 ns | 146,917.23 ns | 14,109,141.2 ns |  1.00 |    0.01 |      - |   12610 B |        1.00 |
| NpgsqlSequentialOneMegabyteByteaAsync   | SequentialOneMegabyteBytea | 10,439,138.8 ns | 6,855,936.84 ns | 375,797.03 ns | 10,749,549.4 ns |  0.75 |    0.02 |      - |    8983 B |        0.71 |
