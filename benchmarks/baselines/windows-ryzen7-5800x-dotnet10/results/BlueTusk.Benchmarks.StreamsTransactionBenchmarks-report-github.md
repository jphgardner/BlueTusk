```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                   | Mean            | Error           | StdDev        | P95             | Gen0     | Gen1     | Gen2     | Allocated  |
|----------------------------------------- |----------------:|----------------:|--------------:|----------------:|---------:|---------:|---------:|-----------:|
| AssembleAndMaterializeOneThousandInserts |        422.0 ns |        153.3 ns |       8.40 ns |        428.4 ns |   0.0508 |   0.0322 |        - |      852 B |
| SpillAndStreamFourMiBTransaction         | 38,282,289.7 ns | 14,713,193.7 ns | 806,479.80 ns | 39,047,107.7 ns | 923.0769 | 923.0769 | 923.0769 | 12731918 B |
