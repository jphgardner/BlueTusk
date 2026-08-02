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
| BlueTuskParameterizedScalarAsync        | ParameterizedScalar        |    474,330.6 ns |   232,692.53 ns |  12,754.66 ns |    486,798.8 ns |  1.00 |    0.03 |      - |    2064 B |        1.00 |
| NpgsqlParameterizedScalarAsync          | ParameterizedScalar        |    347,500.3 ns |   114,539.11 ns |   6,278.28 ns |    353,694.2 ns |  0.73 |    0.02 |      - |    2109 B |        1.02 |
|                                         |                            |                 |                 |               |                 |       |         |        |           |             |
| BlueTuskPoolCheckoutAsync               | PoolCheckout               |        199.1 ns |        98.42 ns |       5.39 ns |        204.4 ns |  1.00 |    0.03 | 0.0100 |     168 B |        1.00 |
| NpgsqlPoolCheckoutAsync                 | PoolCheckout               |        214.6 ns |        31.45 ns |       1.72 ns |        216.2 ns |  1.08 |    0.03 | 0.0110 |     184 B |        1.10 |
|                                         |                            |                 |                 |               |                 |       |         |        |           |             |
| BlueTuskPreparedScalarAsync             | PreparedScalar             |    449,103.6 ns |   286,854.96 ns |  15,723.49 ns |    463,105.5 ns |  1.00 |    0.04 |      - |     992 B |        1.00 |
| NpgsqlPreparedScalarAsync               | PreparedScalar             |    303,544.8 ns |    82,907.70 ns |   4,544.45 ns |    307,940.8 ns |  0.68 |    0.02 |      - |    1118 B |        1.13 |
|                                         |                            |                 |                 |               |                 |       |         |        |           |             |
| BlueTuskSequential1000RowsAsync         | Sequential1000Rows         |    742,192.0 ns |   282,679.59 ns |  15,494.62 ns |    757,234.1 ns |  1.00 |    0.03 |      - |    5519 B |        1.00 |
| NpgsqlSequential1000RowsAsync           | Sequential1000Rows         |    528,203.0 ns |    22,630.96 ns |   1,240.48 ns |    529,122.1 ns |  0.71 |    0.01 |      - |    1611 B |        0.29 |
|                                         |                            |                 |                 |               |                 |       |         |        |           |             |
| BlueTuskSequentialOneMegabyteByteaAsync | SequentialOneMegabyteBytea | 15,084,920.8 ns | 6,808,765.94 ns | 373,211.44 ns | 15,451,563.8 ns |  1.00 |    0.03 |      - |   24977 B |        1.00 |
| NpgsqlSequentialOneMegabyteByteaAsync   | SequentialOneMegabyteBytea | 10,410,421.4 ns | 6,782,239.01 ns | 371,757.41 ns | 10,722,565.5 ns |  0.69 |    0.03 |      - |    8941 B |        0.36 |
