# Sync concepts

This page gives you the mental model for running a Sync pipeline safely: what
a pipeline is, how duplicates and ordering work, where progress is saved, and
what happens when something fails. Shared terms such as acknowledgement,
checkpoint and source identity are explained in
[core concepts](../getting-started/concepts.md); this page does not repeat
them.

## What is a pipeline?

A pipeline connects one Streams source to one destination through your
transform:

```text
PostgreSQL ──► Streams source ──► transform ──► destination ──► target system
 publication    snapshot, then     rows to       writes, then
 + slot         transactions       mutations     confirms position
                     ▲                                │
                     └────────── acknowledge ◄────────┘
```

| Term | What it is |
| --- | --- |
| Pipeline ID | `SyncPipelineOptions.PipelineId`. The stable name under which the destination stores its checkpoint and transform version. |
| Source | An `IConsistentSnapshotSource` (direct slot) or an `ISyncPipelineSource` (durable relay). See [Streams](../streams/concepts.md). |
| Transform | Your `ISyncTransform`. It turns each source transaction or snapshot batch into `SyncMutation` or `SyncSnapshotMutation` values. |
| Mutation | `Upsert`, `Delete` or `DeleteCollection` for a `Collection` and `Key`, with `Content`, `ContentType` and an optional `PartitionKey`. |
| Transform version | `SyncTransformVersion`: a name plus a SHA-256 fingerprint. The destination stores it on first start. |
| Destination | An `ISyncDestination`, such as `PostgreSqlSyncDestination`. It reports what it supports through `SyncDestinationCapabilities`. |

A pipeline moves through these states (`SyncPipelineState`):

```text
Stopped ─► Provisioning ─► Snapshotting ─► CatchingUp ─► Running
                 │                                         │
                 └─► Rebuilding (transform changed)        ├─► Paused (poison data)
                                                           └─► Faulted (error)
```

`Reconciling` appears while a reconciliation run holds the pipeline.

## Whole transactions, one at a time

Sync never splits a source transaction. Each committed PostgreSQL transaction
becomes one `SyncTransactionBatch`. The destination must confirm the exact
commit position of that transaction before Sync acknowledges it to Streams. If
the destination confirms a different position, Sync stops with
`SyncDestinationDurabilityException`.

How "all or nothing" is achieved depends on the destination:

| Destination | Transaction unit |
| --- | --- |
| PostgreSQL | One database transaction for the writes and the checkpoint. |
| Redis | One Lua script for the writes and the checkpoint. |
| OpenSearch | One bulk request. Items apply separately, so a partial failure is replayed; the checkpoint moves only after every item succeeds. |
| NATS JetStream | One envelope message per transaction. |
| Kafka (New in 1.1.0) | One Kafka producer transaction for the event and the checkpoint record. |
| S3 (New in 1.1.0) | One Parquet object, made visible by a commit manifest written last. |
| Webhooks (New in 1.1.0) | One signed HTTP request per transaction. |

## Why duplicates are safe

Delivery is at least once. If the worker stops after the destination write but
before the acknowledgement, the same transaction arrives again, with the same
transaction ID, commit position and `ChangeId` values. Each destination
recognises the repeat:

| Destination | How a repeat is detected |
| --- | --- |
| PostgreSQL | The checkpoint row in `bluetusk_sync.pipelines` is at or past the commit position, so the writes are skipped. |
| Redis | The script compares the stored checkpoint before writing. |
| OpenSearch | Document IDs are SHA-256 hashes and versions are commit positions (`external_gte`), so an older write cannot replace a newer one. |
| NATS JetStream | The message ID is deterministic; JetStream drops repeats inside `DuplicateWindow`. Consumers keep the ID for longer windows. |
| Kafka | The checkpoint in the compacted `<prefix>.state` topic is reloaded on start. Consumers should read with `read_committed` and keep mutation IDs. |
| S3 | Object keys are deterministic and written with `If-None-Match: *`; an existing commit manifest means "already applied". |
| Webhooks | Each request has a stable `BlueTusk-Delivery-Id`. Your receiver stores it with its result and answers `duplicate` on a repeat. |

Your transform must be deterministic: the same transaction must produce the
same mutations. Never use the current time or a random value in a key.

## Ordering

- A pipeline handles one transaction at a time, in commit order. Retries and
  rate limiting wait in line; nothing overtakes.
- Mutations inside a transaction keep their change order. The default
  PostgreSQL document writer and Redis fold repeated writes to the same key
  into the final result.
- Kafka topics must have exactly one partition per pipeline
  (`PartitionCount` must be `1`).
- Separate pipelines are not ordered relative to each other. To go faster,
  split work into pipelines by table or destination.

