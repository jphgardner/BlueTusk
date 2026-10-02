# Workload and recovery qualification

`benchmarks/BlueTusk.Projections.LoadHarness` is a standalone Release executable. It uses public Data,
Events, Events.Streams, Streams, Projections and Projections.Live APIs. It does not introduce another CDC
transport, intercept provider writes or depend on Npgsql/EF. This harness and its reports do not confer
production qualification.

Run from the checkout using PowerShell 7, .NET 10, Python 3 and Docker:

```powershell
./docs/projections/evidence/run-load.ps1 -Profile quick
./docs/projections/evidence/run-load.ps1 -Profile quick -SmokePayloadBytes 65536
./docs/projections/evidence/run-load.ps1 -Profile matrix -Seconds 10
./docs/projections/evidence/run-load.ps1 -Profile soak -Seconds 600
./docs/projections/evidence/run-load.ps1 -Profile capacity -Seconds 600
./docs/projections/evidence/run-load.ps1 -Profile promotion -Repetitions 3
dotnet run --project benchmarks/BlueTusk.Projections.LoadHarness -c Release -- micro --job short --filter '*' --artifacts artifacts/projections-load/micro
```

`-NoBuild` executes an already-built Release binary. Use it for measured runs after compilation has
finished. `-OutputDirectory` must resolve inside the checkout because the candidate fingerprint tool
enforces that boundary; use a unique ignored `artifacts/projections-load/<campaign>` path for CI. The
runner captures the whole candidate before and after execution and fails the candidate gate if any
nonignored source input changed. Raw scenario results remain diagnostic evidence in that case. A
dirty but unchanged tree is a measured candidate, not an approved immutable release.

The fixture runner pins PostgreSQL 18 to
`postgres@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873`.
It provisions a fresh private network, labelled named volumes, and localhost ports 55718/55719; optional
port parameters must name unused distinct unprivileged ports. Default container limits are 4 CPUs and
2 GiB RAM with 128 MiB shared buffers. The client process runs on the host. Cleanup checks both owner
and unique fixture labels before deleting exact objects. The runner restores all prior process
environment values. It never restarts/checkpoints/vacuums the shared compatibility fixtures. The
physical trial's checkpoint is confined to its new promoted container and materializes timeline evidence.

## Workload contract

Every offered operation has a stable identifier. One caller-owned SQL transaction inserts an operation
ledger entry, changes an order amount or customer name, and appends one immutable Events outbox row.
An exact retry reuses identity, timestamp and content; the ledger prevents the business mutation from
repeating. The harness injects these duplicate retries while writers run concurrently.

One ordered Streams reader applies each complete source transaction to a durable source mirror,
customer/order join, dependency index and tenant total. A customer update invalidates its complete
dependency set through pages of 17 keys. Projection checkpoint and derived writes commit together.
The Events.Streams processor then commits transactional inbox effects before acknowledging the same
raw WAL delivery. Projection lease replacement periodically verifies stale-owner rejection and durable
redelivery deduplication. Neither stage treats an acknowledgement failure as permission to repeat a
business effect without its inbox/checkpoint contract.
The snapshot/WAL adapter sends PostgreSQL standby-status feedback only after both durable boundaries
have settled. It reports one confirmed-position update per acknowledged transaction and never advances
the sender on Nack. This makes a long backlog observable to the WAL sender; it does not recover an
application delivery that stalls past the sender timeout. That recovery remains an explicit operator
and source-retention gate.

Every tenant has an independently authorized durable owned Live query and one subscriber. Frames are
bounded, integrity checked and scanned for cross-tenant rows. Replay and subscriber sequences must
remain contiguous. After publishers stop, the drain verifies that every counted fan-out delivery and
replay frame reached the decoder exactly once before measurements end. Periodic refresh coverage timestamps
are conservative: only operations projected before the query starts are marked covered after its
durable refresh completes. Updates may coalesce, so this measures persisted query coverage, not an
individual event's network-delivery latency. Final reconnect uses the persisted sequence head.
Idle subscribers receive a bounded metadata refresh at least every 30 seconds, with at most four
concurrent refreshes, so a cold tenant's two-minute writer lease does not expire during a long soak.

Hot tenant `tenant_000` has the configured 1/64/512 order dependency fanout; each cold tenant has one
order. Offers follow a deterministic ten-hot-to-one-cold schedule, cycling all cold tenants. The bounded
queue admits using `TryWrite`; rejected offers are counted. A separate pending-work ceiling prevents
source/consumer lag from growing indefinitely: `max(4096, configured initial backlog)` accepted
operations may be pending. Rejected offers are not included in successful-operation latency quantiles.
Offer timestamps use the fixed-rate schedule, so admitted latency includes scheduler/queue delay.
Producer concurrency, queue capacity, total accepted identities, individual/batch bytes, stream changes,
dependency fanout, subscriber buffers, latency samples, observation windows and drain deadlines are
bounded. The queue count includes a short dequeue-to-writer bookkeeping overlap of at most the writer
count. The reported queue-byte figure is a conservative configured event-envelope reservation; queued
operation descriptors do not retain a separate payload copy.

