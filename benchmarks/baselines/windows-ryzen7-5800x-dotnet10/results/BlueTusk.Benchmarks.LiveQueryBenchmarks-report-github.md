``` ini

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1
WarmupCount=3

```
| Method                                                     | Mean        | P95         | Gen0    | Gen1   | Allocated |
|----------------------------------------------------------- |------------:|------------:|--------:|-------:|----------:|
| DiffOneUpdatedRowInOneThousand                             | 76,410.2 ns | 84,989.2 ns | 13.1836 | 2.3193 |  221872 B |
| SerializeOneUpdatedRow                                     |    881.1 ns |    919.3 ns |  0.0496 |      - |     832 B |
| CoalesceOneHundredInvalidationsAndFanOut64SubscribersAsync | 92,327.0 ns | 94,711.3 ns | 10.2539 | 2.4414 |  175060 B |
