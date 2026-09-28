# Jobs and Workflows capacity and recovery harness

`benchmarks/BlueTusk.Workflows.LoadHarness` is a repeatable executable that
measures the durable Jobs runtime and six-node Workflows DAG on a disposable
PostgreSQL database. It verifies every admitted item reaches durable success,
every tenant is delivered its own payload, there is one ledger effect per item,
all workflow histories replay to the persisted state, both health inspections
report a drained scope, and bounded pruning removes all expected terminal rows.
Measurement failure exits nonzero. This is local engineering evidence; the
production qualification gates remain open.

## Run

Use a disposable database. The connection environment variable is inherited by
the child process and is never included in command arguments or reports. Each
scenario creates unique schemas and drops them in a `finally` block. The
process-kill test kills only the child process created by this harness. The
wire-fault proxy binds an ephemeral loopback port, accepts one faulted connection
and one healthy pool replacement,
forwards at most one MiB per protocol frame, and expires after 30 seconds. It
requires `SSL Mode=Disable;Channel Binding=Disable` for this local fault test.

```powershell
$env:BLUETUSK_TEST_CONNECTION_STRING = 'Host=127.0.0.1;Port=55418;Username=postgres;Password=postgres;Database=bluetusk_ecosystem;SSL Mode=Disable;Channel Binding=Disable'
./eng/jobs-workflows-load.ps1 -Profile quick -Output docs/jobs/performance-reports/quick.json
./eng/jobs-workflows-load.ps1 -Profile matrix -Output docs/jobs/performance-reports/matrix.json
./eng/jobs-workflows-load.ps1 -Profile soak -Seconds 300 -Output docs/jobs/performance-reports/soak.json
./eng/jobs-workflows-load.ps1 -Profile faults -Output docs/jobs/performance-reports/faults.json
./eng/jobs-workflows-load.ps1 -Profile qualification -Seconds 120 -Output docs/jobs/performance-reports/overload.json
./eng/jobs-postgresql-compatibility.ps1 -Versions 15,16,17
```

Run from the repository root when invoking the executable directly. Build with
`dotnet build ... -c Release -nr:false`, then use `dotnet run --project ... -c
Release --no-build -- matrix`. `-nr:false` is a build option. The wrapper uses
the appropriate build and application argument boundaries.

## Workloads and bounds

The quick profile drains 512 Jobs with 1 KiB of padding beside 4,000 terminal
rows, 64 Workflows with two parallel activities, join, buffered signal, 10 ms
timer and transactional effect, then a three-second Jobs admission loop. The
matrix varies Jobs padding through 64 B, 4 KiB and 64 KiB and per-tenant
concurrency through two and eight; it uses four tenants and 10,000 retained
terminal rows. It also runs a ten-to-one hot-tenant allocation and a pool of 12
connections under 32 potential concurrent handlers. Workflows vary 256 B and
4 KiB padding with the same concurrency sweep. The matrix finishes with a
ten-second admission loop. The soak profile runs the admission loop for 30
seconds by default; `-Seconds` accepts 1–3,600 seconds.

Padding is repeated `x` characters, so it is compressible PostgreSQL bytea/TOAST
data. The payload sweep exercises transferred/materialized bytes and allocation,
but its WAL/storage figures do not represent incompressible business payloads.

Each tenant has its own scoped worker and explicit concurrency allocation.
Tenant completion counts and latency distributions expose the observed
consequences of that allocation. This is not a claim of scheduler fairness
across arbitrary worker allocations or tenant service-level objectives.

The endurance producer has a 256-item outstanding admission window, returns a
slot only after observing durable success, and stops at 200,000 admissions even
if its time budget remains. Finite backlog producers use four enqueue tasks.
Maximum load configuration bounds are 64 tenants, 64 handlers per tenant,
64 KiB padding and a 1,024-item admission window. Scenario deadlines, task
cancellation and awaited worker/collector lifetimes prevent orphaned work.
Latency samples are bounded by 200,000 admitted items; handler samples are
bounded by admitted items, DAG activity count and configured retry attempts.

## Measurements

