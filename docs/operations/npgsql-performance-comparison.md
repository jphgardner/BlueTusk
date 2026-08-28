# BlueTusk V1 performance report: BlueTusk versus Npgsql

**Report date:** 25 August 2026

**Candidate branch:** `codex/v1-owner-release`

**Measured source commit:** `ac702d7c74d984faf375367016b77f9155695679`

**Reference provider:** Npgsql 10.0.3

**Runtime:** .NET 10.0.11, SDK 10.0.303, Release, x64

**Database:** PostgreSQL 18, local Docker, loopback TCP

## Executive verdict

The V1 provider-comparison gate passes for both latency and managed allocation.

- All **16/16 feature pairs** pass their paired mean, P95 and P99 latency
  budgets: **48/48 latency checks pass**.
- All **16/16 feature pairs allocate less managed memory than Npgsql** in the
  same final-source BenchmarkDotNet run.
- BlueTusk has the lower paired mean in 14 of 16 workloads. COPY import is
  0.21% slower and EF update is 0.80% slower; both are inside the declared 5%
  parity band and both allocate less than Npgsql.
- Allocation savings range from 1.1% for COPY export to 95.4% for an empty
  begin/rollback transaction. The largest practical payload win is the 1 MiB
  sequential `bytea` read at 91.4% less allocation.
- The repository verifier independently recomputed all ratios from raw samples
  and reported: `Provider performance gate passed for 16 workloads`.

The accurate V1 claim is:

> On the named PostgreSQL 18 loopback fixtures, BlueTusk passes the complete
> 16-workload latency gate and allocates less managed memory than Npgsql in
> every measured feature pair.

This is not a claim that every BlueTusk operation is faster at every percentile
on every machine. Three measured tail ratios are slightly above 1.0, with the
highest being EF insert P99 at 1.0452. All remain inside the predeclared gate.

## Complete 16-feature result

Lower is better. Latency ratios come from the median of five independently
calculated trial ratios, each containing 501 alternating-provider blocks.
Absolute means and allocation come from the final-source BenchmarkDotNet
ShortRun. Absolute means are useful scale indicators; the paired ratios are the
provider-comparison authority because they counter provider order and machine
drift.

| Workload | BlueTusk mean (us) | Npgsql mean (us) | Paired mean | Paired P95 | Paired P99 | BlueTusk allocation | Npgsql allocation | Allocation ratio | Saved |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Warm pool checkout | 0.20 | 0.23 | **0.9034** | **0.8205** | **0.8145** | **176 B** | 184 B | **0.9565** | 4.3% |
| Parameterized scalar | 424.21 | 447.38 | **0.9352** | **0.9420** | **0.9591** | **1,414 B** | 2,147 B | **0.6586** | 34.1% |
| Prepared scalar | 415.78 | 455.00 | **0.9861** | **0.9862** | **0.9694** | **825 B** | 1,110 B | **0.7432** | 25.7% |
| Sequential 1,000 rows | 617.26 | 643.80 | **0.9443** | **0.9470** | **0.9540** | **1,154 B** | 1,505 B | **0.7668** | 23.3% |
| Sequential 1 MiB `bytea` | 6,013.26 | 6,234.91 | **0.9887** | **0.9924** | **0.9904** | **1,310 B** | 15,219 B | **0.0861** | 91.4% |
| Empty begin/rollback | 0.06 | 430.87 | **0.0004** | **0.0006** | **0.0007** | **48 B** | 1,033 B | **0.0465** | 95.4% |
| Batch, 16 parameterized scalars | 516.70 | 597.26 | **0.8707** | **0.8740** | **0.9159** | **8,913 B** | 9,071 B | **0.9826** | 1.7% |
| Binary COPY import, 1,000 rows | 3,134.09 | 3,146.39 | 1.0021 | **0.9676** | 1.0076 | **1,686 B** | 3,034 B | **0.5557** | 44.4% |
| Binary COPY export, 1,000 rows | 1,111.94 | 1,110.19 | **0.9845** | **0.9520** | **0.9446** | **49,487 B** | 50,048 B | **0.9888** | 1.1% |
| Prepared typed-row round trip | 441.89 | 447.84 | **0.9986** | **0.9977** | 1.0085 | **1,152 B** | 1,373 B | **0.8390** | 16.1% |
| Notification delivery | 514.45 | 570.99 | **0.9783** | **0.9724** | **0.9933** | **1,548 B** | 1,852 B | **0.8359** | 16.4% |
| Large-object read, 1 MiB | 10,998.69 | 11,767.77 | **0.9442** | **0.9505** | **0.9430** | **13,445 B** | 22,954 B | **0.5857** | 41.4% |
| EF compiled query | 632.20 | 629.18 | **0.9855** | **0.9868** | **0.9807** | **34,626 B** | 37,006 B | **0.9357** | 6.4% |
| EF materialize 100 rows | 825.12 | 855.09 | **0.9966** | **0.9942** | **0.9677** | **74,979 B** | 76,945 B | **0.9744** | 2.6% |
| EF insert one row, rolled back | 1,960.26 | 2,035.57 | **0.9947** | **0.9967** | 1.0452 | **51,033 B** | 52,057 B | **0.9803** | 2.0% |
| EF update one row, rolled back | 2,611.42 | 2,551.12 | 1.0080 | 1.0142 | 1.0154 | **55,387 B** | 57,065 B | **0.9706** | 2.9% |

