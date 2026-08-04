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
446/487 µs and 1,663/2,094 B for a parameterized scalar, 436/445 µs and
796/1,099 B for an explicitly prepared scalar, 288/326 ns and 168/184 B for an
untouched warm pool checkout, 672/743 µs and 1,400/1,529 B for a sequential
1,000-row read, and 4.390/4.482 ms and 3,900/8,938 B for a sequential 1 MiB
`bytea` stream.

The current MediumRun therefore records lower BlueTusk mean latency and managed
allocation on all five pairs. Parameterized scalar execution, warm checkout, and
the 1,000-row reader have non-overlapping latency intervals; prepared scalar and
large-stream intervals overlap and are treated as parity despite lower BlueTusk
means. These results are an optimization and regression baseline, not a
provider-wide superiority claim or release performance guarantee.

The V1 multiplexing MediumRun was captured from commit
`9ba2c50c05c9d995b3b78cf65f6d88ee207e835f` on 2026-08-04 on the
same processor and a PostgreSQL 18 loopback server. Both providers use four
physical lanes, 64 concurrent parameterized scalar commands, no command
timeout, and one logical command per operation. End-to-end BlueTusk/Npgsql
results are 19.83/20.57 µs mean, 20.93/22.26 µs P95, 21.06/22.51 µs P99,
and 1,733/1,738 B per command. Reusing the 64 command objects records
17.41/20.01 µs mean, 19.34/21.53 µs P95, 20.04/21.69 µs P99, and
1,143/794 B. BlueTusk therefore has lower mean and tail latency in both
pairs and slightly lower end-to-end allocation, while Npgsql retains the
allocation advantage when command construction is excluded.

The checked-in full JSON contains 25–30 measured samples per workload and
passes the machine-readable mean, P95, P99, throughput-derived, and allocation
budgets. The adjacent evidence manifest binds the report hash to the source
commit, runtime, operating system, and PostgreSQL image digest. This is a
named-environment regression baseline, not a universal provider superiority
claim.

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

The initial Streams kernel report was added on 2026-08-03. Assembling and
materialising a 1,000-insert transaction measured 422 ns and 852 B per change.
Integrity-checking, durably flushing, streaming, and deleting a 4 MiB spooled
transaction measured 38.3 ms and 12.1 MiB allocated. Replacing bit-at-a-time
CRC and redundant protection/deserialization copies reduced the spool workload
from the profiling baseline of 110.2 ms and 32.1 MiB by 65% and 62%,
respectively. These are local ShortRun figures; release budgets will use longer
runs and representative storage devices.

The initial Live load report was added on 2026-08-03. A keyed diff over 1,000
rows with one update measured 76.4 µs and 221,872 B, including the new immutable
snapshot and lookup dictionaries. Serializing and integrity-protecting the
single update measured 881 ns and 832 B. Creating a shared subscription,
coalescing 100 relevant invalidations into one authoritative query, persisting
the diff, and delivering it through bounded channels to 64 subscribers measured
92.3 µs and 175,060 B. These ShortRun values are local regression evidence, not
network end-to-end latency claims.

The initial Continuous Graph report was added on 2026-08-03 against PostgreSQL
19 Beta 2. Capability probing, graph metadata/dependency validation, EF
translation, fingerprinting, and plan construction measured 988 µs and
103,446 B. Authoritatively materialising 999 graph paths measured 2.827 ms and
666,055 B. Processing one affected invalidation through the same PostgreSQL
requery and a keyed immutable Live diff measured 4.225 ms and 888,159 B. The
invalidation source is constant-time and in-memory, while the graph query and
provider work are live. These are three-iteration ShortRun regression values,
not production service-level objectives.
