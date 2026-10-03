# Snapshot and catch-up

This guide shows you how to copy the rows that already exist in your tables and
then stream every later change, with no gap and no overlap between the two.

Use it when a new consumer needs the current state, for example to build a read
model or search index from scratch. If you only need changes from now on, the
[quick start](quickstart.md) is enough.

## How it works

1. Streams opens a replication connection and checks that the server and
   database match your `ChangeSourceIdentity`.
2. It creates the replication slot with an **exported snapshot**. PostgreSQL
   returns a consistent WAL position and a snapshot name for that same moment.
3. Each table is copied inside a repeatable-read transaction that imports that
   snapshot, in key order, using binary `COPY`.
4. When every table is copied, streaming starts from the slot's consistent
   position. Changes committed during the copy were kept by the slot, so they
   arrive next.

A change committed after the consistent point is never in the copy, and is
always the first thing streamed.

## 1. Describe the tables to copy

Streams needs each table's columns, PostgreSQL type OIDs and key. Look them up:

```sql
SELECT row_number() OVER (ORDER BY attnum) - 1 AS ordinal,
       attname AS name, atttypid AS type_oid, atttypmod AS type_modifier
FROM pg_attribute
WHERE attrelid = 'app.orders'::regclass AND attnum > 0 AND NOT attisdropped
ORDER BY attnum;
```

Then describe the table. Mark the key columns with `IsKey: true`; they must be
non-null and must not change (a primary key is ideal):

```csharp
var ordersTable = new ChangeTable(
    relationId: 0,
    "app",
    "orders",
    replicaIdentity: 'd',
    [
        new ChangeColumn(0, "id", TypeOid: 20, TypeModifier: -1, IsKey: true),
        new ChangeColumn(1, "description", TypeOid: 25, TypeModifier: -1, IsKey: false),
    ]);
```

The copy reads exactly the columns you list, in that order.

## 2. Write the consumer

Implement `IChangeStreamConsumer`. Streams calls it in this order: reset, start,
one call per batch of copied rows, complete, then one call per streamed
transaction.

```csharp
sealed class ReadModelConsumer : IChangeStreamConsumer
{
    public ValueTask ResetSnapshotAsync(SnapshotReset reset, CancellationToken cancellationToken = default)
    {
        // Discard rows from reset.AbandonedEpoch (if any) and start epoch reset.Epoch.
        Console.WriteLine($"Reset: {reset.Reason}");
        return ValueTask.CompletedTask;
    }

    public ValueTask StartSnapshotAsync(SnapshotStart start, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"Copying {start.TableCount} table(s)");
        return ValueTask.CompletedTask;
    }

    public ValueTask ConsumeSnapshotBatchAsync(ChangeSnapshotBatch batch, CancellationToken cancellationToken = default)
    {
        // Upsert batch.Rows. Each row has a stable SnapshotRowId within the epoch.
        Console.WriteLine($"Copied {batch.Rows.Count} row(s) from {batch.Table}");
        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteSnapshotAsync(SnapshotComplete complete, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"Snapshot complete: {complete.RowCount} row(s)");
        return ValueTask.CompletedTask;
    }

    public async ValueTask ConsumeTransactionAsync(
        ChangeTransactionDelivery delivery, CancellationToken cancellationToken = default)
    {
        // Apply the transaction, then acknowledge it.
        Console.WriteLine($"Transaction {delivery.Transaction.TransactionId}: {delivery.Transaction.Changes.Count} change(s)");
        await delivery.AcknowledgeAsync(cancellationToken);
    }
}
```

