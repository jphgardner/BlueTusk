# Provider request-level performance capture

This adapter measures all 16 Provider comparison features through BlueTusk and
Npgsql 10.0.3. It supports Windows x64 and Linux x64, configurable concurrency
up to 256, and both plaintext and TLS PostgreSQL connections. It records every
individual operation, rather than percentiles of averaged operation blocks.

**Status:** implemented capture adapter, not a completed leadership gate. Short
local smoke runs validate its operation but must not be presented as performance
wins. Dedicated-runner captures, confidence intervals and the remaining
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
otherwise. This is not a guarantee of sufficient RAM, CPU or free connection
slots: provision and attest the dedicated runner/database before a full run.

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
schema-2 consolidated leadership evidence document. The next processing stage
must independently derive mean/P95/P99, throughput, allocation and CPU/event
from raw counters and samples, compute paired confidence intervals, verify
runner and image provenance, and combine the rest of the required product
matrix. An image digest supplied to this wrapper is a declared identity; retain
the actual container/runtime inspection that binds it to the measured server.

The no-database self-test runs with `--provider-request-self-test` and is included
in the build workflow. It covers real worker concurrency, synchronous-operation
fairness, complete samples, counters, warmup exclusion, sample overflow,
cancellation, worker failure and invalid resource bounds. It is machinery
validation, not measured BlueTusk/Npgsql performance evidence.
