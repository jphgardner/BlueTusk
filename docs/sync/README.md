# Keep another system in sync

BlueTusk Sync takes complete committed transactions from Streams and applies
them to another system. Use it for search indexes, caches, event buses, another
PostgreSQL database, webhooks, or an object-store lake.

If you only need to observe changes, start with [Streams](../streams/README.md).
If you need to update connected users, use [Live](../live/README.md).

## What you choose

Every Sync pipeline has four parts:

1. **Source** — a Streams snapshot-and-change source.
2. **Transform** — application code that turns source rows into destination
   mutations.
3. **Destination** — PostgreSQL, Redis, NATS, Kafka, OpenSearch, S3/Parquet, or
   a signed webhook.
4. **Pipeline identity** — a stable name and transform version used for safe
   recovery.

BlueTusk keeps a source transaction intact. It acknowledges that transaction
only after the destination confirms the exact commit position.

## 1. Choose a destination

| Destination    | Good fit                                     | Recovery model                                        |
| -------------- | -------------------------------------------- | ----------------------------------------------------- |
| PostgreSQL     | Read models in another database              | Mutation and checkpoint commit atomically.            |
| Redis          | Keyed cache or lookup state                  | Same-slot atomic script applies state and checkpoint. |
| OpenSearch     | Search index                                 | Stable versions make replay converge safely.          |
| NATS JetStream | Durable event distribution                   | Stable message identity plus broker deduplication.    |
| Kafka          | Partitioned event and compacted state topics | Transactional publication and durable state.          |
| S3/Parquet     | Analytics lake                               | Versioned objects and commit manifests.               |
| Signed webhook | External HTTP integration                    | Receiver deduplicates the signed delivery identity.   |

Install `BlueTusk.Sync`, `BlueTusk.Sync.DependencyInjection`, and only the
destination package you selected.

## 2. Register one hosted pipeline

```csharp
builder.Services.AddSingleton<OrderTransform>();
builder.Services.AddSingleton(ordersDestination);

builder.Services.AddBlueTuskSync()
    .AddHostedPipeline<OrderTransform, PostgreSqlSyncDestination>(
        new SyncPipelineOptions
        {
            PipelineId = "orders-read-model",
            Retry = new SyncRetryOptions
            {
                MaximumAttempts = 5,
                InitialDelay = TimeSpan.FromMilliseconds(200),
                MaximumDelay = TimeSpan.FromSeconds(10),
            },
        },
        sourceIdentity,
        services => CreateOrdersSnapshotAndStreamSource(services));
```

`CreateOrdersSnapshotAndStreamSource` should use the no-gap Streams bootstrap
described in [snapshot and catch-up](../streams/snapshot-bootstrap.md). Do not
combine an unrelated table export with a later WAL position.

The transform implements `ISyncTransform` and returns stable, keyed
`SyncMutation` values. Treat its name and version as persisted data:

```csharp
public sealed class OrderTransform : ISyncTransform
{
    public SyncTransformVersion Version { get; } =
        SyncTransformVersion.Create("orders", "v1");

    public ValueTask<IReadOnlyList<SyncMutation>> TransformTransactionAsync(
        ChangeTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        // Map inserts, updates, deletes, and truncates deliberately.
        return MapOrdersAsync(transaction, cancellationToken);
    }
}
```

Use the exact interface signature from the installed package; the
[full Sync reference](reference.md) contains destination-specific constructors
and complete transformation rules.

## 3. Prove recovery before traffic

Test these cases with the real destination:

1. stop the worker after the destination write but before acknowledgement;
2. restart and confirm the same transaction is harmlessly redelivered;
3. reject one mutation in a multi-row transaction;
4. change the transform version and verify that a rebuild is required; and
5. disconnect the destination until retries are exhausted.

## The guarantee in plain language

BlueTusk never claims that a transaction is finished before the destination's
documented durable boundary. A crash can cause the last unconfirmed transaction
to arrive again. Official connectors use atomic checkpoints or stable external
versions/identities so that replay is safe. Your webhook or downstream NATS
consumer must also persist the stable delivery identity with its business
effect.

## Production defaults

- Use the PostgreSQL durable relay when multiple consumers need one source slot.
- Bound batch bytes, retry time, destination concurrency, and quarantine size.
- Pause on poison data until an operator deliberately enables quarantine.
- Alert on checkpoint lag, retries, throttling, quarantine, and rebuild state.
- Reconcile destination contents and rehearse a generation rebuild before launch.

Read [delivery guarantees](../realtime-platform/contracts.md) next. Use the
[full Sync reference](reference.md) for transformations, every connector,
reconciliation, rebuilds, and cutover internals.