The JSON contains actual generated payload size, producer time, drain time,
durable completion throughput, nearest-rank P50/P95/P99 and extrema, per-tenant
completion distributions, callback duration, allocation bytes, generation
collection counts, peak managed bytes and process working set. Every 100 ms
it samples pool total/busy/waiters and PostgreSQL application backends, lock
waiters and other waiters. It also records emitted Jobs/Workflows metric
measurement counts, sums and maxima without retaining labels or payloads.
Duration metric sums/maxima use the instrument's seconds unit. Callback duration
and durable latency use milliseconds.

Durable latency is PostgreSQL `completed_at - created_at`, so it includes backlog
enqueue time and measures commit-visible lifecycle timestamps. Finite backlog
throughput uses worker drain time after admission; endurance throughput includes
admission plus final drain. These are deliberately different workload modes.
The handler sample records callback work; it does not include the full claim,
workflow transition or acknowledgement path. Warmups are excluded from reports.

WAL position, transaction/tuple/cache/deadlock/temp-byte snapshots and owned
relation growth expose database behavior. WAL is cluster scoped and
`pg_stat_database` is database scoped; concurrent work can contribute and
asynchronous statistics flushing can lag. Schema relation bytes are scoped to
the scenario. Sampling can miss brief waits and peaks. Polling, metrics,
the admission collector, a ledger insert, generated JSON, and workload checks
add overhead. Use repeated isolated runs on a frozen candidate to establish
capacity; do not compare these figures to other products as a benchmark claim.

Each report records OS, .NET, PostgreSQL version, architecture, logical processor
count and a SHA-256 of Jobs, Workflows and harness source/project files at the
start. That fingerprint excludes provider dependencies and is not an immutable
release identity. Retain the exact commit, dependency lock files, build output,
machine/storage specification and PostgreSQL settings for qualification runs.

## Injected failures

The process-death test starts a real child worker with a one-second lease. The
child commits a fenced ledger effect and waits before acknowledging its job.
The parent observes that effect, kills the child, waits for actual database-clock
lease expiry, reclaims with a newer fence, repeats the idempotent insert, and
completes the second attempt. The old capability is rejected and the ledger has
one effect. This demonstrates an explicit idempotent effect under replay;
external effects are still at least once.

The Workflows process-death test kills a real child process after an idempotent
effect commits but before the activity result persists. A fresh store/worker
reclaims the second attempt with a newer fence, commits the result, resumes the
downstream timer and reaches success. Its durable history must replay to the
final state and the repeated idempotent ledger insert must remain singular.

The ambiguous-COMMIT test performs ledger insertion and caller-transaction
enqueue through a bounded protocol proxy. The proxy observes PostgreSQL's
`CommandComplete(COMMIT)` after server execution and drops the acknowledgement.
The client observes failure, the provider retires the physical session and closes
the connection before normal asynchronous transaction disposal, and a replacement
from the same max-size-one pool must answer a query. The client then resolves the
outcome through a direct connection and the original deduplication identity.
The durable ledger row and original pending job must both exist; retry must
return that original job. Recovery times measure resolution, not a network
availability objective.

## Evidence and remaining gates

Checked-in reports under `performance-reports` are dated local samples, not
production approval. The live regression suites independently cover Jobs leases,
heartbeats, stale completions, crashed final attempts, transaction deadlines,
retention and a larger indexed backlog; Workflows cover fenced state/effect
transactions, recovery cursor progress, history replay, compensation, migration
and bounded reads. The Windows x64 NativeAOT smoke exercises both products,
source/worker restart, signal, timer and compensation.

The 27 September 2026 [matrix report](performance-reports/local-matrix.json)
ran on Windows x64, .NET 10.0.12 and PostgreSQL 18.6 with 16 host logical
processors. All 13 scenarios passed their correctness checks. The table records
each scenario, including the slower high-concurrency small-payload outlier.
The host and database were shared with other development activity; these are
single-run observations with substantial variance, not established capacities.

