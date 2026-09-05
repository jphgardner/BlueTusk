# Provider request-level performance capture

This adapter measures all 16 Provider comparison features through BlueTusk and
Npgsql 10.0.3, plus four optional pool-contention probes. It supports Windows x64 and Linux x64, configurable concurrency
up to 256, and both plaintext and TLS PostgreSQL connections. It records every
individual operation, rather than percentiles of averaged operation blocks.

**Status:** implemented capture adapter, not a completed leadership gate. Short
local smoke runs validate its operation but must not be presented as performance
wins. Dedicated-runner captures, validated statistical assumptions and the remaining
cross-product adapters are still required by the
[performance programme](performance-leadership-1.1.md).

## Run a matrix

Build the benchmark application from a clean, committed candidate. Point
`BLUETUSK_BENCHMARK_CONNECTION_STRING` at a **dedicated benchmark database**.
Do not point this harness at a production database: it creates temporary test
schemas, writes data, creates large objects and opens many connections.
The connection string is inherited by child processes, not placed in evidence
files or command-line arguments.

```powershell
dotnet build benchmarks/BlueTusk.Benchmarks/BlueTusk.Benchmarks.csproj -c Release

./eng/capture-provider-request-matrix.ps1 `
    -ExpectedCommit $candidateSha `
    -PostgreSqlImage $observedImageWithDigest `
    -OutputPath artifacts/provider-request-windows `
    -Concurrency 1,64,256 `
    -Trials 5 `
    -WarmupSeconds 5 `
    -MeasurementSeconds 10 `
    -MaximumTotalSamples 16000000
```

The default feature set is the full 16-feature list from
[`performance-leadership-contract.json`](../../eng/performance-leadership-contract.json).
For focused diagnosis, pass `-Features prepared-scalar,ef-update`. A subset does
not satisfy full-matrix coverage. Use `-Diagnostic` for dirty working trees or
shorter smoke-test windows; those captures are explicitly labelled diagnostic.
The wrapper rejects mismatched candidate SHAs, existing output directories,
duplicate cases, wrong reference versions, incomplete raw samples and failed
child processes. Non-diagnostic captures also check assembly commit metadata.

Each feature/concurrency/trial runs each provider in a **separate process**.
Provider order alternates between trials. The wrapper does not overlap provider
runs. Workers share one provider data source and pool, use independent active
connections where required, and never share a command or DbContext. Test schema
names are unique to each process. EF update workers use disjoint row keys; a
hot-row contention test is a separate workload, not implied by this adapter.

## Counters and measurement boundaries

- Load is closed-loop: each worker issues its next operation after the previous
  one completes. This is not an open-loop arrival-rate or queueing-latency test.
- Raw timings use monotonic Stopwatch ticks around complete, checked operations,
  including reader/stream consumption and disposal. The clock frequency is
  retained. No request samples are silently dropped or replaced by averages.
- Warmup and measurement use equal configured windows for each provider.
  In-flight requests finish before counters are read; actual elapsed ticks are
  retained for throughput calculations.
- Allocation, CPU time and GC counts cover the client process during measurement,
  including harness overhead. Sample arrays are preallocated before counters
  start. No estimated overhead is subtracted to manufacture a win.
- Peak RSS is the client process high-water mark, including setup and sample
  buffers; PostgreSQL is excluded. It is not a database-plus-client memory total.
- Every 64 requests a worker yields to the scheduler, outside individual latency
  timing. Throughput and process counters include that overhead. This prevents
  synchronously completing operations from starving later workers.
- COPY import, EF insert and EF update roll back their transactions, matching the
  existing Provider feature matrix. They do not measure durable commit latency.
  Empty begin/rollback performs no application data work.

`MaximumTotalSamples` is a **total budget divided across all workers**, not a
per-worker allocation. Sixteen million 64-bit samples reserve about 122 MiB
in total at concurrency 1, 64 or 256. Per-worker read buffers additionally use
128 KiB each. The engine rejects sample arrays totalling more than 32 million
entries (about 244 MiB), and fails if a worker fills its assigned sample buffer.
Raise the explicit bound or choose an equal shorter diagnostic window; never
discard overflow and treat the remaining samples as a complete run.

Provide enough database capacity. Notification tests need two connections per
worker; most other cases need one. Preflight requires `max_connections` of at
least `2 * concurrency + 10` for notification delivery and `concurrency + 10`
for the other original features. The optional four-slot contention probes require
`max_connections >= 14`, regardless of worker count. This is not a guarantee of sufficient RAM, CPU or free connection
slots: provision and attest the dedicated runner/database before a full run.

