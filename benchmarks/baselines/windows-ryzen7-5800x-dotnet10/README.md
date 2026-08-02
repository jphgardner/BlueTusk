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

The checked-in GitHub Markdown reports are human-readable; the brief JSON reports support automated comparison. Most fixtures use a short job with only three measured iterations and are development references, not universal performance guarantees or substitutes for release-grade runs. The live provider comparison is the documented MediumRun exception.

The frontend-writer report was regenerated after removing interface-enumerator allocations from extended Bind messages. Both simple and extended writer workloads report zero managed allocation per operation.

The warm-session pool checkout workload uses an in-memory physical session to isolate pool arbitration and reset dispatch. Its initial 0.0.5 reference result is approximately 240 ns per checkout with zero managed allocation per operation.

The initial 0.0.6 core-codec reference results are approximately 8.5 ns for binary timestamp reads, 123 ns and 32 B for arbitrary-precision numeric reads, and 18 ns and 48 B for JSONB reads.

The 0.1.0 reader/streaming reports were added on 2026-07-31 with .NET SDK 10.0.302 on the same processor. The short reference run reads 1,000 buffered binary `int4` values in approximately 21.5 µs, a buffered 1 MiB `bytea` in 434 µs with the expected 1 MiB materialization, and a buffered 1 MiB text value in 324 µs with approximately 2 MiB allocated. Incrementally draining a 1 MiB backend payload takes approximately 13.2 µs and allocates 176 B, providing a baseline for the network-backed sequential reader path.

The 0.3.0 allocation-discipline reports were added on 2026-08-01 with the same SDK/runtime and processor. The in-memory full provider path allocates 1,048 B for a synchronous named binary `int4` parameter and scalar, 1,424 B for the text/string path, 2,560 B for a buffered reader over 100 typed `int4` values, and 1,352 B for the asynchronous scalar path. Warm simple and extended protocol-connection writes allocate 0 B after setup because their bounded session writer is reused. Run `pwsh -File eng/verify-allocation-budgets.ps1` after refreshing reports.

The transport-pipeline decision reports were added on 2026-08-01. The bounded `System.IO.Pipelines` prototype improves the adversarial fragmented async batch and tiny cancellation-drain cases, but is approximately 2x slower for a 1 MiB field, 42% slower for synchronous COPY, effectively tied for asynchronous COPY and TLS, and 76% slower for asynchronous raw TCP. Both warm loopback readers report zero measured managed allocation; the prototype reports 96 B for the large-field batch. These short-run measurements support retaining the current transport, as recorded in ADR 0005.

The live PostgreSQL 19 provider-comparison report was refreshed on 2026-08-02
against the local server on this machine after the command, pool, transport, and
streaming hot-path work. Unlike the short iteration runs used during profiling,
the checked-in report is a MediumRun with two launches, ten warmups, and fifteen
measured iterations. The large-field fixture creates the same one-row 1 MiB
temporary payload on each provider connection during setup, keeping PostgreSQL
payload-generation CPU outside the timed operations. BlueTusk/Npgsql means are
481/481 µs and 1,642/2,079 B for a parameterized scalar, 437/440 µs and
772/1,074 B for an explicitly prepared scalar, 303/327 ns and 168/184 B for an
untouched warm pool checkout, 767/710 µs and 1,558/1,496 B for a sequential
1,000-row read, and 4.660/4.665 ms and 4,288/8,829 B for a sequential 1 MiB
`bytea` stream.

The current MediumRun therefore records BlueTusk at parity or ahead on four of
five latency means and lower managed allocation on four of five pairs. Warm
checkout is the clear latency win; the scalar and large-stream confidence
intervals overlap and are treated as parity. The 1,000-row reader remains 8.0%
slower and allocates 4.1% more, but those gaps have narrowed from 17.7% and 146%
in the preceding checked-in ShortRun. These results are an optimization and
regression baseline, not a provider-wide superiority claim or release
performance guarantee.

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
