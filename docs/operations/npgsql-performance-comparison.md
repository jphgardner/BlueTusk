# BlueTusk versus Npgsql: V1 performance report

**Report date:** 23 August 2026

**Performance source:** `d09d2f654f6c5568fa1053d92aad819872afa348`

**Reference provider:** Npgsql 10.0.3

**Database:** PostgreSQL 19 Beta 3

**Verdict:** pass for all nine named provider and concurrency comparisons

## Executive result

BlueTusk records lower mean, P95 and P99 latency than Npgsql in every workload in
the V1 comparison programme. It also allocates less managed memory in every
comparison. The result covers five direct provider operations and four saturated
64-operation concurrency shapes.

The former saturated non-multiplexed pooling weakness is closed. BlueTusk now
reduces mean latency by 5.17% for fresh ordinary pooled bursts and by 5.05% when
commands are reused. It also reduces allocation by 24.84% and 28.30%
respectively. Multiplexed bursts lead by 17.01% to 17.71% at the mean.

This is a bounded, reproducible V1 result. It does not claim that BlueTusk beats
Npgsql for every SQL statement, schema, payload, server, network or concurrency
level. Capacity planning still requires an application-specific benchmark.

## Results at a glance

Lower latency and allocation are better. Latency columns show BlueTusk's
reduction relative to Npgsql from alternating-provider evidence. Throughput is
the inverse of mean latency for these fixed-operation workloads.

| Workload | Mean latency reduction | P95 reduction | P99 reduction | Throughput uplift | Managed allocation reduction |
|---|---:|---:|---:|---:|---:|
| Warm pool checkout | **8.99%** | **36.91%** | **38.07%** | **9.88%** | **8.70%** (168 B vs 184 B) |
| Parameterized scalar | **9.72%** | **10.10%** | **11.55%** | **10.76%** | **22.80%** (1,652 B vs 2,140 B) |
| Prepared scalar | **1.66%** | **2.28%** | **0.16%** | **1.69%** | **30.10%** (785 B vs 1,123 B) |
| Sequential 1,000 rows | **8.23%** | **8.04%** | **10.73%** | **8.97%** | **23.84%** (1,195 B vs 1,569 B) |
| Sequential 1 MiB `bytea` | **1.20%** | **3.00%** | **3.04%** | **1.21%** | **83.77%** (1,466 B vs 9,031 B) |
| Fresh multiplexed burst | **17.01%** | **15.53%** | **12.37%** | **20.49%** | **17.78%** (1,429 B vs 1,738 B) |
| Reused multiplexed burst | **17.71%** | **13.33%** | **12.37%** | **21.52%** | **21.66%** (622 B vs 794 B) |
| Fresh ordinary pooled burst | **5.17%** | **3.42%** | **1.59%** | **5.45%** | **24.84%** (2,127 B vs 2,830 B) |
| Reused ordinary pooled burst | **5.05%** | **4.26%** | **4.53%** | **5.32%** | **28.30%** (1,343 B vs 1,873 B) |

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
| Warm pool checkout | 213.81 ns | 235.22 ns | 168 B | 184 B |
| Parameterized scalar | 297.69 us | 328.19 us | 1,652 B | 2,140 B |
| Prepared scalar | 291.52 us | 297.47 us | 785 B | 1,123 B |
| Sequential 1,000 rows | 487.35 us | 528.43 us | 1,195 B | 1,569 B |
| Sequential 1 MiB `bytea` | 2.183 ms | 2.223 ms | 1,466 B | 9,031 B |

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
- completed synchronous and asynchronous portals return their last row object
  to the session reuse slot instead of losing it at end-of-stream;
- disabled duration/message-size instruments bypass timestamps and recorder
  calls, while enabled command instrumentation retains its full diagnostics;
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
| Five direct alternating-provider workloads | `benchmarks/baselines/windows-ryzen7-5800x-dotnet10/provider-paired-evidence.json` | `7CAFAC4BD636583D0B204AC0E8507F72A98AAB77618F7653371EDD8F2EC823F6` |
| Four alternating concurrency workloads | `benchmarks/baselines/windows-ryzen7-5800x-dotnet10/multiplexing-paired-evidence.json` | `0ECED596A7CFFEBA7938C24E920162CBF48C5DA657381CAD990AE8AA4E3D3EDA` |
| Final direct-provider MediumRun | `benchmarks/baselines/windows-ryzen7-5800x-dotnet10/results/BlueTusk.Benchmarks.ProviderComparisonBenchmarks-report-brief.json` | `AC5259C51CEAF1E61D7F95776999AC3F9BCB338C8B52487ACB910A8A034F647B` |
| Concurrency MediumRun allocation capture | `benchmarks/baselines/windows-ryzen7-5800x-dotnet10/results/BlueTusk.Benchmarks.MultiplexingComparisonBenchmarks-report-full.json` | `475FE658AE5EDD920BC09D4B8404DDA72E0ECE84B14CFDE7114C323187306CFA` |

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