The twelve-case matrix spans target payloads 128 B/4 KiB/64 KiB, 1/32/256 tenants, 1/4/16 writers,
acknowledged unapplied backlogs 0/10,000/100,000, and dependency fanout 1/64/512. These are selected
combinations, not the full Cartesian product. Deterministically generated high-entropy ASCII padding
avoids a repeated-character compression benchmark; envelope overhead is bounded by 1,024 bytes.
The same padding is carried in source orders, durable source mirrors, joined documents and Live results,
so the payload sweep exercises the Projections read/write path as well as the Events outbox. Selected
fanout/payload combinations keep the complete authorized Live document window below 1 MiB.
Unchanged TOAST values in new update tuples are restored from historical FULL old tuples. Missing
historical bytes fail closed; the definition never reconstructs past WAL from current source SQL.
Backlog seeding uses caller-owned batches limited to 64 operations and 1 MiB of event admission.
The 600-second profile combines 32 tenants, 16 writers, a six-connection pool, 4 KiB payloads,
10,000 initial pending operations, fanout 64 and 1,500 scheduled offers/second. Overload rejection,
backlog recovery and per-tenant service are reported explicitly; this is not a promise of fairness or
latency under every application-defined hot-key lock pattern.

The separate `capacity` profile uses the same 32 tenants, 16 writers, six-connection pool, 4 KiB
payloads, fanout 64 and ten-hot-to-one-cold offer mix, but starts without a backlog and schedules
20 operations/second. It preserves the complete SQL/Streams/Events/Live pipeline and final exact-state
verification. Its lower offered rate is a conservative candidate for steady-service latency measurement,
not an asserted maximum. Admission and delivery rates must be checked against the measured offer
window; a successful final drain alone does not show that the pipeline kept up while offers ran.

The harness verifies every accepted operation against durable SQL: exact operation/outbox/inbox/effect
counts and identities; tenant isolation; contiguous event sequence count/min/max/sum; every joined
document through bounded keyset pages; tenant aggregate equals authoritative order amounts and the
operation ledger; complete snapshot table coverage; durable checkpoint and Live reconnect. Any
missing effect, duplicate, unexpected frame, bound breach or drain timeout fails the scenario.

## Reports and interpretation

`campaign.json` records SDK/platform, CPU identity where available, Docker version, image digest,
container limits, durations, fingerprint status and `ProductionQualified=false`. `run-N.json` contains
the scenario configuration, counts, admission rejection, overall throughput, offered-to-commit,
projection, inbox and Live coverage P50/P95/P99/max, per-tenant results, and cumulative ten-second
service windows. `MeasuredSeconds` is the observed offering period; `PipelineSeconds` includes initial
backlog/recovery and ends at verified subscriber drain.
`TransactionsPerSecond` divides committed operations by `PipelineSeconds`, so it includes
backlog recovery and subscriber drain. To estimate accepted throughput during the scheduled
offering period, subtract seeded backlog operations from the committed total before dividing by
`MeasuredSeconds`; the two rates answer different questions.
`OfferWindow` records actual scheduled offers, accepted and rejected operations, and committed,
projected, inbox-settled and Live-covered operations whose boundary timestamps occurred by the exact
end of the offer period. Its accepted and inbox rates divide those counts by `MeasuredSeconds`;
`PendingInboxAtEnd` is accepted minus inbox-settled work at that boundary. These fields distinguish
steady service from eventual success after a long drain. The historical `TransactionsPerSecond` name
is an operation throughput measure: backlog batches can contain several operations per WAL transaction.
`RuntimeSeconds` covers the concurrent pipeline and drain only; `VerificationSeconds` separately records
final SQL/model/reconnect checks and report statistics. Live replay and fan-out frame totals are reported
separately. Difference adjacent service windows to examine hot/cold progress during saturation.

