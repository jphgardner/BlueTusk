```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                     | Mean      | Error     | StdDev   | Ratio | RatioSD | Gen0     | Gen1     | Gen2     | Allocated  | Alloc Ratio |
|--------------------------- |----------:|----------:|---------:|------:|--------:|---------:|---------:|---------:|-----------:|------------:|
| ReadThousandTypedInt32Rows |  21.49 μs |  19.36 μs | 1.061 μs |  1.00 |    0.06 |   1.4343 |        - |        - |   23.55 KB |        1.00 |
| ReadOneMegabyteByteaStream | 433.73 μs |  75.15 μs | 4.119 μs | 20.21 |    0.88 | 248.5352 | 248.5352 | 248.5352 | 1024.53 KB |       43.50 |
| ReadOneMegabyteTextReader  | 323.71 μs | 165.09 μs | 9.049 μs | 15.09 |    0.74 | 332.5195 | 332.5195 | 332.5195 | 2048.29 KB |       86.96 |
