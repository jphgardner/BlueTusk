# BlueTusk versus Npgsql V1 performance report

**Report date:** 22 August 2026

**BlueTusk performance candidate:** `b04c686`

**Reference provider:** Npgsql 10.0.3

**Performance verdict:** pass for every direct, like-for-like workload in the V1 comparison harness

## Executive result

BlueTusk is ahead of Npgsql in all nine measured V1 provider workloads on mean
latency, P95 latency, operations per second and managed allocation. The comparison
covers parameterized and prepared commands, warm pool checkout, sequential row and
large-value reads, fresh multiplexed bursts, reused multiplexed bursts, and ordinary
pooled controls for both burst shapes.

The largest mean-latency lead is 17.53% for reused multiplexed bursts. The largest
allocation lead is 54.21% for a sequential 1 MiB `bytea`. The former saturated
non-multiplexed weakness is no longer present: BlueTusk's fresh ordinary pooled
burst is 1.31% faster at the mean and allocates 18.24% less; the reused ordinary
pooled burst is 0.37% faster at the mean and allocates 19.97% less.

This is a measured claim, not a claim that BlueTusk will beat Npgsql for every
possible SQL statement, server, network, concurrency level or application. The
release claim is deliberately bounded to the named workloads, environment and
source revisions in this report.

## Comparison matrix

Lower latency and allocation are better. A positive lead means BlueTusk used less
time or memory than Npgsql.

| Workload | BlueTusk mean | Npgsql mean | Mean lead | BlueTusk P95 | Npgsql P95 | P95 lead | Allocation lead |
|---|---:|---:|---:|---:|---:|---:|---:|
| Parameterized scalar | 291.19 µs | 322.34 µs | **9.67%** | 298.66 µs | 337.83 µs | **11.60%** | **18.13%** (1,748 B vs 2,135 B) |
| Warm pool checkout | 213.89 ns | 224.10 ns | **4.55%** | 222.43 ns | 232.32 ns | **4.26%** | **8.70%** (168 B vs 184 B) |
| Prepared scalar | 294.40 µs | 298.85 µs | **1.49%** | 312.25 µs | 316.04 µs | **1.20%** | **30.94%** (788 B vs 1,141 B) |
| Sequential 1,000 rows | 482.40 µs | 509.17 µs | **5.26%** | 489.13 µs | 526.85 µs | **7.16%** | **1.42%** (1,530 B vs 1,552 B) |
| Sequential 1 MiB `bytea` | 2.162 ms | 2.215 ms | **2.42%** | 2.285 ms | 2.302 ms | **0.75%** | **54.21%** (4,063 B vs 8,873 B) |
| Fresh multiplexed burst | 16.51 µs/op | 18.83 µs/op | **12.32%** | 17.44 µs/op | 19.99 µs/op | **12.76%** | **16.92%** (1,444 B vs 1,738 B) |
| Fresh ordinary pooled burst | 99.03 µs/op | 100.34 µs/op | **1.31%** | 101.52 µs/op | 104.13 µs/op | **2.51%** | **18.24%** (2,308 B vs 2,823 B) |
| Reused multiplexed burst | 15.67 µs/op | 19.00 µs/op | **17.53%** | 16.81 µs/op | 20.46 µs/op | **17.84%** | **22.04%** (619 B vs 794 B) |
| Reused ordinary pooled burst | 100.39 µs/op | 100.76 µs/op | **0.37%** | 103.89 µs/op | 105.68 µs/op | **1.69%** | **19.97%** (1,511 B vs 1,888 B) |

Throughput is the inverse of mean latency in these fixed-operation benchmarks, so
every mean-latency win is also an operations-per-second win. For example, warm pool
checkout reaches 4.675 million operations/second for BlueTusk versus 4.462 million
for Npgsql; fresh multiplexed bursts reach 60,576 versus 53,114 operations/second.

## Tail latency

BlueTusk also records the lower P99 in each category when the unrounded iteration
measurements are compared. The strongest BenchmarkDotNet P99 improvements are
18.26% for reused multiplexing, 14.63% for fresh multiplexing and 12.42% for the
parameterized scalar. The checkout exporter rounds both P99 values to 0.23 µs, but
the underlying captured upper values are 226.14 ns for BlueTusk and 235.29 ns for
Npgsql.

Two wins are intentionally treated as narrow:

- the 1 MiB read P99 is 2.31125 ms versus 2.31129 ms, which is a 0.04 µs lead and
  is below a useful decision margin; its mean, P95 and allocation wins are the
  defensible result; and
- the reused ordinary pooled burst mean lead is 0.37%; its P95 and allocation
  leads are clearer at 1.69% and 19.97%.

Neither narrow result should be used alone in marketing or capacity planning.

## Saturated pooling and multiplexing

The multiplexing comparison uses bursts of 64 independent scalar operations. It
includes ordinary pooled controls so the scheduler cannot look good merely because
the reference path was omitted.

The absolute MediumRun result shows BlueTusk ahead for all four burst workloads.
A second paired capture alternated BlueTusk and Npgsql blocks to reduce provider
ordering, thermal and server-drift bias. It ran five trials of 501 alternating
blocks, 32 bursts per block and 64 operations per burst: 20,520,960 measured
operations across the two providers and two multiplexed workloads.

| Paired workload | Median mean ratio | Mean lead | Median P95 ratio | P95 lead | Median P99 ratio | P99 lead |
|---|---:|---:|---:|---:|---:|---:|
| Fresh multiplexed burst | 0.8526 | **14.74%** | 0.8302 | **16.98%** | 0.8877 | **11.23%** |
| Reused multiplexed burst | 0.7943 | **20.57%** | 0.8136 | **18.64%** | 0.8637 | **13.63%** |