Runtime observations include client allocation, GC, CPU/RSS/managed-memory maxima, pool total/busy/
waiting and actual backend/lock/wait counts. They cover the concurrent pipeline and drain; backlog
seeding and physical rebuild occur before that runtime probe, which stops before final verification.
Storage observations before/after
include those earlier phases: WAL position/records/bytes/full-page images, checkpointer counters/times,
owned table heap/index/total bytes, live/dead tuple estimates and vacuum/analyze counters. The probe
samples logical-slot retained WAL and owned table growth, failing at 8 GiB retained WAL or 12 GiB owned
table storage. Docker's raw JSONL statistics capture fixture server CPU/memory/block/network activity
in sampling order. PostgreSQL statistics are estimates and can lag flushes; they are never globally reset.
Each bounded ten-second `ServiceWindow` also includes the most recent retained-slot and owned-relation
bytes and that PostgreSQL probe sample's age. Use their time series, not only maxima, to inspect
physical progression. At most 512 windows are retained, covering the bounded one-hour offering and
maximum drain. The probe includes the fixture's projection and Events schemas; `pg_total_relation_size`
includes each relation's TOAST and index storage.
Only the fresh exclusive fixture makes server-global deltas attributable to this campaign. Other host
workloads must still be disclosed, since container CPU limits do not isolate the client or host storage.

Every scenario also writes an additive diagnostic `run-N-<scenario>-delivery.jsonl`; no report field or
verifier input changes. Its header and trailer carry the UTC anchor and measured PostgreSQL clock
offset. One line per delivered WAL transaction records the receive wait, decode, projection apply,
inbox processing/acknowledgement and periodic lease-rotation durations, each attributed to provider pool
checkouts, pool waits, resets and command round trips (`other` time is mostly `COMMIT`). After the drain,
one line per accepted operation records its offered, committed, projected, inbox and Live timestamps. The
fixture always enables `track_wal_io_timing` and `log_checkpoints`. `-Diagnostics` additionally logs every
autovacuum, samples server wait events (10 Hz) and WAL/relation I/O, checkpointer and replication
counters (1 Hz) from one separate session into `run-N-<scenario>-server.jsonl`, records host CPU and .NET
build/test process CPU time plus Docker statistics for every running container, and retains the exact
fixture's raw server log as `run-N-primary.log`. The JSONL files contain no SQL text, parameters or
payloads; the raw server log is unfiltered PostgreSQL output, so keep `-Diagnostics` output in an ignored
directory. The verifier never reads any of these files.

Events identities, outbox and inbox rows persist permanently in these runs. Their linear storage growth
is measured, not disguised as bounded retention. Projection derived-version cleanup in the physical
trial deletes at most 37 total rows per transaction and proves the retirement fence and event identities
survive. The BenchmarkDotNet micro profile separately measures source-generated event/document
encoding/decoding and immutable event admission with allocation diagnostics; it is not database capacity.
The Live replay store's window is 30 minutes in both `soak` and `capacity`; neither profile invokes
replay pruning. A 30-minute campaign therefore measures a finite accumulation horizon and cannot
establish a replay-storage plateau. A separate longer maintenance trial with explicit pruning and
reconnect-after-expiry assertions is needed for that claim.

## Promotion under acknowledged backlog

Each promotion repetition provisions a new primary/physical standby pair, verifies streaming and
`synchronous_commit=remote_apply`, then acknowledges 2,000 business/outbox operations while the WAL
consumer is stopped. It closes its exported-snapshot attempt, hard-kills the primary before promotion,
promotes the standby and routes existing target stores/old Live owner objects to the promoted database.
All acknowledged operations/outbox rows and the former published checkpoint must remain present.

The changed timeline cannot bind to the old registered lineage. An explicit durable operator recovery
ticket fences the predecessor. A new slot and fresh exported snapshot build version 2; durable Events
replay repairs the preserved outbox inbox effects in bounded batches, rejects stale replay owners and
checks contiguous per-tenant checkpoints. Retained WAL must cover a fresh verified barrier before
`CompleteRecoveryAsync` cutover; ordinary promotion cannot bypass the recovery policy. Its exact retry
confirms the durable completed ticket. Old Live ownership must fail against the actually routed new
target; replacement publishers issue authoritative reconnect resets. The same workload then resumes
and performs final exact-state checks. Separate promotion and rebuild timings avoid implying transparent
failover. After promotion, the new single primary uses local synchronous durability; a second synchronous
standby is not installed by this small fixture.

These rehearsals do not replace partition/split-brain routing, failover-slot continuity, backup restore/
PITR, storage exhaustion, credential rotation, clock movement, supported Linux measurements or endurance
qualification. Fresh PG15–18 integration tests are compatibility evidence, not major-version upgrade
rehearsals. Old/new binary durable-format upgrade/rollback, mixed-binary deployment and recovery-ticket
migration remain separate gates. Source DDL still requires the controlled immutable-history deployment
policy in [RECOVERY.md](RECOVERY.md); a historical DDL ledger is not provided by this harness. No universal
performance leadership claim, production support promise or release approval is inferred.
