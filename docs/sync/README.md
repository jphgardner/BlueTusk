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

The connector packages and shared conformance kit are built in subsequent Phase
5 slices. The Sync release train remains non-publishable until PostgreSQL, NATS
JetStream, Redis, and OpenSearch all pass snapshot-plus-stream recovery.

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
