# BlueTusk Sync

BlueTusk Sync materialises transaction-preserving Streams deliveries into
external destinations. It consumes `BlueTusk.Streams` only; it never reaches
logical-replication wire messages.

The core pipeline owns the provisioning, snapshotting, catching-up, running,
paused, rebuilding, reconciling, faulted, and stopped states. A source
transaction is transformed and offered to a destination as one immutable batch.
The Streams delivery is acknowledged only after the destination returns the
exact commit-end position as durably handled. Duplicate delivery is safe when a
destination reports the same position as already applied.

Transform definitions carry a canonical SHA-256 fingerprint. A mismatch moves
the pipeline to `Rebuilding` and requires an explicit rebuild or migration;
BlueTusk does not silently reinterpret existing destination data.

Poison transformations pause by default. `QuarantineAndAdvance` is accepted only
with an explicit durable quarantine sink, and the source delivery is not
acknowledged until that sink confirms storage. Destination outages and rejected
durability confirmations nack the delivery and fault the pipeline for safe
redelivery.

PostgreSQL, NATS JetStream, Redis, and OpenSearch connector slices are
implemented and pass the same executable snapshot-plus-stream recovery
contract. The shared count, key-set, and partitioned content-hash engine plus
PostgreSQL, Redis, and OpenSearch repair paths are implemented and live-tested.
Product-level rebuild orchestration, source adapters, hosting, and endurance
remain gated work. The Sync release train remains non-publishable until those
remaining Phase 5 gates pass.

## Shared destination conformance

`BlueTusk.Sync.Testing` contains `SyncDestinationConformanceSuite`, the single
connector acceptance scenario used by the PostgreSQL, NATS JetStream, Redis,
and OpenSearch live test projects. It verifies:

- provisioning and transform-fingerprint ownership;
- idempotent snapshot batches and completed snapshot state after a new
  destination instance starts;
- exact durable commit positions, same-instance duplicate delivery, and
  process-restart redelivery without replacing accepted content;
- explicit `RebuildRequired` results for transform-version drift; and
- durable, idempotent quarantine for connectors that expose
  `ISyncQuarantineSink`.

An in-memory reference harness also proves the kit rejects a destination that
reports a checkpoint beyond the applied source transaction. The live variants
run in their existing connector CI jobs; PostgreSQL runs across versions 15–19.

## Reconciliation and repair

`SyncReconciler` supports three explicit depths: count, partitioned key set, and
partitioned exact-content SHA-256. Key partitions are derived from the high
32 bits of SHA-256 and compared in bounded streams ordered by hash and UTF-8 key.
Results retain a configurable number of representative differences while exact
totals continue to accumulate. Count-only equality is deliberately reported as
count equality; it does not claim content equality.

Repair is unavailable in count-only mode. For key-set or content-hash runs, the
authoritative reader must include replacement content and the destination must
implement `ISyncRepairSink`. Repairs are idempotent upserts/deletes sent in
bounded batches. A repaired result remains a mismatch and sets
`RequiresVerification`; only a subsequent clean comparison proves convergence.
Repair never advances the source transaction checkpoint.

`SyncPipeline.ReconcileAsync` serializes reconciliation with delivery, exposes
the `Reconciling` state, restores the previous running/paused state after
success, and faults with diagnostics on a reader or repair failure.

## PostgreSQL destination

`BlueTusk.Sync.PostgreSql` stores an opaque materialised document collection and
the pipeline checkpoint in the same PostgreSQL database transaction. It locks
the pipeline row, skips mutation work for an already-applied commit position,
and advances the checkpoint only after every mutation succeeds. A custom
`IPostgreSqlSyncMutationWriter` can target application-specific tables while
retaining the same atomic checkpoint boundary.

The default writer folds repeated operations to the final per-key result while
preserving collection-delete ordering, then sends bounded multi-row commands
instead of one database round trip per document. The live acceptance suite
covers batches beyond one command chunk as well as retry deduplication.

