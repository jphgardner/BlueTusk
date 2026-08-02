# BlueTusk benchmarks

The BenchmarkDotNet suite covers complete synchronous/asynchronous command parameter and result paths, protocol-connection writes, protocol parsing and incremental payload streaming, typed buffered readers and large field access, integer/numeric/temporal/JSONB codecs, catalogue-composed array/enum/range/composite encoding and decoding, binary COPY field encoding, notification decoding, replication WAL-frame decoding and bounded pull consumption, large-object stream transfer overhead, warm-session pool checkout, EF query compilation/materialisation/writes, SQL/PGQ traversal, and the transport-pipeline decision. Pool, command, reader, protocol-streaming, replication, and large-object workloads isolate provider bookkeeping with in-memory sessions; the application and comparison fixtures execute against live PostgreSQL.

`TransportPipelineBenchmarks` compares the production ArrayPool/Span/Memory reader with a benchmark-only, bounded `System.IO.Pipelines` prototype across fragmented rows, a 1 MiB field, COPY frames, and cancellation recovery, using genuine sync and async entry points. `TransportPipelineSocketBenchmarks` repeats the comparison over raw TCP and authenticated loopback TLS. The production packages do not reference `System.IO.Pipelines`; the resulting decision and limitations are in [ADR 0005](../docs/architecture/decisions/0005-postgresql-pipeline-mode-and-transport-pipelines.md).

`PullOneThousandBoundedXLogFrames` consumes one already-owned frame at a time
without a prefetch queue. This mirrors the replication async iterator's
backpressure boundary: the connection reads the next `CopyData` payload only
when the consumer requests the next message. The benchmark normalizes time and
allocation per frame; it does not claim to measure server or network latency.

## Live application workloads

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
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release -- --job short --filter '*EntityFrameworkCoreBenchmarks*' '*SqlPgqBenchmarks*'
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
buffers. The broader BlueTusk-only suite continues to cover the remaining type,
batch, pipeline, COPY, concurrency, EF, graph, and replication workloads from
the performance strategy.

Set a dedicated live connection string and run the comparison explicitly:

```powershell
$env:BLUETUSK_BENCHMARK_CONNECTION_STRING = "Host=localhost;Port=5419;Database=bluetusk_tests;Username=postgres;Password=postgres;SSL Mode=Disable;Channel Binding=Disable"
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release -- --job short --filter '*ProviderComparisonBenchmarks*'
```

All live fixtures are excluded from an unfiltered benchmark run when the
environment variable is absent. Supplying a live fixture filter without a
connection string fails immediately instead of silently producing a
server-free result.
ShortRun measurements are environment-specific diagnostics with wide confidence
intervals; they identify optimization work and are not a claim that one provider
is universally faster.

The final 2026-08-02 Windows/Ryzen 7 5800X reference run records a 199 ns / 168 B
BlueTusk warm checkout versus 210 ns / 184 B for Npgsql. Parameterized and
prepared BlueTusk scalars also use less managed memory (2,064 B versus 2,113 B,
and 992 B versus 1,132 B), although their loopback latencies remain 1.42x and
1.50x Npgsql. The sequential 1,000-row and 1 MiB stream paths remain 1.28x and
1.34x slower and allocate more. The exact current values and environment are
checked in under `baselines/windows-ryzen7-5800x-dotnet10`; measured wins and
remaining gaps are both retained instead of being converted into an unmeasured
provider-wide performance claim.

The sequential-reader baseline exposed a 32-row portal-fetch default that made a
1,000-row scan pay 32 additional network exchanges. The optimized path sends
Parse/Bind/Describe/Execute/Sync in one transport write (with a metadata Flush
before Execute), streams without a row limit by default, reuses its forward-only
row object, and decodes buffered binary integers without boxing. Positive
`SequentialFetchSize` values still exercise bounded portal suspension. Keep both
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
identity and server-error recovery.

Large sequential fields start with a 64 KiB per-session protocol buffer and can
rent up to 1 MiB of read-ahead storage for the active large payload. The buffer
shrinks back to 64 KiB at the next frame boundary, so ordinary sessions do not
retain the large window. Fast synchronous `ValueTask` completion through the
field-stream stack reduces socket completions, while asynchronous stream reads
return legal partial results and let the protocol completion update row/stream
positions in the same continuation that completes a pending socket read.
Portal startup parses the small Parse/Bind/RowDescription response directly from
the protocol buffer before switching to incremental DataRow payload handling.
Streaming readers retain their command and timeout directly, avoiding per-reader
lifetime closures and delegate dispatch during cleanup.
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

Allocation ownership, the current end-to-end numbers, and the machine-checked regression budgets are documented in [Allocation discipline](../docs/architecture/allocation-discipline.md).
