```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host] : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3
LaunchCount=1  WarmupCount=3

```
| Method                                    | Mean       | Error       | StdDev    | P95        | P99 (us) | Op/s        | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------------------ |-----------:|------------:|----------:|-----------:|---------:|------------:|------:|--------:|-------:|----------:|------------:|
| ExecuteInt32ParameterAndScalar            |   190.7 ns |    10.85 ns |   0.59 ns |   191.2 ns |     0.19 | 5,243,940.1 |  1.00 |    0.00 | 0.0110 |     184 B |        1.00 |
| ExecuteTextParameterAndScalar             |   291.4 ns |    46.23 ns |   2.53 ns |   293.8 ns |     0.29 | 3,432,106.4 |  1.53 |    0.01 | 0.0353 |     592 B |        3.22 |
| ExecuteReaderAndReadOneHundredInt32Values | 2,166.3 ns | 3,543.44 ns | 194.23 ns | 2,357.0 ns |     2.38 |   461,618.8 | 11.36 |    0.88 | 0.0229 |     384 B |        2.09 |
| ExecuteInt32ParameterAndScalarAsync       |   293.6 ns |    38.91 ns |   2.13 ns |   295.7 ns |     0.30 | 3,406,482.5 |  1.54 |    0.01 | 0.0057 |      96 B |        0.52 |
| ClassifySessionNeutralSql                 |   616.3 ns |   778.59 ns |  42.68 ns |   648.9 ns |     0.65 | 1,622,567.6 |  3.23 |    0.19 |      - |         - |        0.00 |
