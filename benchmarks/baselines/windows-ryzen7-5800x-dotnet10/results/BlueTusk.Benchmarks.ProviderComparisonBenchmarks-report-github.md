```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]    : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  MediumRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=MediumRun  IterationCount=15  LaunchCount=2
WarmupCount=10

```
| Method                                  | Categories                 | Mean           | Error        | StdDev       | P95            | P99 (us) | Op/s        | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------------------------------------- |--------------------------- |---------------:|-------------:|-------------:|---------------:|---------:|------------:|------:|--------:|-------:|----------:|------------:|
| BlueTuskParameterizedScalarAsync        | ParameterizedScalar        |   365,124.9 ns |  4,048.36 ns |  6,059.39 ns |   372,942.9 ns |   375.72 |     2,738.8 |  1.00 |    0.02 |      - |    1634 B |        1.00 |
| NpgsqlParameterizedScalarAsync          | ParameterizedScalar        |   391,667.3 ns |  4,871.24 ns |  7,291.04 ns |   403,623.3 ns |   405.15 |     2,553.2 |  1.07 |    0.03 |      - |    2079 B |        1.27 |
|                                         |                            |                |              |              |                |          |             |       |         |        |           |             |
| BlueTuskPoolCheckoutAsync               | PoolCheckout               |       195.1 ns |      1.06 ns |      1.55 ns |       197.3 ns |     0.20 | 5,124,519.4 |  1.00 |    0.01 | 0.0100 |     168 B |        1.00 |
| NpgsqlPoolCheckoutAsync                 | PoolCheckout               |       209.2 ns |      6.39 ns |      8.95 ns |       217.1 ns |     0.24 | 4,781,069.1 |  1.07 |    0.05 | 0.0110 |     184 B |        1.10 |
|                                         |                            |                |              |              |                |          |             |       |         |        |           |             |
| BlueTuskPreparedScalarAsync             | PreparedScalar             |   356,479.7 ns |  3,385.09 ns |  5,066.65 ns |   364,619.2 ns |   365.35 |     2,805.2 |  1.00 |    0.02 |      - |     773 B |        1.00 |
| NpgsqlPreparedScalarAsync               | PreparedScalar             |   358,180.4 ns |  3,881.79 ns |  5,441.72 ns |   365,110.2 ns |   369.82 |     2,791.9 |  1.00 |    0.02 |      - |    1137 B |        1.47 |
|                                         |                            |                |              |              |                |          |             |       |         |        |           |             |
| BlueTuskSequential1000RowsAsync         | Sequential1000Rows         |   528,727.9 ns |  3,582.37 ns |  5,361.93 ns |   537,738.7 ns |   540.57 |     1,891.3 |  1.00 |    0.01 |      - |    1413 B |        1.00 |
| NpgsqlSequential1000RowsAsync           | Sequential1000Rows         |   549,321.7 ns |  2,954.87 ns |  4,142.32 ns |   555,198.6 ns |   559.15 |     1,820.4 |  1.04 |    0.01 |      - |    1418 B |        1.00 |
|                                         |                            |                |              |              |                |          |             |       |         |        |           |             |
| BlueTuskSequentialOneMegabyteByteaAsync | SequentialOneMegabyteBytea | 2,279,163.8 ns | 25,850.23 ns | 35,384.09 ns | 2,328,158.9 ns | 2,371.12 |       438.8 |  1.00 |    0.02 |      - |    3832 B |        1.00 |
| NpgsqlSequentialOneMegabyteByteaAsync   | SequentialOneMegabyteBytea | 2,305,461.1 ns | 24,330.83 ns | 34,108.43 ns | 2,354,583.7 ns | 2,383.77 |       433.8 |  1.01 |    0.02 |      - |    8426 B |        2.20 |
