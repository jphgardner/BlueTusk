# BlueTusk benchmarks

Every public `[Benchmark]` method must have a non-empty checked-in result.
`eng/verify-benchmark-coverage.ps1` currently binds 98 measured results to all
methods across 22 fixtures and rejects empty, stale, duplicate or statistically
invalid reports. `eng/verify-allocation-budgets.ps1` enforces 46 managed
allocation budgets, while `eng/verify-latency-budgets.ps1` enforces 19
production-critical latency/P95 budgets on the named Windows/Ryzen 7
5800X/.NET 10 reference environment. These are regression controls, not
application SLOs.

The BenchmarkDotNet suite covers complete synchronous/asynchronous command parameter and result paths, protocol-connection writes, protocol parsing and incremental payload streaming, typed buffered readers and large field access, integer/numeric/temporal/JSONB codecs, catalogue-composed array/enum/range/composite encoding and decoding, binary COPY field encoding, notification decoding, replication WAL-frame decoding and bounded pull consumption, large-object stream transfer overhead, warm-session pool checkout, EF query compilation/materialisation/writes, Live query diff/replay/fan-out, SQL/PGQ traversal, and the transport-pipeline decision. Pool, command, reader, protocol-streaming, replication, Live, and large-object workloads isolate provider bookkeeping with in-memory sessions; the application and comparison fixtures execute against live PostgreSQL.

`TransportPipelineBenchmarks` compares the production ArrayPool/Span/Memory reader with a benchmark-only, bounded `System.IO.Pipelines` prototype across fragmented rows, a 1 MiB field, COPY frames, and cancellation recovery, using genuine sync and async entry points. `TransportPipelineSocketBenchmarks` repeats the comparison over raw TCP and authenticated loopback TLS. The production packages do not reference `System.IO.Pipelines`; the resulting decision and limitations are in [ADR 0005](../docs/architecture/decisions/0005-postgresql-pipeline-mode-and-transport-pipelines.md).

`StreamsTransactionBenchmarks` measures the application CDC boundary separately from wire decoding. It reports per-change cost for assembling and materialising a 1,000-insert transaction, and the end-to-end cost/allocation of spilling and streaming a 4 MiB transaction through the integrity-checked disk spool. The latter includes durable file flush and cleanup by design.

The 2026-08-07 hardening run records 349.544 ns, P95 350.878 ns and
413 B per materialised change. The final out-of-process MediumRun records
24.044 ms mean, 26.738 ms P95, 28.587 ms P99 and 142,444 B for the complete
4 MiB spill/stream/cleanup operation, with zero Gen0, Gen1 or Gen2 collections.
Large pass-through records are read directly from read-only memory-mapped spool
storage; the mapping owns its file handle independently, so acknowledgement can
atomically remove the spool name while already-materialised values remain valid.

`SyncConnectorBenchmarks` records ownership/allocation baselines for the core
transaction batch and NATS, OpenSearch, and PostgreSQL connector payload paths
before connector ownership is changed.

`NativeCapabilityBenchmarks` covers binary COPY field encoding,
NotificationResponse decoding and a warm large-object chunk read. The same
reference run records 53.363 ns/88 B, 100.760 ns/136 B and 113.814 ns/0 B
respectively.

`PullOneThousandBoundedXLogFrames` consumes one already-owned frame at a time
without a prefetch queue. This mirrors the replication async iterator's
backpressure boundary: the connection reads the next `CopyData` payload only
when the consumer requests the next message. The benchmark normalizes time and
allocation per frame; it does not claim to measure server or network latency.

## Live application workloads

`LiveQueryBenchmarks` measures the bounded real-time application path without
network or database variance. It compares a 1,000-row keyed result after one
row changes, serializes and integrity-protects the resulting replay event, and
runs a complete shared-subscription lifecycle that coalesces 100 relevant
invalidations into one authoritative refresh and fans the update out through
bounded channels to 64 subscribers.

`ContinuousGraphBenchmarks` uses a 1,000-vertex/999-edge PostgreSQL 19 property
graph to measure trusted plan compilation, authoritative `GRAPH_TABLE` requery,
and the complete affected-invalidation path through authoritative requery plus
keyed Live diff. Its constant-time synthetic invalidation log isolates the
application path without accumulating benchmark-history entries; PostgreSQL
query and provider costs remain included.

The 2026-08-03 reference ShortRun records 988 µs/103,446 B for registration,
2.827 ms/666,055 B for authoritative 999-row requery, and
4.225 ms/888,159 B for affected requery plus keyed diff. These are local
regression measurements with three iterations, not production latency claims.