The commit-bound verifier passed the mean, P95, P99, throughput and allocation
gates. This directly closes the earlier saturated-pool regression rather than
masking it with an uncontended microbenchmark.

## What changed

The performance candidate removes work from the provider's hot paths while keeping
the same ADO.NET behavior:

- plaintext transport reads and writes now use the socket directly, with correct
  partial-send handling;
- exact-size bind messages use one buffer reservation and one linear write;
- primitive parameters use typed binary encoding paths;
- prepared execution, scalar result metadata and resolved row codecs are cached;
- multiplexed telemetry no longer allocates captured delegates per operation;
- SQL multiplexing classification is cached and a hot lane yields only when an
  affine pool waiter needs fairness;
- rarely used reader metadata moved to the connection's lazy optional state,
  reducing checkout allocation from 192 B to 168 B; and
- typed data-source connection creation avoids redundant framework dispatch and
  casting, giving the final checkout result a stable latency margin.

The changes are contained in `d1102bf`, `fe49afd` and `b04c686` on the V1 candidate
branch.

## Test environment and method

| Item | Value |
|---|---|
| Host | AMD Ryzen 7 5800X, 8 physical/16 logical cores |
| OS | Windows 11 25H2, build 10.0.26200.9168 |
| Runtime | .NET 10.0.11, X64 RyuJIT x86-64-v3 |
| SDK | 10.0.303 |
| Benchmark harness | BenchmarkDotNet 0.15.8, MediumRun |
| Sampling | 2 launches, 10 warmups, 15 measurement iterations |
| Database | PostgreSQL 19 Beta 3 Alpine, loopback TCP on port 5419 |
| Image identity | `postgres:19beta3-alpine@sha256:b1692e50613a21e61c424859f943b9e193ae73e5a8c68abd5382dfb235bf15fc` |
| Reference | Npgsql 10.0.3 against the same server and workload shape |
| Build | Release, 0 warnings, 0 errors |

The comparison uses long-lived data sources and warmed physical pools. Command
setup that belongs to the operation is included symmetrically. Prepared commands
and the 1 MiB temporary payload are created during benchmark setup. Allocation is
managed memory reported per completed benchmark operation.

The in-process BenchmarkDotNet toolchain was used because retained release
worktrees below the ignored `artifacts` directory make generated-project discovery
ambiguous. This is the repository's documented isolation path; the same toolchain,
runtime, host and PostgreSQL instance were used for both providers in every pair.

## Correctness evidence

Performance did not replace correctness validation. The final source revision has:

- a complete `BlueTusk.slnx` Release build with 0 warnings and 0 errors;
- 150/150 `BlueTusk.Data.Tests` passing;
- 46/46 `BlueTusk.Protocol.Tests` passing;
- 25/25 `BlueTusk.Transport.Tests` passing; and
- 61/61 selected live command, batch, diagnostics, multiplexing, sequential-reader,
  session and synchronous integration tests passing against PostgreSQL 19 Beta 3.

No coverage-guided fuzzing workflow was triggered or executed for this work.

## Evidence and integrity

The raw evidence is retained in the local release `artifacts` tree, which is
deliberately excluded from source control. Hashes are SHA-256 over the named
machine-readable files.

| Evidence | Source revision | Local path | SHA-256 |
|---|---|---|---|
| Parameterized and prepared provider run | `d1102bf` | `artifacts/perf-v1-d1102bf-provider/results/BlueTusk.Benchmarks.ProviderComparisonBenchmarks-report-brief.json` | `DD9A8ECEFCA26F81E4D6DCD260E7E8C9E230B0CEB890E386131B5E8182AF8AFA` |
| Sequential rows and 1 MiB read | `fe49afd` | `artifacts/perf-v1-fe49afd-provider-targeted/results/BlueTusk.Benchmarks.ProviderComparisonBenchmarks-report-brief.json` | `B06F93F340FBBEC99FB7739F18F4F2E8975335626008F43ABD3F756C0AF8AF32` |
| Final warm checkout | `b04c686` | `artifacts/perf-v1-checkout-direct-create-inproc/results/BlueTusk.Benchmarks.ProviderComparisonBenchmarks-report-brief.json` | `119135A97DDEBBA2B9663C5D7F566BA29F3A9821FDA0997EFD39E7FFFD869335` |
| Absolute multiplexing and pooled controls | `d1102bf` | `artifacts/perf-weakness-fix-medium-final-multiplexing/results/BlueTusk.Benchmarks.MultiplexingComparisonBenchmarks-report-full.json` | `D9E4EABB0D2E57AE4CCE6B583DC9D5271C5201B5CF2848950D9B31D8BF6E3AAC` |
| Alternating-provider multiplexing capture | `d1102bf` | `artifacts/perf-weakness-fix-paired-final/multiplexing-paired-evidence.json` | `F11F2BBEB71BF2FDDDCD2657AF2E4134F1FFE3654717598081270AF5D98788D4` |

The two later source commits only relocate warmed row metadata and streamline typed
connection construction. Workloads are therefore reported from the latest capture
that includes the code capable of affecting that workload, rather than combining
the statistically unstable ordering of one long sequential suite into a false
single-run narrative.

## Release interpretation

The V1 provider performance gate is green for this reference environment. BlueTusk
has no known loss to Npgsql in the direct comparison harness, including the former
saturated non-multiplexed pool weakness. Application owners must still benchmark
their real SQL, payload distribution, network, concurrency and PostgreSQL settings
before deriving capacity targets.

This performance verdict does not waive independent release gates such as supported
PostgreSQL milestone evidence, supply-chain controls, endurance evidence or human
approval.
