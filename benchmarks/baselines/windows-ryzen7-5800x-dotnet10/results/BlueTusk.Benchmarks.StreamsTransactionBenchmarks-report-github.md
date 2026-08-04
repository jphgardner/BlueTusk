```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host] : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3
LaunchCount=1  WarmupCount=3

```
| Method                                   | Mean            | Error           | StdDev          | P95             | P99 (us)  | Op/s         | Gen0     | Gen1     | Gen2     | Allocated  |
|----------------------------------------- |----------------:|----------------:|----------------:|----------------:|----------:|-------------:|---------:|---------:|---------:|-----------:|
| AssembleAndMaterializeOneThousandInserts |        703.5 ns |        270.1 ns |        14.80 ns |        717.9 ns |      0.72 | 1,421,384.26 |   0.0508 |   0.0313 |        - |      853 B |
| SpillAndStreamFourMiBTransaction         | 46,752,119.4 ns | 27,777,249.8 ns | 1,522,564.80 ns | 47,639,050.8 ns | 47,640.63 |        21.39 | 583.3333 | 583.3333 | 583.3333 | 12731471 B |
