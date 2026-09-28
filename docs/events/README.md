# BlueTusk.Events

BlueTusk.Events is an independent `0.1.0-preview.1` family targeting .NET 10. It records typed,
versioned business intent in the same PostgreSQL transaction as business data, deduplicates database
effects in a consumer inbox, and replays an ordered tenant stream with durable fenced checkpoints.
The runtime uses ADO.NET and BlueTusk.Data. Npgsql and EF Core are not runtime dependencies of the core.

## Transactional publishing

Define a stable contract name/version and generate JSON metadata for each historical wire version:

```csharp
[JsonSerializable(typeof(OrderPlaced))]
internal partial class EventJson : JsonSerializerContext;

var contract = new EventContract<OrderPlaced>("orders.placed", 1, EventJson.Default.OrderPlaced);
var value = contract.Create(stableEventId, new OrderPlaced(orderId, total), occurredAt);
var stream = new EventStreamKey(tenantId, orderId.ToString(CultureInfo.InvariantCulture));

await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
// Write business rows using this connection and transaction.
await store.AppendAsync(connection, transaction, stream, [value], cancellationToken);
await transaction.CommitAsync(cancellationToken);
```

Generate event identity and occurrence time once, outside a retry loop. A retry of the same tenant/event
identity must contain identical stream, contract, timestamp (at PostgreSQL microsecond precision), and
payload bytes. A conflicting identity fails. The tenant is part of identity: the same UUID in two
tenants identifies two separate events. Cross-stream races for the same tenant/event identity can fail
with PostgreSQL's unique constraint; roll back the transaction and correct the publisher.

The optional `BlueTusk.Events.EntityFrameworkCore` adapter exposes `context.AppendEventsAsync(...)`.
Begin an explicit relational transaction, save business changes, append events, then commit. The
adapter requires that transaction; it does not start one, call SaveChanges, or commit on the caller's
behalf. The caller owns the data source, connection and transaction lifetime.

Call `InitializeAsync` during controlled deployment. Schema version 1 is installed transactionally
under an advisory lock. Unknown versions are rejected. No hot path automatically migrates schemas.

## Ordering and scale

There is no global sequence. The primary key is `(tenant_id, stream_id, sequence)`. Appending locks
that stream's counter row until the application's transaction commits or rolls back. A later writer
therefore cannot publish offset 2 before offset 1 commits, and rollback does not consume an offset.
Unrelated streams can proceed concurrently. Choose an aggregate or shard as the stream; sending all
events to one stream deliberately serializes writers. Transactions publishing to multiple streams must
lock them in a consistent application-defined order to avoid deadlocks.

Each append is bounded by event count, individual payload size, and total payload bytes. Defaults are
1,024 events, 1 MiB/event, 8 MiB/batch. Batches use `jsonb_to_recordset` with one bulk INSERT; they do
not issue one INSERT per event. They perform one stream lock, one bounded duplicate lookup, one bulk
INSERT, and one head update. Base64 transport and UTF-8/UTF-16 JSON buffers incur bounded additional
memory above the payload-byte budget. Query keys are indexed, and replay always filters both tenant
and stream before ordering by sequence. Readers enforce both count and payload-byte limits.

Byte budgets must accommodate the maximum allowed individual event so a large event cannot stall a
cursor. Consistent limits are a schema-wide operating contract: do not attach a smaller reader limit
to a store already containing larger events. Use cancellation and bounded connection-pool admission.
Configure `CommandTimeoutSeconds` for SQL work. Handler execution time must also have an application
deadline; a database command timeout alone cannot limit arbitrary application code.

## Inbox and replay

`ProcessInboxAsync` inserts `(consumer_id, tenant_id, event_id)` and invokes the handler using the same
caller-owned transaction. Concurrent duplicate deliveries wait for that transaction. If it rolls back,
the next delivery can handle the event. If it commits, the next delivery is suppressed. Duplicate
stream/sequence/contract/timestamp/payload-hash conflicts fail. On any handler or store exception,
roll back the complete transaction. Do not catch a handler exception and commit its inbox row.

The guarantee applies to database effects using the supplied connection/transaction. It does not make
HTTP requests or other external effects exactly once. Record another outbox entry when an external
operation must follow a database commit.

Replay acquires an explicit lease for `(consumer, tenant, stream)`. Acquisition advances a persisted
fencing token. Renewals and release require the current owner/token. Database time determines expiry.
`ReplayAsync` locks the replay row, reads a bounded ordered batch, runs transactional inbox handling,
and advances the checkpoint in the same transaction. A checkpoint requires an unexpired matching
lease; an expired worker rolls back all batch effects. Missing sequence numbers fail rather than
silently advancing. Owner replacement and process restart preserve the checkpoint.

