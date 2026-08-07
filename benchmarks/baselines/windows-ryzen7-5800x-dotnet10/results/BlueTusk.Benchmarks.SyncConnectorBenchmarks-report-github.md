```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host] : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3
LaunchCount=1  WarmupCount=3

```
| Method                         | Mean         | Error       | StdDev     | P95          | P99 (us) | Op/s         | Ratio  | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|------------------------------- |-------------:|------------:|-----------:|-------------:|---------:|-------------:|-------:|--------:|-------:|-------:|----------:|------------:|
| ConstructCoreTransactionBatch  |     58.70 ns |    11.37 ns |   0.623 ns |     59.30 ns |     0.06 | 17,034,478.6 |   1.00 |    0.01 | 0.0134 |      - |     224 B |        1.00 |
| EncodeNatsTransactionEnvelope  | 18,474.76 ns | 4,853.77 ns | 266.052 ns | 18,675.06 ns |    18.69 |     54,127.9 | 314.73 |    4.87 | 4.0283 | 0.4883 |   67624 B |      301.89 |
| ValidateOpenSearchDocument     |    648.60 ns |    50.14 ns |   2.749 ns |    650.29 ns |     0.65 |  1,541,793.5 |  11.05 |    0.11 | 0.0038 |      - |      72 B |        0.32 |
| CopyPostgreSqlParameterPayload |  1,148.26 ns |   474.64 ns |  26.016 ns |  1,173.92 ns |     1.18 |    870,886.3 |  19.56 |    0.42 | 1.9569 |      - |   32816 B |      146.50 |
