# Multiplexing compatibility

BlueTusk multiplexing is an opt-in throughput path for independent,
session-neutral commands created directly from a `BlueTuskDataSource`. It does
not turn PostgreSQL sessions into logical connections and never moves an active
session-scoped operation between physical connections.

## Routing matrix

| Surface | Automatic route | Reason and evidence |
| --- | --- | --- |
| Independent text and parameterised commands | Multiplexed | A bounded FIFO scheduler writes at most the configured pipeline size, with one PostgreSQL `Sync` group per command. |
| Explicit `BlueTuskConnection` | Affine | The caller owns the logical/physical lease. `Require` fails before execution instead of silently falling back. |
| Transactions and savepoints | Affine | Transaction state, failures, and savepoints belong to one backend. |
| Explicitly prepared commands and SQL `PREPARE`/`EXECUTE`/`DEALLOCATE` | Affine | Prepared statement identity belongs to a backend session. |
| Sequential readers and cursors | Affine | A portal remains live until the reader completes or is disposed. `Require` fails closed. |
| COPY import/export | Affine | COPY changes the protocol state until completion, cancellation, or abort recovery. |
| Large objects and `lo_*`/legacy large-object routines | Affine | Descriptors and their owning transaction belong to one connection. |
| `LISTEN`/`UNLISTEN` and notification APIs | Affine | Listener registration and the notification pump own dedicated session state. `NOTIFY` is conservatively routed affine as well. |
| Temporary objects and `pg_temp` | Affine | Temporary schemas and objects belong to one backend. |
| Session advisory locks | Affine | Lock ownership is the backend process. Transaction-scoped advisory-lock routines are conservatively affine too. |
| `SET`, `RESET`, `SHOW`, `set_config`, `current_setting`, `currval`, and `lastval` | Affine | These mutate or observe session-local settings/sequence state. |
| `CALL`, `DO`, and unknown stateful user routines | Affine by explicit policy | `CALL` and `DO` fail closed automatically. SQL text cannot prove an arbitrary function is pure; set `MultiplexingMode.Disable` for a known stateful routine. |
| Replication | Dedicated | Replication owns an unpooled physical/logical `COPY BOTH` session and never enters the statement scheduler. |

`MultiplexingMode.Auto` uses this routing table,
`MultiplexingMode.Require` rejects every fallback, and
`MultiplexingMode.Disable` deliberately obtains an affine lease. The classifier
skips quoted strings, quoted identifiers, dollar-quoted bodies, line comments,
and nested block comments before inspecting tokens. It remains conservative;
it is not a SQL authorisation boundary.

## Scheduler and failure invariants

- The channel, pipeline group, worker count, commands per lease, and graceful
  shutdown duration are independently bounded.
- Accepted commands are serviced FIFO per lane. Multiple lanes increase
  concurrency without allowing one lane to retain a pool lease beyond
  `MaxCommandsPerLease`.
- Cancellation while waiting for channel admission does not enter the queue.
  Cancellation after admission completes that request and preserves the lane.
- Pool exhaustion remains cancellable. For pools of two or more sessions, the
  automatic worker count consumes at most half the configured pool; a
  one-session pool cannot reserve simultaneous affine capacity.
- Each command has its own protocol synchronization group. A server error,
  caller cancellation, or command timeout is drained through `ReadyForQuery`
  before the next group completes.
- Disposal stops admission, drains accepted work up to `ShutdownTimeout`, then
  aborts stuck physical transports and completes every remaining request.
- A touched lease is rolled back if needed and runs `DISCARD ALL` before reuse,
  clearing settings, temporary objects, listeners, advisory locks, and
  prepared statements.

The process-wide `BlueTusk.Diagnostics` meter exposes pending/executing
up/down counters, admission and outcome counters, queue-wait duration, pipeline
size, and forced-shutdown count. `GetMultiplexingStatistics()` remains the
per-data-source point-in-time view.

## PgBouncer

Live CI exercises bounded multiplexing through PgBouncer 1.24 in both session
and transaction modes. Session-affine transactions, temporary objects, and
explicit preparation retain their separate acceptance tests. Transaction mode
requires PgBouncer protocol-level prepared-statement support when an
application explicitly prepares statements; BlueTusk does not emulate
session-affine state on top of transaction pooling.

## Reproduce

Use an isolated PostgreSQL 18 test database:

```powershell
$env:BLUETUSK_TEST_CONNECTION_STRING = "Host=localhost;Port=5418;Database=bluetusk_tests;Username=postgres;Password=postgres;SSL Mode=Disable;Channel Binding=Disable"
dotnet test tests/BlueTusk.IntegrationTests/BlueTusk.IntegrationTests.csproj `
  --configuration Release `
  --filter FullyQualifiedName~BlueTuskMultiplexingIntegrationTests
```

Start the repository PgBouncer fixtures and run their matrix:

```powershell
docker compose -f eng/compose/postgres.yml --profile compatibility-tests up -d --build --wait pgbouncer-session18 pgbouncer-transaction18
dotnet test tests/BlueTusk.IntegrationTests/BlueTusk.IntegrationTests.csproj `
  --configuration Release `
  --filter FullyQualifiedName~PgBouncer
```

Run the release comparison and its machine gate:

```powershell
$env:BLUETUSK_BENCHMARK_CONNECTION_STRING = $env:BLUETUSK_TEST_CONNECTION_STRING
$env:BLUETUSK_BENCHMARK_ARTIFACTS = "artifacts/benchmarks"
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release -- `
  --job medium --inProcess --filter '*MultiplexingComparisonBenchmarks*'
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release --no-build -- `
  --multiplexing-paired-evidence artifacts/benchmarks/multiplexing-paired-evidence.json
./eng/verify-multiplexing-performance.ps1 `
  -ReportPath artifacts/benchmarks/results/BlueTusk.Benchmarks.MultiplexingComparisonBenchmarks-report-full.json `
  -PairedReportPath artifacts/benchmarks/multiplexing-paired-evidence.json
```

The checked-in MediumRun, not a development ShortRun, is the regression
authority. It reports mean, P95, P99, operations per second, and managed
allocation for BlueTusk multiplexed, BlueTusk ordinary pooled, Npgsql
multiplexed, and Npgsql ordinary pooled paths. Results from one loopback
machine are evidence for this workload, not a universal provider-performance
claim.

The exact-candidate workflow uses the full report for absolute latency,
allocation and pooled-path comparisons. Provider-relative latency comes from
five alternating paired trials: each trial records 501 blocks, each block
contains 32 bursts per provider, and execution order reverses between blocks
and trials. The verifier calculates each trial's mean/P95/P99 ratio from raw
timings and gates the median trial ratio. The paired phase runs before the long
BenchmarkDotNet suite so prior thread-pool, database and thermal state cannot
contaminate it. With 501 observations per trial, P99 is the sixth-slowest block
rather than a decision dominated by one or two scheduler spikes; alternation
also avoids measuring all of one provider before all of the other.