| Scenario | Completed/s | Durable P50 ms | P95 ms | P99 ms |
| --- | ---: | ---: | ---: | ---: |
| Jobs 64 B, concurrency 2/tenant | 382.4 | 1,557.5 | 2,475.6 | 2,550.1 |
| Jobs 64 B, concurrency 8/tenant | 108.8 | 9,017.7 | 9,561.6 | 9,583.8 |
| Jobs 4 KiB, concurrency 2/tenant | 383.9 | 1,628.9 | 2,415.5 | 2,535.5 |
| Jobs 4 KiB, concurrency 8/tenant | 887.5 | 888.4 | 1,064.2 | 1,095.6 |
| Jobs 64 KiB, concurrency 2/tenant | 368.9 | 1,729.6 | 2,545.0 | 2,622.5 |
| Jobs 64 KiB, concurrency 8/tenant | 674.6 | 1,308.1 | 1,417.9 | 1,452.2 |
| Jobs hot tenant, concurrency 4/tenant | 193.7 | 4,743.0 | 9,650.9 | 10,172.3 |
| Jobs pool 12, concurrency 8/tenant | 708.8 | 1,043.2 | 1,319.3 | 1,357.0 |
| Workflows 256 B, concurrency 2/tenant | 52.4 | 2,698.1 | 2,822.8 | 2,855.7 |
| Workflows 256 B, concurrency 8/tenant | 83.8 | 1,661.0 | 1,887.4 | 1,949.5 |
| Workflows 4 KiB, concurrency 2/tenant | 56.2 | 2,365.8 | 2,445.7 | 2,470.4 |
| Workflows 4 KiB, concurrency 8/tenant | 89.7 | 1,658.4 | 1,862.9 | 1,913.0 |
| Jobs admission loop, 10 seconds plus drain | 231.6 | 237.8 | 1,888.9 | 2,035.1 |

Process RSS samples ranged from 100.3–126.5 MiB in this matrix. The 64 KiB Jobs
cases allocated 424.8–430.3 MiB per 1,000 items and observed 19–25 generation-two
collections, so large payload allocation deserves further profiling. The pool-12
scenario sampled 19 waiting clients. Workflow scenarios sampled up to nine lock
waiters. Cluster WAL advances ranged from 2.9–16.0 MiB per scenario, subject to
the scope and compression limitations above; the raw JSON includes collection,
relation-growth and database-counter detail.

In the hot-tenant case, all 1,540 hot-tenant items and 153–154 items from each
other tenant completed. Hot-tenant P99 was 10.2 seconds; other-tenant P99 was
1.34–1.36 seconds under equal worker allocations. This demonstrates observed
separation for this scenario, not a universal fairness guarantee.

The [fault report](performance-reports/local-faults.json) also passes actual Jobs
and Workflows process death and ambiguous COMMIT with normal provider cleanup.
Each killed activity was recovered on attempt two with one durable effect;
the COMMIT test retained the original deduplication identity. Individual recovery
timings are in the report and should not be interpreted as availability promises.

The final [30-second admission report](performance-reports/local-soak-30s.json)
completed 7,371 Jobs and matching effects at 239.7/s including final drain, with
durable P50/P95/P99 of 737.5/2,407.1/2,974.9 ms. It sampled peak RSS 104.4 MiB,
managed memory 23.6 MiB, 452.2 MiB total allocated, 31/5/2 generation collections,
15 pool connections and zero pool/lock waiters. All 17,371 terminal rows
(including 10,000 initial retained rows) were pruned; all three fault scenarios
also passed. Thirty seconds is a smoke endurance sample, not a sustained
production soak or memory-leak exclusion test.

## Offered overload and retention

The `qualification` profile runs Jobs and Workflows independently for 120 seconds
each by default. Every tenant has four workers and 32 reserved admission slots;
there are four tenants, a 32-connection pool and a hard total of 20,000 admitted
items per product. The hot producer offers a new item on a one-millisecond timer;
each cold producer offers a Job every 100 ms or a Workflow every 500 ms. Producer
timer ticks can coalesce while enqueue awaits I/O, so the report counts actual
accepted and rejected offers rather than claiming a fixed achieved arrival rate.
Activity callbacks deliberately take 100 ms. Exhausted admission slots reject
an offer immediately without creating durable work. This admission policy belongs
to the harness/application, not an automatic queue quota in the Jobs store.

Separate tenant reservations prevent a hot producer from consuming a cold
tenant's ingress slots. The report contains exact admission high water, completion
count, rejection count, latency distribution and maximum gap between completions
for every tenant. These quantify the tested allocation and cold-tenant progress.
They do not establish fairness under different reservations, worker counts,
shared connection pressure or application payloads.