## Measure pool saturation and multiplexing

The original 16-feature adapters provision enough connections for workers that
hold their own leases. Sharing a pool in those cases does **not** mean that the
pool is saturated. Use these additional features to measure many requests
competing for only four physical connections:

| Feature | Multiplexing | Command lifetime |
|---|---|---|
| `pooled-scalar` | Disabled | New command per request |
| `pooled-reused-scalar` | Disabled | One reused command per worker |
| `multiplexed-scalar` | Enabled | New command per request |
| `multiplexed-reused-scalar` | Enabled | One reused command per worker |

Select them explicitly with `-Features`. The default remains the original
16-feature release matrix; these probes neither replace it nor certify its
coverage. For example, use `-Features pooled-reused-scalar,multiplexed-reused-scalar`
with `-Concurrency 1,64,256`. Keep the same trial counts, observation windows,
transport and sample budgets for both providers.

Every worker issues `SELECT $1::int4` with its own typed integer parameter and
checks its own returned value. Commands belong to the shared data source;
workers do not hold a physical connection between requests. A reused command is
not explicitly prepared. Both providers use pool size four and command timeout
zero, with explicit request cancellation tokens. BlueTusk's multiplexing probe
uses four workers, a 256-request queue, up to 64 commands per pipeline and 65,536
commands per lease, matching the existing burst benchmark. The options are
recorded, not presented as automatically selected production defaults.

These probes record **individual complete request latency**, including time
waiting for a pool slot, verification and disposal. They remain closed-loop:
they are not an open-loop arrival-rate test and do not report a latency at an
offered load higher than the completed throughput. They also differ from the
64-request burst benchmark in scheduling and enabled per-request cancellation;
their latency and allocation numbers must not be substituted for that benchmark's
absolute budgets. The analyzer rejects contention evidence that substitutes a
larger pool or the wrong multiplexing mode, even if both providers agree on that
incorrect metadata.

## TLS

Configure certificate validation in the connection string and pass `-Tls`.
The adapter queries `pg_stat_ssl` and fails if the observed connection is not
encrypted. For production-like verification use `SSL Mode=VerifyFull` and an
explicit trusted root certificate; enabling `-Tls` does not itself select or
weaken certificate validation.

For the shared fixture's `Root Certificate` setting, the adapter explicitly
configures BlueTusk's documented `UseRemoteCertificateValidationCallback` API
with a custom-root chain, server-authentication purpose, expiry and hostname
checks. Npgsql uses its native root-certificate setting. Both use the requested
revocation-check setting. The disposable CA has no revocation service, so its
smoke-test configuration leaves revocation checking off; this is recorded in
the artifact and is not evidence of production revocation handling. This
translation belongs to the benchmark adapter, not to BlueTusk's general
connection-string parser.

`eng/new-benchmark-tls-fixture.ps1 -OutputPath artifacts/benchmark-tls` creates
a two-day disposable CA and localhost server certificate/key for isolated smoke
tests. It does not change the system trust store or configure a server. Keep the
private server key confined to that disposable test environment.

Constrained-network capture is **not implemented by this adapter**. A TLS run
is not evidence of controlled latency, bandwidth, jitter or packet loss. A
separate digest-pinned network-shaping fixture and retained configuration are
required before claiming that variant.

## Artifacts and remaining verification

Each child writes a raw JSON report and the wrapper retains its input options.
`capture-index.json` is written only after every requested case passes capture
validation. It records workload keys, trial/provider order, file hashes and the
measured harness hash. Failed runs retain their partial files but have no
completed index. Output directories are never overwritten.

The index deliberately sets `leadershipGatePassed` to `false`. It is not the
schema-2 consolidated leadership evidence document. An image digest supplied
to this wrapper is a declared identity; retain the actual container/runtime
inspection that binds it to the measured server.

## Derive a readable comparison

After capture, run the analyzer against its complete index and exact source SHA:

```powershell
dotnet benchmarks/BlueTusk.Benchmarks/bin/Release/net10.0/BlueTusk.Benchmarks.dll `
    --provider-request-analyze `
    artifacts/provider-request-windows/capture-index.json `
    $candidateSha `
    artifacts/provider-request-windows-analysis
```