## Where is the checkpoint stored?

There are two saved positions:

1. **The destination checkpoint.** Every materialising destination stores the
   last applied commit position next to the data: PostgreSQL in
   `<ControlSchema>.pipelines`, Redis under `KeyPrefix`, OpenSearch in its
   control index, Kafka in the state topic, S3 as commit manifests. This is
   what makes duplicates safe.
2. **The source position.** This decides where reading restarts. It depends
   on how you register the pipeline:

| Registration | On restart |
| --- | --- |
| `AddHostedPipeline` with a `PostgreSqlConsistentSnapshotSource` (direct slot) | Takes a fresh snapshot. With `ExistingSlotMode = RestartSnapshot` it replaces its own inactive slot, resets the destination and copies the tables again. |
| `AddHostedPipelineSource` with a `PostgreSqlRelaySyncPipelineSource` | Resumes from its consumer-group checkpoint in the [durable relay](../streams/durable-relay.md). A completed snapshot is not repeated. |

Use the direct slot for small tables and development. Use the relay for
production: it resumes without re-copying, and several pipelines can share one
slot.

> **Note:** A direct slot only moves forward when PostgreSQL receives feedback.
> See [WAL keeps growing](troubleshooting.md#why-does-wal-keep-growing-with-a-direct-pipeline).

## Changing the transform: rebuild and repair

The transform fingerprint is stored in the destination when the pipeline is
first provisioned. Change it whenever the same row would produce different
output: `SyncTransformVersion.Create("orders", "v2")`. `CompositeSyncTransform`
and `JsonSyncTransformStage` include their configuration in the fingerprint
for you.

On the next start the destination reports `RebuildRequired`. The worker stops
with `SyncTransformVersionMismatchException`, the state becomes `Rebuilding`
and the health check reports unhealthy. Sync never reinterprets existing data
on its own. You choose how to rebuild:

| Destination | How to rebuild |
| --- | --- |
| OpenSearch | Zero-downtime: `SyncRebuildCoordinator` builds a new index generation, verifies it and swaps aliases atomically. Register the cutover with `AddRebuildCutover` or `AddPostgreSqlRelayRebuildCutover`. |
| PostgreSQL | Use a new `PipelineId`, or delete the pipeline's row from `<ControlSchema>.pipelines` (its documents go with it) and restart. The pipeline is provisioned again and re-copies the source. |
| Redis, NATS, Kafka, S3 | Write the new version to a new `KeyPrefix`, stream, `TopicPrefix` or `Prefix`, then move readers. |
| Webhooks | Your receiver decides; it returns its stored fingerprint in `BlueTusk-Transform-Fingerprint`. |

**Reconciliation** checks a destination against the source without a rebuild.
`SyncPipeline.ReconcileAsync` compares counts, key sets or content hashes
(`SyncReconciliationMode`) and can repair differences. The PostgreSQL default
document writer, Redis and OpenSearch support it.

## Failures, retries and poison data

**Destination errors are not retried by default.** A failed write stops the
pipeline unless you register an `ISyncRetryClassifier` that says the error is
transient. Then `SyncRetryOptions` controls the backoff. See
[retries](configuration.md#retry-transient-destination-errors).

**A stopped pipeline stays stopped.** The hosted worker logs
`BlueTusk Sync pipeline {PipelineId} stopped and requires operator action.`,
the `bluetusk_sync` health check turns unhealthy, and other pipelines keep
running. Fix the cause and restart the process. The checkpoint never moved
past the failed transaction, so nothing is lost.

**Poison data** is a transaction your transform cannot map. Throw
`SyncPoisonRecordException` from `TransformTransactionAsync` and Sync applies
`SyncPipelineOptions.PoisonRecordPolicy`:

| Policy | What happens |
| --- | --- |
| `Pause` (default) | The transaction is rejected (nacked) and the pipeline stops. Nothing is skipped. |
| `QuarantineAndPause` | A `SyncQuarantineRecord` is stored, the transaction is acknowledged and the pipeline pauses. |
| `QuarantineAndAdvance` | The record is stored and the pipeline continues with the next transaction. |

Quarantine policies need a durable sink. PostgreSQL, Redis and OpenSearch
destinations act as their own sink; otherwise pass a `quarantineFactory`.
`SyncQuarantineReplayCoordinator` can later re-apply a quarantined transaction
from the durable relay. Any other exception from the transform, and any error
during the snapshot, stops the pipeline without quarantine.

## Next steps

- [Configuration](configuration.md)
- [Troubleshooting](troubleshooting.md)
- [Delivery guarantees](../realtime-platform/contracts.md) across products
- [Full Sync reference](reference.md) for internals and cutover details