```powershell
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release -- --job short --filter '*LiveQueryBenchmarks*' --exporters json
```

Run the Continuous Graph workload against PostgreSQL 19:

```powershell
$env:BLUETUSK_BENCHMARK_CONNECTION_STRING = "Host=localhost;Port=5419;Database=bluetusk_tests;Username=postgres;Password=postgres;SSL Mode=Disable;Channel Binding=Disable"
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release -- --job short --filter '*ContinuousGraphBenchmarks*' --exporters json
```

`EntityFrameworkCoreBenchmarks` uses one long-lived `BlueTuskDataSource` and a
1,000-row table to measure fresh parameterized EF query compilation plus first
execution, no-tracking materialization of 100 entities, inserts, and
load/track/update operations. The
write methods perform 16 `SaveChanges` operations inside a rolled-back
transaction and declare `OperationsPerInvoke=16`, keeping the fixture stable
while reporting per-operation cost.

`SqlPgqBenchmarks` requires PostgreSQL 19. It creates a 1,000-vertex,
999-edge property graph and traverses all outgoing edges from one source through
both a prepared raw `GRAPH_TABLE` command and BlueTusk's typed EF graph query
root. Both paths fully consume or materialize the results.

Run the six application workloads against an isolated PostgreSQL 19 database:

```powershell
$env:BLUETUSK_BENCHMARK_CONNECTION_STRING = "Host=localhost;Port=5419;Database=bluetusk_tests;Username=postgres;Password=postgres;SSL Mode=Disable;Channel Binding=Disable"
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release -- --job short --filter '*EntityFrameworkCoreBenchmarks*' '*SqlPgqBenchmarks*' '*ContinuousGraphBenchmarks*'
```

These live fixtures use fixed `bluetusk_benchmark_*` object names and recreate
their own tables/graph, so the configured database must be dedicated to the
benchmark run.

## Live provider comparison

`ProviderComparisonBenchmarks` runs equivalent BlueTusk and Npgsql operations
against the same PostgreSQL server. The benchmark-only Npgsql package reference
does not flow into any BlueTusk runtime package. PostgreSQL remains the
correctness authority; Npgsql is used only as a mature-provider performance
reference.

The paired workloads cover asynchronous warm-pool checkout, parameterized and
explicitly prepared scalar commands, a sequential 1,000-row read, and a
sequential 1 MiB `bytea` stream. Each pair uses identical SQL and connection
lifetimes, long-lived data sources and physical connections, and reusable stream
buffers. Each connection builds its one-row 1 MiB temporary payload during global
setup, so the timed stream pair measures query, wire, and provider work without
repeatedly charging either provider for PostgreSQL's payload-generation CPU cost.
The broader BlueTusk-only suite continues to cover the remaining type,
batch, pipeline, COPY, concurrency, EF, graph, and replication workloads from
the performance strategy.

Set a dedicated live connection string and run the comparison explicitly:

```powershell
$env:BLUETUSK_BENCHMARK_CONNECTION_STRING = "Host=localhost;Port=5419;Database=bluetusk_tests;Username=postgres;Password=postgres;SSL Mode=Disable;Channel Binding=Disable"
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release -- --job medium --filter '*ProviderComparisonBenchmarks*'
```

All live fixtures are excluded from an unfiltered benchmark run when the
environment variable is absent. Supplying a live fixture filter without a
connection string fails immediately instead of silently producing a
server-free result.
ShortRun measurements are useful iteration diagnostics but have wide confidence
intervals. The checked-in provider comparison therefore uses `--job medium`
(two launches, ten warmups, and fifteen measured iterations) and still does not
claim that one provider is universally faster.

The refreshed 2026-08-07 Windows/Ryzen 7 5800X MediumRun records lower BlueTusk
mean latency and managed allocation on all five paired workloads. BlueTusk/Npgsql
measure 365/392 us and 1,634/2,079 B for the parameterized scalar, 195/209 ns and
168/184 B for warm checkout, 356/358 us and 773/1,137 B for the prepared scalar,
529/549 us and 1,413/1,418 B for the 1,000-row reader, and 2.279/2.305 ms and
3,832/8,426 B for the isolated 1 MiB stream. The exact values and environment are checked in under
`baselines/windows-ryzen7-5800x-dotnet10`; these paired results are not a
provider-wide performance guarantee.

