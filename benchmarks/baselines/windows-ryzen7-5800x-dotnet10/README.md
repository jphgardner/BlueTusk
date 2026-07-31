# Reference baseline — Windows / Ryzen 7 5800X / .NET 10

Captured on 2026-07-21 with:

- BenchmarkDotNet 0.15.8
- Windows 11 10.0.26200.8894
- AMD Ryzen 7 5800X, 8 physical / 16 logical cores
- .NET SDK 10.0.110 and .NET runtime 10.0.10
- x64 RyuJIT with workstation concurrent GC

Command:

```powershell
$env:BLUETUSK_BENCHMARK_ARTIFACTS = "benchmarks/baselines/windows-ryzen7-5800x-dotnet10"
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release -- --job short --filter '*'
```

The checked-in GitHub Markdown reports are human-readable; the brief JSON reports support automated comparison. A short job has only three measured iterations and is a development reference, not a universal performance guarantee or a substitute for release-grade runs.

The frontend-writer report was regenerated after removing interface-enumerator allocations from extended Bind messages. Both simple and extended writer workloads report zero managed allocation per operation.

The warm-session pool checkout workload uses an in-memory physical session to isolate pool arbitration and reset dispatch. Its initial 0.0.5 reference result is approximately 240 ns per checkout with zero managed allocation per operation.

The initial 0.0.6 core-codec reference results are approximately 8.5 ns for binary timestamp reads, 123 ns and 32 B for arbitrary-precision numeric reads, and 18 ns and 48 B for JSONB reads.

The 0.1.0 reader/streaming reports were added on 2026-07-31 with .NET SDK 10.0.302 on the same processor. The short reference run reads 1,000 buffered binary `int4` values in approximately 21.5 µs, a buffered 1 MiB `bytea` in 434 µs with the expected 1 MiB materialization, and a buffered 1 MiB text value in 324 µs with approximately 2 MiB allocated. Incrementally draining a 1 MiB backend payload takes approximately 13.2 µs and allocates 176 B, providing a baseline for the network-backed sequential reader path.

The 0.3.0 allocation-discipline reports were added on 2026-08-01 with the same SDK/runtime and processor. The in-memory full provider path allocates 1,048 B for a synchronous named binary `int4` parameter and scalar, 1,424 B for the text/string path, 2,560 B for a buffered reader over 100 typed `int4` values, and 1,352 B for the asynchronous scalar path. Warm simple and extended protocol-connection writes allocate 0 B after setup because their bounded session writer is reused. Run `pwsh -File eng/verify-allocation-budgets.ps1` after refreshing reports.