Use `EventRouterBuilder` to register source-generated contracts with typed handlers. The immutable
router dispatches exact name/version pairs. Register historical versions with application-owned typed
upgrade functions. An unknown version fails the batch and keeps its checkpoint unchanged. Rebuilding
the router cannot change a previously built instance.

Lease duration must cover the worst-case batch. Renew between batches. A replay batch holds its row
lock, so an independent renewal or takeover waits until it completes. `ReachedEnd` means the returned
checkpoint reached the committed stream head observed during that batch; producers can append again.
Use a new consumer identity for intentional replay from zero. Reusing a consumer resumes its durable
checkpoint and retains inbox deduplication.

`ReadReplayStatusAsync(consumer, stream)` returns one committed metadata snapshot containing stream
existence/head, replay registration/checkpoint, exact remaining-event count and DB-clock owner/fence/
expiry. It performs two indexed metadata lookups in one SQL statement, creates no stream/consumer
rows and reads neither outbox nor inbox payloads. Missing registration is distinct from an expired or
released owner. Health reads require the explicit tenant/stream and consumer; they cannot enumerate
other tenants. The bounded owner identity is excluded from the wire if corrupted beyond its contract.
Use the returned `ObservedAt` when displaying health: `IsLeaseActive` is a point-in-time observation,
not permission to commit later. Mutating operations still enforce their own DB-clock fencing.
Poll a bounded set of authorized streams with connection/command/cancellation admission; this seam
does not implement a global wildcard monitor or a worker host. Unknown historical versions still fail
the replay transaction; repair the registered typed handler/upgrade and resume the existing checkpoint,
or intentionally rebuild a separate versioned effect model under a new consumer identity. Do not
delete inbox identities or rewrite stored wire events to make a repair pass.

## Streams delivery adapter

`BlueTusk.Events.Streams` is separate from the core. `EventOutboxChangeDecoder` recognizes only the
configured immutable outbox table in raw Streams change contracts. It supports PostgreSQL text and
binary representations of UUID, int4/int8, timestamptz and bytea, including both hex and escape bytea
output, and rejects unavailable columns, malformed payloads, excessive event sizes and outbox mutation.

`PostgreSqlEventDeliveryProcessor` binds a consumer to a specific Streams source. It bounds source
changes, event count, and payload bytes, validates source transaction identity and within-transaction
stream ordering, then handles **all** outbox events in that source transaction through the target
transactional inbox. It commits target effects before acknowledging the Streams delivery. A handler
failure or target COMMIT failure leaves the source unacknowledged and target effects rolled back. If
acknowledgment fails after target commit, redelivery finds the inbox and suppresses repeated effects.
Streams owns source transport and checkpoints; the adapter does not install replication infrastructure
or intercept provider/EF writes. Register a source beginning at a deliberate checkpoint, and deliver
distinct transactions in source commit order; do not dispatch them concurrently to the same logical
consumer. The bounded processor materializes only selected business-event envelopes, not all source
rows. Transactions are never split across inbox commits to fit a bound.

This committed-CDC processor does not implement historical snapshot delivery or durable independent
per-stream remote subscription offsets. Use core replay for an existing local outbox, or establish an
explicit source bootstrap/replay policy before wiring a remote live event subscription. Source identity
changes require deliberate consumer/source reconfiguration. Target effects must use the processor's
supplied connection/transaction, and its data source must address the inbox store's database/schema.

## Local-only archive and retention

Schema version 2 adds a compact `(tenant, event ID)` identity ledger, an archive manifest and explicit
per-stream `ArchivedThrough` and `RetainedThrough` horizons. New outbox inserts write their identity
record through a database trigger, including when an older application binary inserts the row. A v1
deployment migrates metadata without scanning its entire outbox. An old v1 event receives its identity
record when its bounded archive batch is sealed, before it is eligible for pruning. The ledger preserves
the original sequence and detects cross-stream identity reuse. An archived retry compares immutable
metadata, payload length and SHA-256; byte-for-byte comparison still applies while the outbox row exists.
Identity records, stream heads, replay checkpoints/fences, archive manifests and inbox rows are retained.
Their storage still grows with event count; this first stage only removes archived outbox payload rows.

Local retention is **off by default**. Set `PostgreSqlEventsOptions.EnableLocalOnlyRetention = true`
only after inventorying readers and freezing publication changes. Supply an `IEventArchiveStore` backed
by durable, immutable external storage. `ArchiveNextAsync` writes a bounded contiguous prefix, reads it
back in full, verifies every event, then commits its manifest, batch digest and any legacy identities.
If the archive write/readback or DB commit fails, the stream horizon does not move; an unreferenced
external object may remain for archive-side garbage collection. `ReadRetentionStatusAsync` exposes the
committed horizons without reading payloads.