`MultiplexingComparisonBenchmarks` is a separate fairness fixture for 64
concurrent parameterized scalar commands. Both providers use four physical
lanes, bounded queues, multiplexing enabled, no command timeout, identical SQL,
and one command object per logical operation. `OperationsPerInvoke=64` reports
per-command latency and allocation while preserving the real burst.
It also repeats the burst with 64 reusable command objects so scheduler and
protocol costs are visible separately from command construction.

```powershell
$env:BLUETUSK_BENCHMARK_CONNECTION_STRING = "Host=localhost;Port=5418;Database=bluetusk_tests;Username=postgres;Password=postgres;SSL Mode=Disable;Channel Binding=Disable"
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release -- --job medium --inProcess --filter '*MultiplexingComparisonBenchmarks*'
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release --no-build -- `
  --multiplexing-paired-evidence artifacts/benchmarks/multiplexing-paired-evidence.json
./eng/verify-multiplexing-performance.ps1 `
  -ReportPath artifacts/benchmarks/results/BlueTusk.Benchmarks.MultiplexingComparisonBenchmarks-report-full.json `
  -PairedReportPath artifacts/benchmarks/multiplexing-paired-evidence.json
```

The full BenchmarkDotNet report remains authoritative for BlueTusk's absolute
latency budgets, all managed-allocation comparisons, and multiplexed versus
ordinary pooled BlueTusk. A release-candidate provider latency comparison adds
the paired report above because executing one provider's complete
BenchmarkDotNet method before the other allows machine or server drift to be
mistaken for a provider difference.

The fresh-command multiplexed allocation cap is 75% of BlueTusk's ordinary
pooled path, while the reused-command cap remains 60%. The fresh cap preserves
at least a 25% multiplexing allocation advantage without treating an allocation
improvement in the ordinary pool as a multiplexing regression. Independent
absolute caps (1,850 B/op fresh and 800 B/op reused) and the Npgsql-relative cap
continue to fail closed if multiplexing itself regresses.

The paired capture runs before the long BenchmarkDotNet suite, performs 64
untimed warm-up bursts per provider, then five trials of 501 paired blocks. Each
block contains 32 real 64-command bursts per
provider (2,048 logical commands), reverses provider order from the preceding
block, and the first provider also reverses between trials. The verifier
recomputes each trial's mean, P95 and P99 ratios from the raw block timings and
applies the unchanged 1.05 provider caps to the median of the five trial
ratios. With 501 observations, P99 is the sixth-slowest block in each trial
rather than a statistic decided by one or two scheduler spikes. Running this
phase first also prevents prior suite state from contaminating the paired tail.
It rejects missing samples, changed
dimensions, invalid timings, incorrect order, future timestamps and duplicate
workloads. Run
`eng/test-multiplexing-performance-verifier.ps1` to exercise the positive
fixture and fail-closed mutations.

The 2026-08-04 Windows/Ryzen 7 5800X MediumRun from commit `9ba2c50`
records BlueTusk/Npgsql at 19.83/20.57 µs mean, 20.93/22.26 µs P95,
21.06/22.51 µs P99, and 1,733/1,738 B for end-to-end commands. Reused
commands were refreshed on 2026-08-07 and record 16.37 µs mean, 16.76 µs P95,
16.82 µs P99, and 749 B for BlueTusk, clearing the 800 B/op gate without a
latency regression. The unchanged comparison row records Npgsql at 20.01 µs
mean, 21.53 µs P95, 21.69 µs P99, and 794 B. The full checked-in report,
environment manifest, and budget verifier make this a reproducible regression
gate rather than a universal provider claim.

The immutable full comparison report remains bound to its original commit and
image digest. The refreshed BlueTusk row is stored in the adjacent
`MultiplexingComparisonBenchmarks-reused-hardening.json` supplemental report;
the allocation verifier reads it after the frozen report so the 800 B/op gate
uses the current measurement without invalidating historical evidence.

`--inProcess` is required for this repository fixture on Windows because
archived worktrees below ignored artifact directories can otherwise be
discovered by BenchmarkDotNet's generated-project build. It does not change
the workload, iteration counts, or measured command boundary.

The sequential-reader baseline exposed a 32-row portal-fetch default that made a
1,000-row scan pay 32 additional network exchanges. The optimized unlimited path
sends Parse/Bind/Describe/Execute/Sync in one transport write without an
intermediate metadata `Flush`, uses the unnamed portal without a row limit, and
reuses the server's unnamed statement when the exact SQL and parameter type OIDs
repeat, including commands created afresh for each execution.
It also reuses session-owned row/header storage after reader disposal, writes
parameters from struct-backed views, and decodes buffered binary integers without
boxing. Positive `SequentialFetchSize` values retain the metadata flush and
exercise bounded portal suspension for early-reader cancellation. Keep both
latency and allocation columns when evaluating this path; the checked-in report
is refreshed only after the corresponding integration gates pass.

The warm-pool comparison distinguishes untouched logical checkouts from leases
that reached the physical session. Untouched open/close cycles require no reset;
touched leases still run rollback when necessary plus `DISCARD ALL` before reuse.
The hot path shares the data source's immutable parsed settings, uses one
lock-free clean-session slot ahead of the contended channel, completes typed
data-source opens synchronously when the lease is ready, and leaves transaction,
notification, and large-object coordination unallocated until requested. The
live pool isolation and stress tests remain the authority for dirty-lease reset,
waiter wake-up, clear, and disposal invariants.

Scalar execution has a dedicated response path: it retains only the first field
of the first row instead of building buffered result-set and row collections.
Repeated commands cache named-parameter ordering until command text or the
parameter collection changes, command timeouts use the existing CancelRequest
timer rather than linked cancellation sources, and explicitly prepared scalar
commands reuse the statement description returned by `Prepare`. The latter
omits a redundant portal description while preserving binary/text format
identity and server-error recovery. Repeated prepared executions refresh an
in-memory deadline while one native timer wake-up remains outstanding, avoiding
two operating-system timer reschedules on every successful command.

Large sequential fields start with a 64 KiB per-session protocol buffer. Caller
buffers of 8 KiB or larger read directly from the transport after consuming any
already-buffered bytes, avoiding a second copy and a transient 1 MiB rental.
Smaller reads can rent up to 1 MiB of bounded read-ahead storage for the active
payload, which shrinks back to 64 KiB at the next frame boundary. Fast
synchronous `ValueTask` completion through the
field-stream stack reduces socket completions, while asynchronous stream reads
return legal partial results and let the protocol completion update row/stream
positions in the same continuation that completes a pending socket read.
Portal startup parses the small Parse/Bind/RowDescription response directly from
the protocol buffer before switching to incremental DataRow payload handling.
Streaming readers retain their command and timeout directly, avoiding per-reader
lifetime closures and delegate dispatch during cleanup. Parameterless commands
share immutable rewrite plans and the empty encoded-parameter vector, and do not
create a parameter collection unless the caller requests one. Portal startup and
prepared scalar execution use pooled `ValueTask` state; row delivery reuses a
per-session completion source. Single-segment backend frames decode without an
intermediate payload array, small streamed control frames reuse session storage,
repeated command tags reuse the last decoded value, and ordinary command
timeouts rent warmed registrations instead of constructing native timers.
Already buffered rows are read directly from the protocol window for the current
reader iteration, avoiding one small copy per row. Repeated portal descriptions
reuse immutable field metadata only after a byte-for-byte match against the new
server frame. One portal-lifetime read lease protects the shared window instead of
two atomic lifetime operations per buffered row; contiguous backend frames bypass
the general segmented parser, while typed sequential reads cache their concrete
field array and validated field count.
The 1 MiB comparison remains an end-to-end SQL, wire, and provider measurement;
it is not a memory-copy microbenchmark.

Run the complete suite in Release mode:

```powershell
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release
```

For a shorter development run:

```powershell
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release -- --job short
```

Run only the transport comparison with:

```powershell
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release --no-restore -- --job short --filter '*TransportPipelineBenchmarks*' '*TransportPipelineSocketBenchmarks*'
```

On Windows, the TLS benchmark needs access to the current user's certificate key store for its transient self-signed server certificate. Validate the loopback harness with `--transport-tls-smoke` if needed.

Markdown and brief JSON reports are written below `artifacts/benchmarks` by default. Set `BLUETUSK_BENCHMARK_ARTIFACTS` to redirect them. Checked-in results below `benchmarks/baselines` document one named reference environment; they are evidence and comparison inputs, not universal performance promises.

For an exact V1 candidate, dispatch `.github/workflows/performance.yml`. It
uses MediumRun, the in-process toolchain, the named self-hosted reference
machine and digest-pinned PostgreSQL 19, captures the paired alternating-provider
report, then runs all coverage, allocation, latency and multiplexing gates.
Both reports and their SHA-256 hashes are bound into the candidate manifest.
BenchmarkDotNet can return zero after producing an empty report or logging a
cleanup exception; the V1 wrapper therefore validates the report inventory and
scans the log instead of trusting only the process exit code.

Allocation ownership, the current end-to-end numbers, and the machine-checked regression budgets are documented in [Allocation discipline](../docs/architecture/allocation-discipline.md).