A maintenance task prunes terminal work older than two seconds with at most 128
rows per tenant/product per sweep, about once a second. Workflows also prune their
underlying Jobs independently. Every second, the harness samples retained primary
rows, active rows, Jobs attempts and physical runtime relation bytes. Final drain
and pruning must leave zero primary rows, Jobs and attempt history; all expected
business effects and completion measurements must remain.

Completion latency is copied by an ephemeral database trigger in the same
transaction as successful state transition. This lets measurement survive runtime
retention without holding admission slots forever for an already pruned job.
The trigger, durable measurement audit, business-effect ledger and collector add
work, and those two audit tables are excluded from the runtime storage series.
They are bounded by the 20,000-item admission limit and are removed with the
scenario schemas. PostgreSQL deletion does not shrink heap/index files; assess
row retention and physical page reuse separately, including autovacuum behavior.

The [PostgreSQL version report](performance-reports/postgresql-15-17-tests.json)
records real PostgreSQL 15/16/17 fixture versions and 34 Jobs plus 23 Workflows
passes per release, with no skips. `eng/jobs-postgresql-compatibility.ps1` reads
credentials from the explicitly owned containers, restores the environment on
completion and writes unique TRX result directories. A recurring test fixture was
placed inside its interval to avoid assuming synchronized host/database clocks.

The [two-minute report](performance-reports/local-overload-120s.json) passed both
campaigns and all five fault scenarios. Jobs admitted 7,308 items and rejected
18,634 excess hot-tenant offers; Workflows admitted 1,949 and rejected 14,078.
Each cold tenant admitted 1,199 Jobs or 239 Workflows with zero rejections. Peak
outstanding admissions were 51 and 41, within the total limit of 128. Maximum
gaps between cold-tenant completions were 308 ms and 1,239 ms respectively.

| Observation | Jobs | Workflows |
| --- | ---: | ---: |
| Completions/s, including drain | 60.4 | 16.1 |
| Hot-tenant durable P99 | 2.042 s | 5.467 s |
| Worst cold-tenant durable P99 | 0.378 s | 1.256 s |
| Peak retained primary rows | 167 | 78 |
| Peak retained dispatch Jobs | 167 | 217 |
| Primary rows pruned | 7,308 | 1,949 |
| Dispatch Jobs pruned | 7,308 | 7,796 |
| Peak process RSS | 96.8 MiB | 106.5 MiB |
| Peak managed bytes | 22.4 MiB | 28.1 MiB |
| Total allocated | 617.9 MiB | 1,413.3 MiB |
| Generation 0/1/2 collections | 43/5/1 | 94/8/1 |
| Peak pool connections / busy | 21 / 17 | 23 / 19 |
| Peak sampled pool / lock waiters | 0 / 0 | 0 / 6 |
| Peak physical runtime relations | 7.93 MiB | 16.14 MiB |

All primary rows, dispatch Jobs and attempt history were zero after final
pruning. Physical files did not reach a plateau: a least-squares fit to nonzero
retained-row samples after 60 seconds shows approximately 1.49 MiB/minute for
Jobs and 7.28 MiB/minute for Workflows. This is a qualification gap despite
bounded live/retained row counts. Longer runs across repeated vacuum/checkpoint
cycles, dead-tuple measurements and index/page reuse analysis are required.
The [reference environment snapshot](performance-reports/local-reference-environment.json)
records host CPU/RAM, actual container image ID and selected PostgreSQL settings
after the run: fsync and synchronous commit on, autovacuum on with a 60-second
nap time, 128 MiB shared buffers, 4 MiB work memory and logical WAL. Other
development activity, including native compilation, shared this host.

## Reference operator thresholds

These are initial diagnostic thresholds for the exact two-minute campaign,
derived by rounding up twice the observed latency/gap/row-count peaks. They are
not production SLOs. Re-establish them on an isolated immutable candidate with
the application's payloads, arrivals, retention and callbacks. An application
must collect durable lifecycle latency itself; exported handler/activity
histograms describe attempts and do not measure total queue/DAG latency.

