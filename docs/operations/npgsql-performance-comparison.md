# BlueTusk versus Npgsql: V1 performance report

**Report date:** 23 August 2026

**Candidate:** the V1 source revision containing this report

**Reference provider:** Npgsql 10.0.3

**Database:** PostgreSQL 19 Beta 3

**Verdict:** pass for all nine named provider and concurrency comparisons

## Executive result

BlueTusk records lower mean, P95 and P99 latency than Npgsql in every workload in
the V1 comparison programme. It also allocates less managed memory in every
comparison. The result covers five direct provider operations and four saturated
64-operation concurrency shapes.

The former saturated non-multiplexed pooling weakness is closed. BlueTusk now
reduces mean latency by 4.94% for fresh ordinary pooled bursts and by 4.76% when
commands are reused. It also reduces allocation by 17.27% and 20.92%
respectively. Multiplexed bursts lead by 17.01% to 20.78% at the mean.

This is a bounded, reproducible V1 result. It does not claim that BlueTusk beats
Npgsql for every SQL statement, schema, payload, server, network or concurrency
level. Capacity planning still requires an application-specific benchmark.

## Results at a glance

Lower latency and allocation are better. Latency columns show BlueTusk's
reduction relative to Npgsql from alternating-provider evidence. Throughput is
the inverse of mean latency for these fixed-operation workloads.

| Workload | Mean latency reduction | P95 reduction | P99 reduction | Throughput uplift | Managed allocation reduction |
|---|---:|---:|---:|---:|---:|
| Warm pool checkout | **15.20%** | **19.14%** | **30.91%** | **17.92%** | **8.70%** (168 B vs 184 B) |
| Parameterized scalar | **8.97%** | **9.56%** | **13.08%** | **9.85%** | **17.07%** (1,773 B vs 2,138 B) |
| Prepared scalar | **0.82%** | **0.52%** | **1.45%** | **0.82%** | **20.18%** (898 B vs 1,125 B) |
| Sequential 1,000 rows | **5.26%** | **4.93%** | **6.10%** | **5.55%** | **1.86%** (1,585 B vs 1,615 B) |
| Sequential 1 MiB `bytea` | **1.13%** | **0.77%** | **4.89%** | **1.15%** | **82.20%** (1,585 B vs 8,906 B) |
| Fresh multiplexed burst | **17.01%** | **14.13%** | **13.02%** | **20.49%** | **13.87%** (1,497 B vs 1,738 B) |
| Reused multiplexed burst | **20.78%** | **17.94%** | **12.68%** | **26.23%** | **21.79%** (621 B vs 794 B) |
| Fresh ordinary pooled burst | **4.94%** | **3.81%** | **3.91%** | **5.19%** | **17.27%** (2,337 B vs 2,825 B) |
| Reused ordinary pooled burst | **4.76%** | **3.52%** | **6.65%** | **5.00%** | **20.92%** (1,489 B vs 1,883 B) |

All 36 provider-relative decisions in the table pass a checked-in maximum ratio
of `1.0`: five direct and four concurrency workloads, each evaluated for mean,
P95, P99 and allocation. The gate therefore fails on parity loss; it contains no
allowance that can hide a slower or more allocating BlueTusk result.

The prepared-command latency win is narrow. It is a pass in the alternating
capture, but it should be described as measured parity rather than as a material
capacity advantage. The sequential BenchmarkDotNet order below reverses that
pair by 0.09%, illustrating why the alternating evidence is the declared
cross-provider authority. The large-value mean is similarly close; its
allocation reduction is the useful engineering result.

## Absolute BenchmarkDotNet context

Alternating-provider ratios are the cross-provider latency authority because
they reduce provider order, thermal and server-drift bias. BenchmarkDotNet
MediumRun remains the absolute latency and managed-allocation authority. The
representative direct-provider captures are:

| Workload | BlueTusk mean | Npgsql mean | BlueTusk allocation | Npgsql allocation |
|---|---:|---:|---:|---:|
| Warm pool checkout | 211.07 ns | 227.56 ns | 168 B | 184 B |
| Parameterized scalar | 295.27 us | 320.81 us | 1,773 B | 2,138 B |
| Prepared scalar | 291.13 us | 290.88 us | 898 B | 1,125 B |
| Sequential 1,000 rows | 480.22 us | 508.49 us | 1,585 B | 1,615 B |
| Sequential 1 MiB `bytea` | 2.236 ms | 2.291 ms | 1,585 B | 8,906 B |

The concurrency allocation capture records BlueTusk below Npgsql for fresh and
reused multiplexing and for both ordinary pooled controls. Current latency
decisions for those paths use the later alternating capture in the first table;
the ordinary controls are included specifically so multiplexing cannot appear
fast merely because a comparable saturated pooled path was omitted.

## What changed

The V1 candidate removes work from measured hot paths without changing the
public ADO.NET contract:

- saturated pool returns may complete a waiting checkout inline, avoiding one
  ThreadPool dispatch per hand-off while preserving bounded channel ownership;
- typed data-source commands avoid redundant base-class dispatch and casting;
- prepared commands cache their structural plan, stable built-in parameter
  encodings, result description and resolved scalar codec;
- mutable buffers and custom parameter types are still re-encoded, with live
  correctness coverage proving changed `byte[]` values are observed;