The default document writer also exposes server-partitioned hash reconciliation
and transactional repair. PostgreSQL computes the shared SHA-256 partition in
SQL, streams rows in deterministic order, and applies a bounded repair batch in
one database transaction without touching the pipeline checkpoint. A custom
mutation writer does not advertise reconciliation because BlueTusk cannot infer
how to inspect or repair an application-owned schema.

Snapshot reset, batches, and completion are guarded by the active snapshot epoch
and transform fingerprint. The destination also implements a durable,
deduplicated quarantine sink. Document and transaction byte ceilings are
validated before opening the write transaction.

## NATS JetStream destination

`BlueTusk.Sync.Nats` publishes one versioned binary envelope for each source
transaction. It waits for JetStream's persistence acknowledgement before
returning the exact durable source position, so the Sync pipeline cannot
acknowledge a partially published transaction. Snapshot reset, start, batch,
and completion are also individually durable envelopes.

Every publish uses a fixed-size SHA-256 message ID derived from the pipeline,
source, transform version, and transaction or snapshot identity. JetStream
deduplicates redelivery inside its configured duplicate window; the same stable
identity remains inside the envelope so downstream consumers can deduplicate
beyond that window. BlueTusk still advertises at-least-once delivery.

The envelope has a magic header, explicit format version, bounded payload size,
and SHA-256 integrity footer. Consumers decode it with
`NatsSyncEnvelopeReader`. Mutation records retain stable change or snapshot row
IDs, collection/key routing, content type, partition key, and opaque content.

Provisioning creates a file-backed, limits-retained JetStream stream by default.
The stream carries ownership metadata for the envelope format, pipeline, source,
transform, and subject. Existing metadata and retention settings are validated
before publishing; drift pauses provisioning. A transform fingerprint change
returns `RebuildRequired`, so operators must provision a new stream generation
or explicitly migrate routing rather than reinterpret existing events.

The duplicate window must cover the expected worker recovery interval. Retain
stable IDs downstream even when using a long window because redelivery after
the window is valid at-least-once behaviour. Set `CreateStream` to `false` when
stream creation is managed externally; BlueTusk will still validate the stream
contract.

For local acceptance, start a JetStream-enabled NATS server and run:

```powershell
$env:BLUETUSK_NATS_URL = 'nats://localhost:4222'
dotnet test tests/BlueTusk.Sync.Nats.Tests/BlueTusk.Sync.Nats.Tests.csproj
```

The live suite proves whole-transaction persistence, duplicate recovery after a
destination restart, snapshot lifecycle deduplication, transform-generation
rejection, and stable stream message counts.

## Redis destination

`BlueTusk.Sync.Redis` stores materialised documents and the source checkpoint in
one Redis Lua operation. All keys for a pipeline use the same generated Redis
Cluster hash tag, so an atomic batch never crosses slots. The script checks the
source, transform, monotonic fixed-width commit position, key types, and every
operation before writing; a predictable failure therefore cannot leave a
partial transaction or advance its checkpoint.

Repeated mutations are folded to their final per-key outcome before the script
runs, while the last collection delete remains ordered before subsequent
upserts. Configurable document, transaction-byte, and mutation-count ceilings
bound Lua execution time and Redis argument memory. Transactions beyond those
limits pause safely for operator action instead of blocking Redis indefinitely.

Documents use a small versioned binary value with the stable source change or
snapshot-row ID, content type, partition key, opaque content, and a SHA-256
integrity footer. Applications can inspect a materialised value with
`ReadDocumentAsync` or decode an exported value with
`RedisSyncDocumentReader`.

Snapshot reset atomically removes registered materialisations and clears the
checkpoint before activating a new epoch. Snapshot batches are idempotent, and
completion prevents late batches for the epoch. Quarantine records use a stable
transaction field and `HSET NX`, so retrying quarantine-and-advance cannot add
duplicates.

