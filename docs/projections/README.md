# BlueTusk.Projections

BlueTusk.Projections is an independent .NET 10 `0.1.0-preview.1` family for versioned, durable
application read models. Its PostgreSQL destination stores source mirrors, joined output documents,
dependency indexes, decimal aggregates, snapshot coverage, and CDC checkpoints in one database.
It consumes **BlueTusk.Streams** snapshot and committed-transaction contracts. It has no EF persistence
interception or Sync connector dependency, and no Npgsql runtime dependency.

## Definition and destination contract

Implement `IProjectionDefinition` with an explicit name, version, immutable definition fingerprint,
and Streams source identity. A registered `(name, version)` cannot be rebound to different code
semantics or a different source. Change the version whenever serialization, joins, aggregates, tenant
resolution, keys, dependency semantics, or mappings change. The fingerprint must represent those
semantics, not an arbitrary per-process value.

The definition receives a bounded `ProjectionWriteContext` inside a destination transaction. It can:

- Store each source table's current committed row image with `UpsertSourceAsync`/`DeleteSourceAsync`,
  or bounded `UpsertSourcesAsync`/`DeleteSourcesAsync` bulk operations using one SQL command per batch.
- Read source mirrors to calculate joins from exactly the CDC history being applied.
- Write typed output using source-generated `JsonTypeInfo<T>`, or persist an explicit byte payload.
- Replace output and dependencies in bulk using `UpsertManyAsync`: three SQL commands for a bounded
  batch, independent of document count.
- Find affected joined outputs with indexed, tenant-scoped, ordinal keyset dependency pages.
- Apply exact decimal aggregate deltas with `AddAggregateAsync` and read them in the same transaction.
- Use its borrowed connection/transaction for application-specific relational state.

Stage **all** source rows from a committed transaction before resolving joins. A transaction may
update both parent and child tables, and its CDC row order is not an application dependency order.
Keep enough old source state to remove prior aggregate contributions, update memberships, and handle
unchanged TOAST/unavailable old values explicitly. Never query the live source database to reconstruct
an earlier transaction: it may already contain later commits.

The deployable sample and integration test's `OrdersProjection` implement two-table business state: they mirror orders
and customers, maintains order totals, invalidates every joined order when a customer changes, keeps
orphan dependencies for later customer insertion, remove order dependencies on deletion, and write
derived rows in bulk. Repeated changes to one source key collapse to its final committed image before
aggregate deltas and joins are calculated. Both snapshot table arrival orders are supported.

Destination operations, source mirrors, dependency replacement, aggregates, and checkpoint advance
commit atomically. A handler exception, bound failure, expired lease, or cancellation rolls all of
them back. The context is unusable after its callback returns. External side effects do not share this
database atomicity; publish them through an outbox.

## Bounds and ordering

Defaults permit 100,000 source changes and 64 MiB of source/output payload per destination transaction,
2,048 snapshot rows per batch, 1 MiB per document, 256 dependencies per document, and 100,000 write
operations/invalidations per transaction. Bulk document writes are limited to 1,024 documents and
8 MiB. Tune these bounds together with worker concurrency and connection-pool admission. Payload
budgets do not include every CLR/JSON/base64/SQL representation; benchmark the complete allocation
envelope rather than treating the payload bound as a process memory bound.

Actual CDC row bytes and ordered change identity are validated even if a producer underestimates
`ChangeSet.EstimatedBytes`. Validation and definition processing enumerate a replayable Streams
`ChangeSet`; large spooled transactions incur two reads. Source transactions are never split across
checkpoint commits. Oversized transactions fail explicitly for operator remediation.

Dependency pages include `ContinueAfter`. Every outstanding page must be consumed before checkpoint.
Incomplete pagination and exceeded output/fan-out limits poison the context; catching that exception
inside a definition cannot checkpoint partial work. Read and write operations always require a tenant.
Raw SQL through the borrowed transaction is trusted application code and must enforce the same bounds
and tenant policy.

One leased version serializes destination transactions to retain commit order. Send it an ordered
Streams delivery sequence. PostgreSQL LSNs are not consecutive counters, so the store cannot infer a
missing source transaction from a numeric LSN gap. Never concurrently dispatch different source
positions to the same version; concurrent duplicate deliveries are safely deduplicated. Parallelize
independent projection versions/source partitions. Two-phase, synthetic, wrong-source, and typed
mapped transactions fail closed in this preview; use raw committed Streams row contracts.

## Consistent bootstrap and rebuild

Use `PostgreSqlConsistentSnapshotSource` with `SnapshotThenStreamCoordinator` and
`StreamsProjectionConsumer`. The existing Streams source creates a logical replication slot with an
exported PostgreSQL snapshot, imports that snapshot for every source table, and resumes the matching
slot's WAL. An arbitrary SELECT plus the current WAL position does not satisfy this contract.

