```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host] : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3
LaunchCount=1  WarmupCount=3

```
| Method                                                     | Mean        | Error       | StdDev      | P95         | P99 (us) | Op/s        | Gen0    | Gen1   | Allocated |
|----------------------------------------------------------- |------------:|------------:|------------:|------------:|---------:|------------:|--------:|-------:|----------:|
| DiffOneUpdatedRowInOneThousand                             | 41,382.6 ns | 37,181.3 ns | 2,038.03 ns | 43,385.9 ns |    43.67 |    24,164.7 |  2.0142 | 0.2441 |   34584 B |
| DiffUnchangedOneThousandRows                               | 45,330.8 ns |  2,328.3 ns |   127.62 ns | 45,456.4 ns |    45.47 |    22,060.0 |  2.0142 | 0.0610 |   34392 B |
| SerializeOneUpdatedRow                                     |    748.6 ns |    400.0 ns |    21.92 ns |    769.6 ns |     0.77 | 1,335,914.8 |  0.0372 |      - |     632 B |
| CoalesceOneHundredInvalidationsAndFanOut64SubscribersAsync | 95,766.6 ns | 62,684.7 ns | 3,435.96 ns | 99,153.1 ns |    99.61 |    10,442.1 | 10.0098 | 2.3193 |  168237 B |