After all registered local replay consumers have checkpointed the archived prefix, call
`AdvanceLocalRetentionAsync` with the expected prior floor and a stream-bound
`new EventLocalRetentionCertification(stream, operatorId, changeReference,
confirmedNoExternalReaders: true)`.
This operator certificate explicitly asserts that no unregistered direct or external reader needs the
prefix. The method rejects lagging registered replay consumers and any PostgreSQL publication containing
the outbox. `PruneRetainedAsync` removes at most the requested number of rows per transaction and checks
publication membership again before every batch. It retains every event identity and inbox row.

`ReadAsync` throws `EventHistoryUnavailableException` when `afterSequence` is below the retained floor;
`ReplayAsync` applies the same guard to its checkpoint. A new replay consumer cannot acquire a lease
after the floor advances. Existing consumers continue from their acknowledged checkpoint. There is no
implicit skip, archive replay API or snapshot bootstrap in this stage. Restore the archived range using
an application-owned procedure, or create a separately specified snapshot bootstrap before admitting a
new consumer.

The publication check uses `pg_publication_tables` and holds a `SHARE UPDATE EXCLUSIVE` lock on outbox
through each floor advance and bounded delete. This serializes explicit-table publication changes with
the check. PostgreSQL all-tables and schema-level publication DDL may not lock the individual outbox
relation, so deployment policy **must forbid publication DDL while local retention is enabled** and keep
publication privileges separate from the runtime role. PostgreSQL cannot discover application readers
that do not register in `replay`; the operator certificate is a required external inventory claim, not
automatic proof. Never use this API for the published outbox consumed by `BlueTusk.Events.Streams` or
Projections: their current decoders reject outbox deletes. A published-outbox retention protocol needs
an ordered source control record and durable acknowledgements from every CDC consumer/version before
deletes can be interpreted as maintenance. Source/target restore must preserve the external archive and
manifest together. The archive backend's durability, periodic integrity audit, physical compaction/
vacuum, and migration under sustained load remain deployment responsibilities.

## Validation and remaining release gates

The `BlueTusk.Events` meter records bounded append preparation/duplicate counts, inbox attempt outcome,
committed replay progress/remaining count and lease fencing. Append and inbox methods use caller-owned
transactions: their `prepared` measurements do **not** claim that the caller later committed. Replay
commit/progress measurements follow the store-owned transaction commit. Metrics omit tenant/event IDs,
and throwing listener callbacks cannot alter database durability. No metric substitutes for an application
transaction outcome or durable checkpoint.
`bluetusk.events.replay.observed_remaining` records read-only status observations separately from
committed replay progress, with no consumer/tenant/owner labels. Faulty observer callbacks cannot
fail the metadata query.

Run live tests with a disposable PostgreSQL database:

```powershell
$env:BLUETUSK_TEST_CONNECTION_STRING = '<disposable PostgreSQL connection string>'
dotnet test tests/BlueTusk.Events.Tests/BlueTusk.Events.Tests.csproj -c Release -nr:false
```

The tests create and remove only their randomly generated `events_test_*` schemas. They cover real
rollback/visibility, ordering under transaction lock contention, concurrent appends and identity
retries, tenant isolation, byte limits, inbox duplicate races, EF transaction integration, cancellation,
typed version dispatch, handler failure, owner replacement and fencing before checkpoint commit.
Adapter tests additionally verify real pgoutput outbox WAL, text/binary wire equivalence, arbitrary
byte payloads, atomic handler rollback, deferred-constraint target COMMIT failure, acknowledgment
failure after commit, duplicate replay, immutable outbox admission and transaction bounds. The native
smoke exercises the adapter's inbox and duplicate-delivery path against PostgreSQL as well.

`EventsBoundedWorkloadTests` runs eight concurrent tenant streams with 512 retained events each,
reuses the same IDs across tenants, rolls back and retries bounded append batches, and resumes fenced
replay in 47-event pages. It verifies all 4,096 database effects, their exact sequence sum and retained
contiguous offsets. This bounded correctness workload does not measure sustained throughput or p99.

This preview is not a claim of readiness for massive production workloads. Required work before a
stable release includes sustained throughput/allocation/p99 benchmarks at representative payload sizes
and stream cardinalities; crash/kill and ambiguous-commit fault campaigns; failover/restore/upgrade
testing; partition/archive/retention strategies preserving deduplication and replay; large backlog
query-plan verification; control-plane/Streams transport integrations; observability and operational
runbooks; least-privilege tenant/RLS deployment validation; and full NativeAOT/endurance/release gates.
Do not delete outbox or inbox records behind a replay consumer: retained identity and contiguous offset
invariants are part of correctness. The local-only API above is an explicit, guarded exception; it is
not a retention path for published outboxes or a claim of bounded whole-system storage.
