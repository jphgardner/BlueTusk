```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                  | Categories                 | Mean           | Error           | StdDev        | P95            | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------------------------------------- |--------------------------- |---------------:|----------------:|--------------:|---------------:|------:|--------:|-------:|----------:|------------:|
| BlueTuskParameterizedScalarAsync        | ParameterizedScalar        |   476,935.6 ns |   160,315.59 ns |   8,787.44 ns |   484,854.5 ns |  1.00 |    0.02 |      - |    2064 B |        1.00 |
| NpgsqlParameterizedScalarAsync          | ParameterizedScalar        |   498,507.9 ns |   125,337.65 ns |   6,870.18 ns |   505,256.2 ns |  1.05 |    0.02 |      - |    2067 B |        1.00 |
|                                         |                            |                |                 |               |                |       |         |        |           |             |
| BlueTuskPoolCheckoutAsync               | PoolCheckout               |       301.6 ns |       224.77 ns |      12.32 ns |       312.3 ns |  1.00 |    0.05 | 0.0100 |     168 B |        1.00 |
| NpgsqlPoolCheckoutAsync                 | PoolCheckout               |       326.2 ns |        15.91 ns |       0.87 ns |       327.0 ns |  1.08 |    0.04 | 0.0110 |     184 B |        1.10 |
|                                         |                            |                |                 |               |                |       |         |        |           |             |
| BlueTuskPreparedScalarAsync             | PreparedScalar             |   443,197.1 ns |   179,867.98 ns |   9,859.17 ns |   452,442.6 ns |  1.00 |    0.03 |      - |     992 B |        1.00 |
| NpgsqlPreparedScalarAsync               | PreparedScalar             |   427,858.4 ns |   221,844.69 ns |  12,160.06 ns |   439,196.0 ns |  0.97 |    0.03 |      - |    1065 B |        1.07 |
|                                         |                            |                |                 |               |                |       |         |        |           |             |
| BlueTuskSequential1000RowsAsync         | Sequential1000Rows         |   856,660.2 ns | 1,603,840.65 ns |  87,911.92 ns |   934,881.9 ns |  1.01 |    0.13 |      - |    3701 B |        1.00 |
| NpgsqlSequential1000RowsAsync           | Sequential1000Rows         |   727,846.9 ns |    45,234.60 ns |   2,479.46 ns |   730,095.1 ns |  0.86 |    0.08 |      - |    1505 B |        0.41 |
|                                         |                            |                |                 |               |                |       |         |        |           |             |
| BlueTuskSequentialOneMegabyteByteaAsync | SequentialOneMegabyteBytea | 4,658,900.3 ns | 4,879,038.35 ns | 267,436.56 ns | 4,922,734.4 ns |  1.00 |    0.07 |      - |    6041 B |        1.00 |
| NpgsqlSequentialOneMegabyteByteaAsync   | SequentialOneMegabyteBytea | 4,817,868.2 ns | 4,177,579.66 ns | 228,987.24 ns | 4,971,873.7 ns |  1.04 |    0.07 |      - |    8782 B |        1.45 |