| Signal at the tested allocation | Jobs warning | Workflows warning |
| --- | ---: | ---: |
| Durable hot-tenant P99 | above 4.1 s | above 11 s |
| Durable cold-tenant P99 | above 0.8 s | above 2.6 s |
| Cold-tenant completion gap while work is offered | above 0.62 s | above 2.5 s |
| Retained primary rows with 2 s pruning | above 334 | above 156 |
| Retained dispatch Jobs with 2 s pruning | above 334 | above 434 |

Warn immediately on a cold-tenant admission rejection in this reserved-slot
scenario; hot-tenant rejection is expected overload feedback and must be visible
to the caller. Never acknowledge a rejected offer as enqueued. Maintain the
32-slot-per-tenant and 20,000-total campaign limits rather than increasing them
to hide overload. Inspect active queue counts with bounded `InspectAsync` calls;
terminal-row retention needs a separately bounded database count.

The 32-connection reference pool peaked at 19 busy connections. Investigate
sustained usage of 26 or more (80 percent rounded up), any persistent waiter,
or a stalled completion stream before raising pool limits. Store-failure counters
above zero require investigation; expected fault injection explicitly accounts
for them. With the tested one-second lease and prompt connection healing, an
expired lease that remains visible for more than two seconds after connectivity
returns should trigger investigation. Pending future timers/signals are normal;
do not treat oldest active workflow age alone as a stalled-work signal.

For storage, track dead tuples, autovacuum timestamps/counts and physical
relation bytes alongside retained rows. Investigate persistent page growth across
cleanup cycles even while row limits hold. For the same workload/window, growth
above twice the observed late slope (about 3 MiB/minute Jobs or 14.6 MiB/minute
Workflows) is a reference warning, not an allowed indefinite growth rate. Pruning
does not shrink files, and this campaign has not qualified a long-term storage
budget. Set hard database/disk budgets separately before production use.

## Network partition boundary

The [partition fault report](performance-reports/local-faults-partition.json)
adds Jobs and Workflows connection-cut/rejected-connect tests. A bounded loopback
gate accepts at most 128 sessions, eight concurrent relays and uses two 8 KiB
copy buffers per relay; tested workers use a pool capped at four. The gate closes
established connections and rejects replacements for at least 1.6 seconds after
the first handler is canceled, outlasting its one-second database lease. The
old owner cannot renew or complete. Healing the gate must produce a successful
second attempt with a newer fence and one durable effect; Workflows history must
replay. Store-failure telemetry, canceled attempts and rejected connections are
reported.

In the recorded local partition sample, Jobs recovered in 94.1 ms and Workflows
in 247.9 ms after healing, both with one canceled attempt. The gate observed 16/31
rejected connection attempts and peaks of one/two relays respectively. Those
timings describe prompt TCP closure and rejected reconnects; silent packet
blackholes, extended transport timeouts, failover, credential rotation and disk
pressure remain separate untested boundaries.

## Sustained physical storage campaign

Run `eng/jobs-storage-campaign.ps1 -Version 15 -Seconds 600` after reserving the
owned fixture from other qualification tasks. The `storage` harness profile
performs excluded Jobs/Workflows path warmups, then ten minutes of admitted load
per product. It retains the four-tenant overload allocation, 1 KiB compressible
padding, two-second terminal pruning and exact durable-effect assertions. Its
finite measurement/audit cap is 200,000 accepted items per product, and it fails
if that cap is reached before the duration completes. Definitions, audit and
effect tables are finite fixtures rather than an unlimited background producer.

The first 120 seconds observe default autovacuum. Thereafter a single maintenance
loop supplements it every 30 seconds with scoped ordinary vacuum/ANALYZE,
without manual tail truncation. Short smoke durations scale these two intervals
down and record the actual intervals in the report. Vacuum statements have
finite lock/statement deadlines and use a separate one-connection source, giving
a total tested main-plus-maintenance pool ceiling of 33. The separate source
prevents session maintenance settings from reaching the workers' pool. Ordinary
vacuum is part of the operator contract in [maintenance.md](maintenance.md),
with a [known-table psql script](../../eng/jobs-workflows-vacuum.sql).

