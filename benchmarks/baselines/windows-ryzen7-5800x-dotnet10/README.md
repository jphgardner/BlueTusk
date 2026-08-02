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

The transport-pipeline decision reports were added on 2026-08-01. The bounded `System.IO.Pipelines` prototype improves the adversarial fragmented async batch and tiny cancellation-drain cases, but is approximately 2x slower for a 1 MiB field, 42% slower for synchronous COPY, effectively tied for asynchronous COPY and TLS, and 76% slower for asynchronous raw TCP. Both warm loopback readers report zero measured managed allocation; the prototype reports 96 B for the large-field batch. These short-run measurements support retaining the current transport, as recorded in ADR 0005.

The live PostgreSQL 19 provider-comparison report was refreshed on 2026-08-02
against the local server on this machine after the command, pool, and streaming
hot-path work. BlueTusk/Npgsql means are 482/340 µs and 2,064/2,113 B for a
parameterized scalar, 452/302 µs and 992/1,132 B for an explicitly prepared
scalar, 199/210 ns and 168/184 B for an untouched warm pool checkout, 713/555 µs
and 5,519/1,600 B for a sequential 1,000-row read, and 13.97/10.44 ms and
12,610/8,983 B for a sequential 1 MiB `bytea` stream.

The current ShortRun therefore establishes measured BlueTusk wins for both
latency and managed allocation on untouched warm checkout: approximately 4.9%
faster and 8.7% smaller. Parameterized and prepared scalar commands allocate
approximately 2.3% and 12.4% less than Npgsql, respectively, while remaining
1.42x and 1.50x slower in this loopback run. The sequential row and `bytea`
paths remain 1.28x and 1.34x slower and allocate 3.45x and 1.40x as much.

Relative to the pre-optimization measurements from the same work session, the
untouched pool path is approximately 2,390x faster and allocates 39x less, while
the 1,000-row reader is approximately 21x faster and allocates 43x less. The
prepared scalar allocates 77% less, the parameterized scalar allocates 58% less,
and the 1 MiB stream is approximately 12% faster with 64% less allocation. These
three-iteration results are an optimization and regression baseline, not a
provider-wide superiority claim or release performance guarantee.

The live EF Core and SQL/PGQ application reports were added on 2026-08-02
against PostgreSQL 19 Beta 2. Fresh parameterized query compilation plus first
execution measured 2.94 ms and 132,048 B; materializing 100 no-tracking orders
measured 1.45 ms and 164,679 B. Normalized tracked inserts measured 1.51 ms and
27,462 B per operation, while load/track/update measured 2.09 ms and 37,665 B.
Traversing and consuming 999
edges measured 1.09 ms and 187,936 B through a prepared raw `GRAPH_TABLE`
command, and 2.98 ms and 685,864 B through the typed EF graph root. These
ShortRun values include caller-owned materialized results and retain the same
three-iteration limitations as the provider comparison.