Make every callback safe to repeat. Copied rows arrive as binary values
(`ChangeValueEncoding.Binary`); a [typed mapping](typed-mappings.md#snapshot-rows)
can decode them (text columns need a decoder).

## 3. Copy, then stream, then resume

The code below runs the snapshot on the first start. It also saves a checkpoint
for each streamed transaction, so later starts resume from the slot instead of
copying the table again. `store` is a PostgreSQL state store and `server` comes
from `IdentifySystemAsync()`, as in the [quick start](quickstart.md).

```csharp
var sourceIdentity = new ChangeSourceIdentity(
    server.SystemIdentifier, server.DatabaseName!, "orders_snapshot", "orders_publication");

var feedback = new DeferredFeedbackSender();
await using var checkpoints = await CheckpointingChangeDeliveryObserver.AcquireAsync(
    store,
    ChangeStreamStateKey.Create(sourceIdentity, "read-model"),
    workerId,
    TimeSpan.FromSeconds(30),
    ChangeStreamCheckpoint.CreateInitial(
        sourceIdentity, server.SystemIdentifier, "pgoutput", "read-model-v1"),
    feedback);

if (checkpoints.Checkpoint is { } checkpoint)
{
    // A previous run finished its snapshot and acknowledged at least one
    // transaction: resume from the slot instead of copying the table again.
    await ResumeFromCheckpointAsync(checkpoint, checkpoints, stoppingToken);
    return;
}

// ordersTable is the ChangeTable from step 1.
var snapshotSource = new PostgreSqlConsistentSnapshotSource(
    dataSource,
    new PostgreSqlConsistentSnapshotOptions
    {
        Source = sourceIdentity,
        PublicationNames = ["orders_publication"],
        Tables = [new PostgreSqlSnapshotTable(ordersTable, keyOrdinals: [0])],
        ExistingSlotMode = PostgreSqlExistingSnapshotSlotMode.RestartSnapshot,
    },
    observerFactory: replication =>
    {
        feedback.Connection = replication;
        return checkpoints;
    });

await new SnapshotThenStreamCoordinator(snapshotSource).RunAsync(consumer, stoppingToken);
```

`ResumeFromCheckpointAsync` is your own method: the quick start's read loop,
started at `checkpoint.AcknowledgedCommitPosition` with `checkpoints` as the
observer.

The snapshot source opens its own replication connection, so the feedback
sender is connected when streaming starts:

```csharp
// Sends feedback on the replication connection that the snapshot source opens.
sealed class DeferredFeedbackSender : IReplicationFeedbackSender
{
    public BlueTuskReplicationConnection? Connection { get; set; }

    public ValueTask SendFeedbackAsync(
        BlueTuskLogSequenceNumber position,
        CancellationToken cancellationToken = default) =>
        new LogicalReplicationFeedbackSender(
                Connection ?? throw new InvalidOperationException("Streaming has not started."))
            .SendFeedbackAsync(position, cancellationToken);
}
```

Add `using BlueTusk.Replication;` and `using BlueTusk.TypeSystem;` for these
types. With this composition, the first run prints:

```text
Reset: Initial consistent snapshot.
Copying 1 table(s)
Copied 7 row(s) from app.orders
Snapshot complete: 7 row(s)
Transaction 1188: 1 change(s)
```

> **Note:** Do not create the slot yourself. The snapshot source must create it
> to get the exported snapshot. Provision only the publication (for example
> with `bluetusk-streams provision --skip-slot`).

To run a snapshot consumer inside a .NET host with a health check, see
[hosting and observability](hosting-observability.md).

## What happens when something fails

**The copy fails part-way.** If the snapshot session is lost before
`CompleteSnapshotAsync`, Streams throws away that attempt, drops the slot it
created, creates a new one and calls `ResetSnapshotAsync` with a new epoch and
the abandoned one. It never continues an expired snapshot. After
`MaximumSnapshotAttempts` attempts (3 by default, set on
`SnapshotThenStreamOptions`) it stops with
`SnapshotRestartLimitExceededException`. Errors thrown by your consumer are not
retried.

**The process restarts and the slot already exists.** A new process cannot
prove it created the slot, so the default, `ExistingSlotMode = Fail`, refuses to
touch it and the run stops with `SnapshotRestartLimitExceededException`
(the inner error says the slot already exists). With
`ExistingSlotMode = RestartSnapshot`, Streams drops the slot only if it is
inactive, logical, uses `pgoutput` and belongs to the configured database, then
starts a fresh snapshot epoch. Choose `RestartSnapshot` only if your reset and
copy are idempotent.

**Streaming fails after the snapshot completed.** That is a normal restart:
resume from the checkpoint as shown above. It never triggers a new snapshot by
itself.

## Tune the copy

`PostgreSqlConsistentSnapshotOptions` controls memory use and parallelism:

| Option | Default | Effect |
| --- | --- | --- |
| `CopyPageRows` | 2,048 | Rows per keyset `COPY` page. |
| `MaximumBatchRows` | 512 | Rows per `ConsumeSnapshotBatchAsync` call. |
| `MaximumBatchBytes` | 4 MiB | Bytes per batch. |
| `MaximumRowBytes` | 4 MiB | Largest single row. A larger row stops the attempt. |
| `MaximumParallelTables` | 4 | Tables copied at the same time. |

A slow consumer slows the copy down instead of filling memory: the hand-off
between readers and your consumer holds at most twice `MaximumParallelTables`
batches. See [configuration](configuration.md#snapshot-bootstrap) for every
option.

## Related pages

- [Concepts: snapshot, then stream](concepts.md#snapshot-then-stream)
- [Sample worker](sample.md)
- [Troubleshooting](troubleshooting.md)