Every roughly one-second raw storage sample records direct retained/live runtime
row counts, attempt rows, `pg_total_relation_size`, individual heap/index bytes,
estimated live/dead rows, manual/autovacuum and analyze counts/timestamps,
WAL insert LSN and statistics, timed/requested checkpoint counts and cumulative
write/sync milliseconds, process CPU, working set, managed memory, allocations,
GC counts and source-pool statistics. The 100 ms runtime probe also reports pool
and database lock-wait peaks and exported product instruments. Table totals
include their indexes/TOAST; heap and index columns are reported separately and
need not sum to the total. Measurements exclude the harness audit/effect tables.
Logical final pruning alone is never treated as a physical plateau.

The wrapper retains actual image/settings metadata and a roughly four-second
Docker CPU/memory/network/block-I/O series for the dedicated fixture. Docker CPU
percentage is relative to a single CPU and may exceed 100 percent. Docker block
I/O reporting can be unavailable inside the development VM; a zero reading is
not proof that PostgreSQL performed no disk I/O. Process CPU is accumulated
processor time, not host utilization. The development host remains shared with
other compilation/testing even when this PostgreSQL fixture is dedicated.

The combined JSON report includes all five recovery faults. Incremental
`*.Jobs.samples.jsonl` and `*.Workflows.samples.jsonl` sidecars retain complete
raw progress even if a later invariant fails; `*.fixture.jsonl` records fixture
observations and `*.environment.json` records the setup. Capture the terminal
output too when conducting release qualification. Reproduce the window and
relation-growth calculations with:

```powershell
python eng/jobs-storage-summary.py docs/jobs/performance-reports/local-storage-pg15-600s.json
```

The summary uses least-squares relation-byte slopes for the initial automatic
phase, maintenance settling, and the final half after at least two initial
observation periods. It also reports net/range growth and per-table heap/index
reuse, rather than using a fitted slope alone. Live/dead estimates and statistics
can lag; WAL/checkpoint counters remain cluster scoped. A stable finite window
establishes measured page reuse for this allocation, not an indefinite disk
bound or qualification of large/incompressible payloads and long retention.

## Durable-format rehearsal boundary

The added Jobs and Workflows durable-format suites rehearse format-one reopen
with operational tuning, then configuration rollback, preserving identity,
history/fencing and signal/timer continuation. Unsupported format markers and
durable admission fingerprints reject initialization without rewriting existing
state. These use the same candidate binary; there is no prior released durable
format or cross-format upgrade/downgrade tooling. The exact supported boundary
and remaining migration programme are in [durable-format.md](durable-format.md).

## Ten-minute physical storage result

The dedicated PostgreSQL **15.19** campaign completed 600 offered seconds per
product after excluded path warmups on the shared Windows development host.
The [raw combined report](performance-reports/local-storage-pg15-600s.json),
[computed summary](performance-reports/local-storage-pg15-600s.json.summary.json),
incremental per-product samples, fixture series and environment sidecars are
retained. fsync/synchronous commit and autovacuum were enabled. Every accepted
item produced exactly one idempotent durable effect in this fixture; all runtime
rows/attempts were pruned at the end. This is finite local evidence with collector,
trigger and maintenance overhead, not a production or indefinite-disk guarantee.

| Measured signal | Jobs | Workflows |
| --- | ---: | ---: |
| Accepted / exact durable effects | 36,989 / 36,989 | 10,052 / 10,052 |
| Rejected hot offers / rejected cold offers | 93,625 / 0 | 77,991 / 0 |
| Completions/s including final drain | 61.55 | 16.62 |
| Hot durable P50 / P95 / P99 | 0.925 / 1.245 / 2.104 s | 2.576 / 4.360 / 8.109 s |
| Worst cold durable P99 / completion gap | 0.399 / 0.489 s | 2.523 / 2.153 s |
| Maximum outstanding / configured admission slots | 58 / 128 | 50 / 128 |
| Peak retained primary / dispatch Jobs | 170 / 170 | 84 / 227 |
| Physical final-half range | 3.922–5.781 MiB | 8.742–10.523 MiB |
| Physical final-half fitted slope | -0.0075 MiB/min | +0.0189 MiB/min |
| Final-half endpoint net change | -0.359 MiB | +1.531 MiB |
| Observed WAL insert rate | 23.77 MiB/min | 55.24 MiB/min |
| Manual maintenance passes / table statements | 16 / 160 | 16 / 160 |
| Vacuum statement P99 / maximum | 37.35 / 39.12 ms | 59.01 / 74.86 ms |
| Timed / requested checkpoint count delta | 2 / 0 | 2 / 0 |
| Process CPU / average single-CPU percentage | 158.3 CPU-s / 26.42% | 270.8 CPU-s / 45.22% |
| Peak RSS / managed memory | 104.8 / 29.1 MiB | 119.6 / 32.5 MiB |
| Cumulative allocated / GC 0/1/2 | 3,296.4 MiB / 226/24/4 | 7,378.5 MiB / 488/47/4 |
| Peak pool total / busy / waiting | 23 / 20 / 0 | 27 / 23 / 0 |
| Peak sampled database lock waiters | 3 | 8 |