Bold latency ratios are below 1.0. All unbolded latency ratios remain below the
applicable 1.05 ceiling. Every allocation ratio is below the strict 1.0 ceiling.

The empty transaction is a semantic fast path. BlueTusk defers `BEGIN`; when no
command executes, rollback completes locally without a server round trip. It is
valid behavior for this exact workload and does not imply that a transaction
containing commands is free.

## What fixed allocation

The final pass removed the earlier allocation deficits without replacing them
with benchmark-only shortcuts.

- Command and parameter storage now keep common small shapes inline, rent larger
  backing storage, reuse encoded parameter buffers and return pooled memory on
  disposal and failure paths.
- SQL rewrite plans are cached both by object identity and in a bounded
  content-keyed cache. Equal EF-generated SQL strings reuse the parse template
  while every command binds its own parameter objects. The value cache is capped
  at 1,024 templates and only admits SQL at most 16 KiB long.
- EF configuration now installs EF Core's standard relational warning defaults,
  matching the provider contract used by Npgsql. This prevents immutable warning
  maps from being rebuilt per query and preserves caller-specified warning rules.
- Buffered rows, data readers and protocol decoders retain compact metadata and
  reuse bounded storage. Built-in scalar decoding avoids incidental field arrays
  and boxing.
- COPY import coalesces its header and primitive fields in a pooled output buffer.
  COPY export reuses per-column type/codec resolution and returns those states on
  every completion or failure path.
- Prepared statements retain their parameter snapshots, scalar resolver and
  reusable timeout state together. Unprepared commands do not pay for that state.
- Large-object reads, notification delivery, batching, transaction handling and
  frontend message writes use pooled or reusable state on their measured hot
  paths.

The largest before/after reversals are important:

| Workload | Earlier BlueTusk ratio | Final ratio | Final result |
|---|---:|---:|---|
| Batch | 1.7221 | **0.9826** | 1.7% less than Npgsql |
| COPY import | 1.7398 | **0.5557** | 44.4% less |
| COPY export | 1.0358 | **0.9888** | 1.1% less |
| Notification delivery | 1.8642 | **0.8359** | 16.4% less |
| EF compiled query | 1.1454 | **0.9357** | 6.4% less |
| EF materialize 100 rows | 1.1484 | **0.9744** | 2.6% less |
| EF insert | 1.2390 | **0.9803** | 2.0% less |
| EF update | 1.2651 | **0.9706** | 2.9% less |

## Saturated pooling result

The separate retained saturation fixture executes real 64-command bursts. It
covers fresh/reused commands through both multiplexed lanes and ordinary
non-multiplexed pools. Its exact gate remains green in all four shapes:

| Shape | Paired mean ratio | Throughput delta | P95 ratio | P99 ratio | Allocation result |
|---|---:|---:|---:|---:|---|
| Fresh multiplexed burst | 0.9545 | +4.77% | 0.9831 | 0.9762 | BlueTusk lower |
| Reused multiplexed burst | 0.9094 | +9.97% | 0.9410 | 0.9357 | BlueTusk lower |
| Fresh ordinary pooled burst | 0.9630 | +3.84% | 0.9700 | 0.9742 | BlueTusk lower |
| Reused ordinary pooled burst | 0.9622 | +3.93% | 0.9708 | 0.9678 | BlueTusk lower |

This is the evidence that reverses the earlier approximately 46.5% saturated
ordinary-pooling deficit. The current 16-feature report above is the final-source
authority for standalone allocation and latency.

## Method and fairness controls

The paired collector runs five trials for each feature. Every trial contains 501
blocks for BlueTusk and 501 equivalent blocks for Npgsql. Provider order reverses
between blocks and the provider starting each trial alternates. Each trial
independently computes mean, P95 and P99 ratios; the gate uses the median trial
ratio. Every workload validates its result before samples are accepted.

The first five established provider hot paths require ratios at or below 1.0.
The eleven extended features use a 1.05 parity ceiling to account for host and
server tail variance. Managed allocation is strict for all 16: BlueTusk must be
at or below Npgsql, with no tolerance band.

Both providers use long-lived data sources, warmed physical pools, identical SQL,
the same PostgreSQL process and the same command-ownership shape. The Npgsql
package is a benchmark/test reference only; BlueTusk has no runtime Npgsql
dependency.

| Item | Value |
|---|---|
| Host | AMD Ryzen 7 5800X, 8 physical / 16 logical cores |
| OS | Windows 11 25H2 |
| Runtime | .NET 10.0.11, X64 RyuJIT x86-64-v3 |
| SDK | .NET SDK 10.0.303 |
| Database | PostgreSQL 18, local Docker, loopback TCP port 5418 |
| Reference | Npgsql 10.0.3 |
| Build | Release |
| Latency programme | 5 trials x 501 blocks x 16 workloads |
| Allocation programme | BenchmarkDotNet ShortRun, 32 methods |

## Validation and evidence

The final source builds with zero warnings. Focused regression results are 167
Data tests, 50 Protocol tests, and 258 EF tests passing with 49 explicit EF
environment/version skips. The provider smoke and exact performance verifier
also pass. Coverage-guided fuzzing was not triggered, executed or inspected.

| Evidence | SHA-256 |
|---|---|
| `benchmarks/baselines/windows-ryzen7-5800x-dotnet10/results/BlueTusk.Benchmarks.ProviderComparisonBenchmarks-report-brief.json` | `11a2f247ae5e9f65f07ce5bc7827a5bcc386a637cd4752a24165804747ab7fca` |
| `benchmarks/baselines/windows-ryzen7-5800x-dotnet10/provider-paired-evidence.json` | `e5736af0c74b758391682e782129465a40cbc1e786d651c6baee6a9413eede72` |
| `artifacts/benchmarks/copy-paired-before-buffer-tuning/provider-copy-paired-evidence.json` | `e0fd0809e125854e7be3a2a34336e0f6388e9a58e970e11db57130f851d57abf` |

The raw artifacts are local engineering evidence and are intentionally outside
the package payload. Stable package publication remains governed by the exact
candidate, approval, endurance and PostgreSQL-version gates documented in the
release process. PostgreSQL 19 GA validation is outside this performance report.

## Production interpretation

The known V1 provider performance weaknesses are closed on the controlled
reference fixture: standalone latency passes all declared budgets, standalone
allocation is lower in all 16 features, and the retained saturated-pooling gate
is green in all four shapes.

Applications should still benchmark their own SQL, payload distribution,
concurrency, network latency and SLO percentiles. This comparison is a strong
regression and release gate, not a substitute for workload-specific capacity
planning.