The destination starts a source-bound snapshot epoch, validates contiguous batch sequences for each
table, stores exact batch-content fingerprints for idempotent retries, and counts complete table and
row coverage. A durable source-key ledger rejects overlapping rows across different snapshot batches
so retry/overlap cannot double an aggregate contribution. The checkpoint becomes the epoch's consistent position only after every declared table
has ended and the completion counts match. New CDC is rejected before completion. A reset durably binds
a replacement epoch and enters `Resetting`, which blocks snapshot application, CDC and cutover. It then
deletes only that unpublished version's derived state in fenced transactions of at most
`MaximumResetBatchRows` total rows, draining dependencies before documents to bound cascades. Only the
last transaction opens `Snapshot`; abandoned-epoch batches fail. State/lineage/identity remain intact.
`StartSnapshotAsync` drives this protocol automatically. `BeginSnapshotResetAsync`,
`ContinueSnapshotResetAsync` and `ReadSnapshotResetAsync` support explicit bounded progress and recovery
after cancellation or process loss. A replacement worker reads the recorded epoch and resumes it under
its new fence; an interrupted reset must finish before another epoch can replace it. The Streams consumer
drains such a recorded reset before beginning a new exported snapshot attempt.
The consumer's reset notification is followed by SnapshotStart, which performs the durable reset.

`StreamsProjectionConsumer` commits the destination before acknowledging a delivery. If acknowledgment
fails after commit, redelivery finds the durable checkpoint and does not repeat aggregate effects.
Reacquire a version lease after restart and resume an ordered retained Streams source after the stored
checkpoint. Do not start a fresh snapshot over a published version.

For a changed definition, register a new version and build it beside the published one. Public reads
join the `heads` pointer with versioned documents in one query. `PromoteAsync` requires a complete
snapshot, a checkpoint at or beyond the caller's source barrier **and** the locked current active
checkpoint, a valid fenced lease, and the expected current active version. The pointer switches
atomically. Keep the old version until rollback/retention policy permits its retirement. Source barriers
must come from the ordered source stream, not an unrelated target database WAL position.

The default promotion requires the exact source identity, including its slot. For a separate-slot rebuild,
capture actual source evidence with `PostgreSqlProjectionLineage.CaptureAsync`, then use
`RegisterWithLineageAsync` **before** its first snapshot. Schema version 5 persists the immutable lineage
and table/column snapshot contract. Existing initialized histories cannot acquire evidence retroactively.
The evidence includes system identifier, database name/OID, timeline, actual publication OID/flags and
table OIDs, replica identities, full column/type/key metadata and row-filter identity. A publication name
alone is insufficient. Current snapshots reject filtered or partial-column publications and require all
four DML operations and primary keys.

`CaptureForCutoverAsync` refreshes this actual evidence and emits a transactional logical message on a
source connection verified against it. `PromoteWithLineageAsync` requires matching persisted active and
candidate lineage, fresh evidence, the expected active version, complete snapshot coverage and candidate
WAL coverage through **both** that verified source barrier and the locked active checkpoint. Enable
Streams logical messages and deliberately handle the `bluetusk.projections.barrier` prefix in the
definition. The sample runs two independent snapshot/slot workers and performs this explicit cutover.

Source/publication DDL must remain immutable throughout the snapshot and retained WAL interval.
Raw row CDC cannot prove that an external actor changed and then reverted that historical contract.
The role needs access to PostgreSQL control functions as well as logical replication and publication
metadata. Cross-timeline failover, restore and independent source histories are not certified by this
ordinary policy. Strict promotion also rejects different bound lineage under the same Streams identity.
Schema version 6 provides an explicit durable operator recovery ticket and pre-DDL maintenance fence:
the old writer is fenced, its model stays readable, and a new version must build a fresh exported snapshot
and cover the authoritative target's verified barrier before atomic cutover. See
[operator recovery and controlled DDL](RECOVERY.md) for policy boundaries and the physical standby rehearsal.

Database-clock leases carry monotonically increasing fencing tokens. Acquisition replaces only an
expired/released owner; renewal and release require the current token. Every changing destination
transaction checks expiry again before checkpoint commit. A lease must cover the worst-case batch;
renew between batches. Row locking means a concurrent renewal waits for a running batch. Application
deadlines must bound handler time in addition to the SQL command timeout.

## Published Live reads, metrics and retirement

`BlueTusk.Projections.Live` adapts the durable published revision to existing Live invalidation,
source-generated replay and SSE APIs. It provides tenant-scoped, count/byte-bounded ordinal key pages,
detects stale page continuations, and emits authoritative resets on version cutover or server restart.
See [the Live contract](LIVE.md) and the executable
[orders sample](../../samples/BlueTusk.Projections.Orders.Live/README.md).