Before supplemental maintenance, the initial 120-second physical slopes were
3.18 and 6.19 MiB/min. The subsequent settling slopes were negative, then the
last five-minute windows remained within the ranges above. All measured runtime
indexes had exactly zero fitted growth in that final half; small heap oscillations
and autovacuum tail truncation account for changing totals. Workflows' positive
endpoint net change is retained explicitly: choosing different cleanup-cycle
endpoints can differ substantially from a fitted slope. This establishes page
reuse under the combined policy, without extrapolating a forever plateau.
Jobs/attempts observed ten autovacuum cycles each during Jobs load; the six
changing Jobs/Workflows tables observed nine each during Workflow load.

Checkpoint write/sync time deltas were 195,055/1,561 ms for Jobs and
266,599/77 ms for Workflows. These are cumulative background-checkpoint times,
not per-request latency or a directly measured disk throughput. The fixture CPU
series across both products had P50/P95/P99 94.78/164.05/181.58 percent of a
single CPU and a maximum of 188.56 percent. Docker reported zero block I/O in
this VM; actual block-I/O throughput is unqualified. WAL/statistics/checkpoint
observations remain cluster-scoped and may publish late. The sizeable allocation
churn, particularly Workflows, remains a profiling/optimization target despite
bounded retained managed memory and no sampled pool waiters.

All five recovery scenarios passed again: Jobs/Workflows process death recovered
in 931/1,204 ms, ambiguous COMMIT acknowledgement loss recovered in 14.7 ms,
and prompt connection-cut/rejected-connect healing recovered in 94.5/219.0 ms.
Each effect count was one; recovered worker leases had a newer fence and the
expected second attempt. This retains the previously documented fault boundary.

The scoped operator script passed Jobs-only and combined Jobs/Workflows target
checks, left a neighboring business-effect table untouched, rejected missing
targets before vacuum, and stopped a blocked vacuum at its two-second server
lock deadline (2.28 s observed including process/protocol overhead). The
[operator validation record](performance-reports/maintenance-script-checks.json)
binds these checks to the script hash. The final live regression ran Jobs **36**
and Workflows **26** tests on PostgreSQL **15.19, 16.15, 17.11 and 18.6**:
**248 passed, zero skipped**, with per-run TRX paths in the
[version rehearsal summary](performance-reports/postgresql-format-rehearsal-15-18-tests.json).

For this longer reference window, revise the initial diagnostic latency/row
thresholds to rounded-up twice-observed values: hot P99 **4.3/16.3 s**, cold P99
**0.8/5.1 s**, cold progress gap **1.0/4.4 s**, retained primary **340/168** and
retained dispatch Jobs **340/454**, respectively for Jobs/Workflows. A cold
rejection remains unexpected under the reserved allocation. Investigate physical
runtime storage above **12/22 MiB** persisting across two successful cleanup
passes at this same allocation, or a rising cycle-to-cycle trend despite
bounded rows and advancing vacuum counts. These are reference warnings, not
hard physical bounds; establish disk/WAL and fleet-wide admission limits
independently and remeasure real payloads, retention and immutable candidates.

Still required are long isolated endurance runs, overload/fairness objectives,
multi-process worker fleets, large fan-in and near-limit payload matrices,
network partitions and latency, PostgreSQL failover, host/database clock
movement, autovacuum and long-retention behavior, upgrade/rollback, Linux AOT,
supported PostgreSQL releases, security review and operator playbooks. Use
`../ecosystem/implementation-programme.md` for the release gates.