The output directory must be new. `provider-report.md` contains latency,
allocation, throughput, CPU and peak-RSS comparisons; `provider-analysis.json`
retains the per-trial absolute values, GC counts, source hashes, ratios and
confidence intervals. The analyzer records its own assembly version separately
from the measured candidate, so it can analyze a retained older capture without
pretending to have measured a newer binary.

The analyzer checks every raw file hash before parsing it. It rejects missing
trial pairs, duplicate records, mismatched source/runtime/transport/method
metadata, changed observation windows, inconsistent sample counts and invalid
counters. It recomputes mean and nearest-rank P95/P99 from individual request
ticks, allocation and CPU per completed operation, and throughput from the
actual elapsed measurement window. It does not accept precomputed summary
statistics as a substitute for those inputs.

Each independently restarted process is one trial. For each metric, the reported
ratio is the geometric mean of matched BlueTusk/Npgsql trial ratios; trials have
equal weight. At least five pairs are required for the approximate, two-sided
95% Student-t interval on trial log-ratios. The calculation applies the
[NIST mean confidence-interval formula](https://www.itl.nist.gov/div898/handbook/eda/section3/eda352.htm)
to those log-ratios and exponentiates its bounds. It uses the
[NIST critical values](https://www.itl.nist.gov/div898/handbook/eda/section3/eda3672.htm)
with a small conservative allowance for published rounding.

This assumes independent trials and approximately normal trial log-ratios;
the analyzer does not establish those assumptions. Requests within one process
are not treated as independent trials. These are individual metric intervals,
not a simultaneous confidence guarantee across the full matrix. Zero-valued
counters remain visible but receive no log-ratio inference. Too few trials or
an interval crossing the target cannot produce a numerical-target pass.

Even when a workload meets the numerical 0.98 point/upper-confidence target for
all four latency/allocation measures, **the analyzer does not certify a release
gate**. Diagnostic runs remain diagnostic. Isolated-runner and image provenance,
statistical validation, final-SHA evidence and all remaining product/OS/network
workloads are still required. This output does not feed the schema-2 consolidated
verifier yet: that verifier's separate candidate/reference bounds are not the
same quantity as this analyzer's paired-ratio interval. Integration must preserve
that distinction, not relabel one type of interval as the other.

The no-database self-test runs with `--provider-request-self-test` and is included
in the build workflow. It covers real worker concurrency, synchronous-operation
fairness, complete samples, counters, warmup exclusion, sample overflow,
cancellation, worker failure, invalid resource bounds and private-CA validation.
It also tests raw statistic derivation, known confidence limits, ties, zero
counters, insufficient trials, hash mismatches, source identity, incomplete
pairs, path containment and mismatched capture methodology. Its synthetic
fixtures test machinery, not measured BlueTusk/Npgsql performance.

## Supplementary EF batch-size diagnostic

`eng/capture-ef-batching.ps1` helps compare the cost of saving 100 or 1,000
tracked updates together. It runs BlueTusk with batch limits 1, 42 and 1,000,
plus Npgsql 10.0.3 with its default. Provider processes run separately and their
order rotates between trials. This is supplementary engineering evidence; it
does not add a seventeenth feature to, or replace, the release matrix.

Start with a clean, committed checkout and a benchmark assembly built from that
exact commit. Configure `BLUETUSK_BENCHMARK_CONNECTION_STRING` for an isolated,
disposable PostgreSQL database, then supply:

- `ExpectedCommit`: the full 40-character measured commit;
- `PostgreSqlImage`: the actual digest-pinned image used by that database;
- `BenchmarkAssemblyPath`: the isolated Release build's benchmark DLL; and
- `OutputPath`: a new directory beneath `artifacts`.

The script defaults to five trials per arm, one-second warmup and two-second
observation windows. Every operation creates a context, loads and tracks the
same rows, changes them, calls `SaveChangesAsync`, rolls back and disposes the
context. It checks affected-row counts and verifies that the updates were not
committed. Raw request ticks, allocation, CPU, RSS, GC counts, assembly versions
and capture hashes are retained. The completed index is written only after all
captures pass their identity and result checks.

The fixture uses **unlogged tables and rollback**, not durable commits. These
short, C1, local diagnostic runs cannot establish production capacity, sustained
tail latency or workload leadership. Compare actual commit cost, logged tables,
representative network conditions and realistic concurrent work separately.
The retained `releaseGatePassed` value is always false. The request-capture
self-test checks that this supplementary entry point rejects non-diagnostic
mode and unsupported row, concurrency and batch-size options.