The `BlueTusk.Projections` meter records `bluetusk.projections.commits` by operation, fenced work,
bound failures, apply duration/outcome, output writes/bytes, dependency invalidations and retired rows
pruned, plus `bluetusk.projections.reset.rows_deleted` after bounded reset commits.
Commit/output measurements follow owned destination commits; duplicate transactions do not
report new writes. Metric callbacks cannot change durability. Labels contain no tenant, document or
projection identity; configure application tracing for bounded identity correlation.

`RetireAsync` fences an unpublished version while verifying the expected active version under the same
publication lock. It leaves a permanent identity tombstone: that version can never register, acquire,
apply or promote again. `PruneRetiredAsync` removes at most the requested total derived row count per
call, drains dependencies before documents and retains state/checkpoint/identity/tombstone records.
Retirement is irreversible and rules out rollback to that version. Pause/release its source worker and
apply the application's retention policy before retiring; source-slot removal is a separate operation.
There is no pruning of active state, Events deduplication identities or shared source history.

## Verification and remaining production gates

```powershell
$env:BLUETUSK_TEST_CONNECTION_STRING = '<disposable PostgreSQL connection string>'
dotnet test tests/BlueTusk.Projections.Tests/BlueTusk.Projections.Tests.csproj -c Release -nr:false
dotnet publish tests/BlueTusk.Projections.NativeAotSmoke/BlueTusk.Projections.NativeAotSmoke.csproj -c Release -r win-x64 -nr:false
```

Tests own randomly generated `projections_test_*` schemas, publications, and slots. They cover real
PostgreSQL rollback, typed joined read models, dependency fan-out, incomplete-pagination failure,
aggregate exactness, deletion and orphan recovery, concurrent duplicates, restart/lease fencing,
snapshot sequence/content/coverage, source/version mismatch, and guarded version cutover.
`ConsistentProjectionIntegrationTests` uses a **real exported snapshot and real pgoutput WAL**: one
transaction changes two source tables while the snapshot is open, then verifies old snapshot state
and subsequent atomic joined/aggregate state without a concurrent-write gap. The rebuild integration
uses two real exported snapshots/slots, a concurrent source commit, verified source barrier and explicit
lineage cutover. The sample test launches two actual server processes and exercises SSE, signed reconnect,
abrupt restart and candidate promotion. The native smoke executes joined/bulk state, Live JSON replay,
actual lineage capture and a verified source barrier against PostgreSQL.
The native smoke also executes maintenance fencing, actual source DDL, fresh exported snapshot/WAL,
durable recovery ticket, exact cutover retry and published Live reset. The deployable sample's SSE test
executes the new operator endpoints through an actual third process and observes later WAL updates.

`ProjectionBoundedWorkloadTests` imports 128 initial orders in 17-row snapshot batches, retains sixteen
subsequent source commits behind an open candidate snapshot, applies customer dependency fan-out and
deletion, and reacquires ownership after destination commit before source acknowledgment. It verifies
112/64 final tenant documents, exact 176/64 totals, 17-document page bounds, duplicate suppression and
promotion through the verified barrier. It then prunes the retired version in at most 37 derived rows
per transaction while preserving the published pointer, checkpoint, aggregate and permanent identity.
`ProjectionResetRecoveryTests` builds more than 10,000 derived rows and verifies exact deletion counts
in 37-row transactions, concurrent reset continuations, canceled batches, cross-instance lease recovery,
durable epoch reconstruction and blocked snapshot/CDC/cutover until completion.
These fixed-size invariant tests are not a sustained throughput, allocation or p99 benchmark.

The timed real SQL/outbox/pgoutput/inbox/owned-Live workload and measured 60-second local report are
documented in [workload evidence](evidence/README.md). The captured one-producer profile verifies exact
state through 49 lease recoveries and duplicate retries; it does not certify production capacity.

This preview does not yet satisfy massive-production release gates. Required work includes sustained
throughput/allocation/p99 tests across backlog sizes, fan-out distributions, partition counts and large
transactions; SQL plan/lock/connection-pool analysis; crash/ambiguous-commit/failover/restore campaigns;
cluster routing/deployment recovery; restore/PITR and sustained failover validation; automated rebuild scheduling;
historical source-DDL verification and mapping compatibility; archive/partition policy; control-plane/runbooks; richer declarative definitions;
and complete release/endurance validation. The per-projection publication lock serializes active and
candidate applications and bounded reset transactions. Benchmark its contention and PostgreSQL vacuum,
WAL and storage behavior under representative backlog sizes before production deployment.