Redis format version 2 maintains a same-slot sorted reconciliation index beside
each collection hash. Lua writes update the document, index, registry, and CDC
checkpoint atomically. Partition reads use bounded score ranges instead of
rescanning the whole collection, and repair updates the document hash and index
in one Lua call without changing the CDC checkpoint.

The live Redis suite deliberately introduces a wrong-type destination key and
proves preflight rejection occurs before any mutation. It also covers retry,
restart, collection-delete ordering, snapshot reset/completion, quarantine, and
transform rebuild requirements.

## OpenSearch destination

`BlueTusk.Sync.OpenSearch` uses one bounded NDJSON bulk request as the source
transaction delivery unit. It assigns SHA-256 document IDs and PostgreSQL
commit-end LSNs as `external_gte` versions, so a partial request or ambiguous
network failure can safely replay the entire transaction. The per-generation
checkpoint is written only after every bulk item succeeds. A partially accepted
bulk therefore never advances the checkpoint, and its successful items remain
idempotent on retry.

OpenSearch bulk operations are independently applied by the server, so this
connector deliberately does not advertise `TransactionalBatches` or a
co-located checkpoint. Transaction preservation comes from bounded whole-batch
submission, item-by-item response validation, stable external versions, and
checkpoint-after-bulk ordering. Collection resets complete before the
subsequent folded mutations are sent. JSON objects are the only accepted
materialisation content.

Format version 2 creates a generation-owned reconciliation sidecar beside every
materialised index. Each sidecar record contains the original logical key, its
shared unsigned SHA-256 partition hash, the exact content hash, content type,
and routing value; application JSON remains untouched and hashed document IDs
never need to be reversed. A source mutation and its sidecar operation share
the same replay-safe external version in one bulk request. Partial bulk failure
cannot advance the checkpoint, and replay heals either half before progress is
claimed. Count reads reject materialised/sidecar cardinality drift instead of
silently comparing an incomplete view.

Partitioned sidecar scans use bounded `search_after` pages ordered by key hash
and logical key. Repair looks up prior routing, removes an old routed copy when
the routing value changes, and writes the application document plus sidecar
without changing the CDC checkpoint. A subsequent reconciliation run is still
required to prove convergence. Logical keys and page sizes have explicit
operator-configured ceilings so reconciliation cannot create unbounded terms or
responses.

Each transform generation writes to isolated concrete indexes. Stable aliases
are attached to the initial generation, while a rebuild generation remains
invisible. `BeginRebuildAsync` is restart-safe and copies the active collection
registry, snapshot and catch-up writes target only the rebuild indexes,
`VerifyRebuildAsync` checks every active/rebuild count, and
`CompleteRebuildAsync` moves all aliases in one atomic OpenSearch aliases
request. Previous generations are retained until an explicit
`RetireGenerationAsync` call.

The control index owns versioned pipeline, collection, snapshot, checkpoint,
and quarantine documents. Source and transform fingerprints are validated on
every restart; a changed transform returns `RebuildRequired`. Index names,
aliases, and document IDs contain hashes instead of application keys, and
document, mutation-count, and encoded bulk-byte limits are checked before
submission.

For local acceptance, run an OpenSearch node without the security plug-in and
then execute:

```powershell
$env:BLUETUSK_OPENSEARCH_URL = 'http://localhost:9200'
dotnet test tests/BlueTusk.Sync.OpenSearch.Tests/BlueTusk.Sync.OpenSearch.Tests.csproj
```

The CI and local live suite use OpenSearch 3.7.0. It deliberately causes a
mapping conflict after another item has succeeded, repairs and replays the same
transaction, and then covers checkpoint deduplication, collection reset,
snapshot lifecycle, quarantine, restart, transform isolation, count
verification, atomic alias cutover, old-generation retirement, paged
content-hash reconciliation, bounded repair, and checkpoint non-advancement. The design
follows the official [Bulk API](https://docs.opensearch.org/latest/api-reference/document-apis/bulk/)
and [Manage Aliases API](https://docs.opensearch.org/latest/api-reference/alias/aliases-api/)
contracts.