- prepared-command timeouts reuse timer state and close the stop-versus-callback
  race with an execution barrier;
- scalar execution uses a synchronous session-lock fast path and avoids an
  unnecessary `ValueTask`-to-`Task` adapter;
- portal payload continuations use pooled builders and keep the existing read
  lease instead of acquiring a redundant nested lease;
- row descriptions and resolved codecs are reused when PostgreSQL returns the
  same result shape; and
- the data reader uses one atomic closed/lifetime state, making disposal safer
  under competing callers and removing the final sequential-reader allocation
  loss.

Experimental read-ahead and stream-buffer variants that made the large-value
path slower were rejected and are not part of the candidate.

## Method

### Alternating-provider latency evidence

Each workload runs five trials of 501 alternating BlueTusk/Npgsql blocks. The
provider that starts a trial alternates, and provider order reverses between
blocks. The gate computes a mean, P95 and P99 ratio for every trial and makes its
decision from the median trial ratio.

The direct-provider block sizes are chosen to keep a block long enough to measure
while limiting drift:

| Workload | Operations per block |
|---|---:|
| Warm checkout | 256 |
| Parameterized scalar | 32 |
| Prepared scalar | 64 |
| Sequential 1,000 rows | 16 |
| Sequential 1 MiB `bytea` | 4 |

The concurrency capture runs four bursts per block and 64 operations per burst.
It covers fresh and reused multiplexed commands plus fresh and reused ordinary
pooled commands for both providers.

### Absolute latency and allocation evidence

BenchmarkDotNet 0.15.8 MediumRun uses two launches, ten warm-up iterations and
15 measurement iterations. MemoryDiagnoser reports managed bytes per completed
operation. Both providers use long-lived data sources, warm physical pools, the
same SQL, the same PostgreSQL process and the same command ownership shape.

Prepared commands use `CommandTimeout = 0` for both providers so the comparison
isolates prepared execution rather than comparing different timer
implementations. Production timeout behavior remains covered by the normal test
suite. The 1 MiB stream path uses a 128 KiB caller buffer for both providers.

## Environment

| Item | Value |
|---|---|
| Host | AMD Ryzen 7 5800X, 8 physical / 16 logical cores |
| OS | Windows 11 25H2, build 10.0.26200.9168 |
| Runtime | .NET 10.0.11, X64 RyuJIT x86-64-v3 |
| SDK | .NET SDK 10.0.303 |
| Database | PostgreSQL 19 Beta 3 Alpine, loopback TCP on port 5419 |
| Database image | `postgres:19beta3-alpine@sha256:b1692e50613a21e61c424859f943b9e193ae73e5a8c68abd5382dfb235bf15fc` |
| Reference | Npgsql 10.0.3 |
| Build | Release |

PostgreSQL 19 is still pre-GA at the report date. The official project identifies
Beta 3 as the current preview and plans the major release for September 2026.
That release milestone is independent of this provider-relative performance
result.

## Evidence and integrity

The canonical reports are checked in beside a schema-2 manifest that binds the
source commit, environment, PostgreSQL image identity and SHA-256 values. The
exact-candidate workflow produces the same four report roles in a separate
downloadable artifact bound to the final candidate SHA.

| Evidence | Repository path | SHA-256 |
|---|---|---|
| Five direct alternating-provider workloads | `benchmarks/baselines/windows-ryzen7-5800x-dotnet10/provider-paired-evidence.json` | `6CD2904293AFE8D3775BB0CFEEF646D3473DE69E3600068633C620FD3FABD20A` |
| Four alternating concurrency workloads | `benchmarks/baselines/windows-ryzen7-5800x-dotnet10/multiplexing-paired-evidence.json` | `84F1BB3C91C3D7E724E1B34EFAAFA09354DEF66AEA48F94803017324D203EBE0` |
| Final direct-provider MediumRun | `benchmarks/baselines/windows-ryzen7-5800x-dotnet10/results/BlueTusk.Benchmarks.ProviderComparisonBenchmarks-report-brief.json` | `9EBFA21E448C9EAEC1A98155339BB465BD724FAFB8BB0CFA599A2983EC8BBD12` |
| Concurrency MediumRun allocation capture | `benchmarks/baselines/windows-ryzen7-5800x-dotnet10/results/BlueTusk.Benchmarks.MultiplexingComparisonBenchmarks-report-full.json` | `51C4CA87489D3955AC79043AD889A1287A798984F44BC1EC5CA48389D7CF78ED` |

The repository verifies report shape, workload identity, sample counts, provider
order, finite positive values and every ratio before accepting an artifact. Its
self-tests mutate each protected dimension and prove that the verifier fails
closed.

## Release interpretation

The V1 Npgsql-comparison performance gate is green for this source tree and
reference environment. Performance no longer contains a known Npgsql loss in
the measured V1 matrix, including saturated ordinary pooling.

This result does not waive the remaining release controls. Stable publication
still requires the exact candidate workflows, supply-chain evidence, independent
approval and the project's production/endurance evidence. PostgreSQL 19 GA is
explicitly deferred by the current release decision; the product must continue
to identify PostgreSQL 19 support as preview until the official GA milestone is
verified.

No coverage-guided fuzzing payload was executed while producing or publishing
this report. Automatically queued PR runs were cancelled under the release
constraint and were not retried.
