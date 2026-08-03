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

PostgreSQL, NATS JetStream, and Redis connector slices are implemented.
OpenSearch, the cross-destination conformance kit, reconciliation, and rebuild
orchestration remain gated work. The Sync release train remains non-publishable
until all four destinations pass snapshot-plus-stream recovery.

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

The live Redis suite deliberately introduces a wrong-type destination key and
proves preflight rejection occurs before any mutation. It also covers retry,
restart, collection-delete ordering, snapshot reset/completion, quarantine, and
transform rebuild requirements.
