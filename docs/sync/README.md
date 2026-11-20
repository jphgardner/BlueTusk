# BlueTusk Sync

BlueTusk Sync keeps another system up to date with your PostgreSQL data. It
reads committed transactions from [Streams](../streams/README.md), turns each
row into a keyed document with your code, and writes the result to another
PostgreSQL database, Redis, NATS, OpenSearch, Kafka, S3 or a webhook.

Install matching 1.2.0 Sync and destination packages. See
[the installation guide](../getting-started/install.md) and
[runtime compatibility](../getting-started/runtime-compatibility.md).

## When should I use Sync?

Use Sync when a copy of your data must follow the database: a read model in
another database, a cache, a search index, an event topic, a data lake or a
partner system.

Use something else when:

- you want to run your own code for each change: use
  [Streams](../streams/README.md) directly;
- you want to push query results to connected users: use
  [Live](../live/README.md).

## Destinations

Install `BlueTusk.Sync.DependencyInjection` and the package for your
destination. Each destination package brings in `BlueTusk.Sync`.

| Package | Writes |
| --- | --- |
| `BlueTusk.Sync.PostgreSql` | Rows in your own tables (with a custom writer) or JSON documents, plus the checkpoint, in one transaction |
| `BlueTusk.Sync.Redis` | Documents in Redis hashes, plus the checkpoint, in one Lua script |
| `BlueTusk.Sync.Nats` | One JetStream message per transaction, with a stable message ID |
| `BlueTusk.Sync.OpenSearch` | Documents in versioned indexes behind aliases; supports zero-downtime rebuilds |
| `BlueTusk.Sync.Kafka` | One event per transaction plus a checkpoint in a compacted state topic, in one Kafka transaction |
| `BlueTusk.Sync.S3` | One immutable Parquet object per transaction, made visible by a commit manifest |
| `BlueTusk.Sync.Webhooks` | One signed HTTPS request per transaction |

Supporting packages:

| Package | Purpose |
| --- | --- |
| `BlueTusk.Sync` | Pipeline, transforms, retries, reconciliation and rebuilds. |
| `BlueTusk.Sync.DependencyInjection` | Hosted workers, health check, metrics. |
| `BlueTusk.Sync.Aspire` | Passes source and destination connections to an Aspire worker. |
| `BlueTusk.Sync.Testing` | Conformance tests for your own destination. |

## What does Sync guarantee?

Sync applies each source transaction as a whole, in commit order, and moves
its checkpoint only after the destination confirms that exact transaction.
Delivery is at least once: after a crash the last unconfirmed transaction can
arrive again, and every destination recognises the repeat by its stable
identity. PostgreSQL and Redis store the data and the checkpoint atomically;
the other destinations use stable IDs, versions or commit markers. For
NATS, Kafka and webhooks, your consumer should also keep the delivery ID.
Details: [why duplicates are safe](concepts.md#why-duplicates-are-safe) and
[delivery guarantees](../realtime-platform/contracts.md).

## What does it look like?

You register a destination and a hosted pipeline that names your transform:

```csharp
builder.Services.AddSingleton(new PostgreSqlSyncDestination(new PostgreSqlSyncOptions
{
    DestinationDataSource = target,
}));
builder.Services.AddBlueTuskSync()
    .AddHostedPipeline<OrdersTransform, PostgreSqlSyncDestination>(
        new SyncPipelineOptions { PipelineId = "orders-replica" },
        sourceIdentity,
        _ => new PostgreSqlConsistentSnapshotSource(source, snapshotOptions));
```

`OrdersTransform` implements `ISyncTransform` and maps each change to an
upsert or delete. The [quick start](quickstart.md) has the complete program.

## Next steps

- [Quick start](quickstart.md): copy a table into another database in about
  10 minutes.
- [Concepts](concepts.md): pipelines, idempotency, ordering, checkpoints,
  rebuilds and failure handling.
- [Configuration](configuration.md): every option, per destination.
- [Troubleshooting](troubleshooting.md): stopped pipelines, duplicates, lag
  and permissions.
- [Full Sync reference](reference.md): engineering detail for each connector.
