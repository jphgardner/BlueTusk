# Consistent snapshot bootstrap

Streams bootstraps a new logical replication slot with PostgreSQL's exported-snapshot protocol. It does not combine an unrelated table read with a later WAL position.

## Consistency sequence

1. A dedicated logical-replication connection identifies the PostgreSQL system and database and verifies them against `ChangeSourceIdentity`.
2. `CREATE_REPLICATION_SLOT ... LOGICAL pgoutput EXPORT_SNAPSHOT` returns one consistent LSN and exported snapshot name.
3. Every snapshot reader starts a repeatable-read transaction and imports that exact snapshot before issuing its first query.
4. Readers scan declared key columns in deterministic keyset order. Each page uses binary `COPY TO STDOUT`; raw binary field payloads become explicit `ChangeColumnValue` instances without an intermediate CLR materialisation.
5. Bounded parallel readers feed a bounded channel. The consumer sees serial reset, start, batch, and complete callbacks.
6. After snapshot completion, pgoutput starts from the slot's matching consistent LSN. WAL generated during the snapshot has remained retained by the slot, so concurrent writes are delivered after the snapshot without a gap.

The implementation has a PostgreSQL 15–19 acceptance test that creates the slot, commits a write after the consistent point, verifies that the write is absent from the snapshot, and then verifies that it is the first streamed transaction.

## Bounds and backpressure

`PostgreSqlConsistentSnapshotOptions` independently limits:

- rows per keyset COPY page;
- rows and bytes per consumer batch;
- bytes in one row; and
- parallel table readers.

The cross-reader channel is bounded to twice the configured parallelism. A slow consumer therefore propagates backpressure into COPY reads rather than accumulating an unbounded in-memory snapshot. A row larger than the explicit row limit fails the attempt with diagnostics.

Every table requires one or more non-null, immutable ordering keys. The declared key order must match the intended primary or unique key order. PostgreSQL produces the continuation literals with `quote_nullable`; callers never interpolate client-provided key text into keyset SQL.

## Restart semantics

`SnapshotThenStreamCoordinator` treats `SnapshotSessionLostException` before completion as abandonment of the complete epoch. It disposes the attempt, removes only the inactive slot that the same source instance can prove it created, creates a new slot and consistent point, and calls `ResetSnapshotAsync` with a new epoch. It never continues an expired exported snapshot.

The number of complete attempts is bounded by `MaximumSnapshotAttempts`. Failure before PostgreSQL establishes an epoch can be retried without inventing an abandoned epoch identity. Consumer exceptions and permanent configuration failures are not relabelled as session loss.

The snapshot consumer must apply reset and rows idempotently. Once `CompleteSnapshotAsync` succeeds, normal transaction-delivery acknowledgement rules apply. A replication failure after that boundary is a normal checkpoint-based reconnect concern and never causes an implicit full snapshot restart.

## Low-level composition

```csharp
var source = new PostgreSqlConsistentSnapshotSource(
    dataSource,
    new PostgreSqlConsistentSnapshotOptions
    {
        Source = sourceIdentity,
        PublicationNames = ["application_publication"],
        Tables =
        [
            new PostgreSqlSnapshotTable(ordersRelation, [ordersIdOrdinal]),
        ],
        CopyPageRows = 2_048,
        MaximumBatchRows = 512,
        MaximumBatchBytes = 4 * 1024 * 1024,
        MaximumParallelTables = 4,
    },
    replication => CreateCheckpointBeforeFeedbackObserver(replication));

await new SnapshotThenStreamCoordinator(source).RunAsync(consumer, stoppingToken);
```

The observer factory is where a direct consumer composes its fenced checkpoint store and `LogicalReplicationFeedbackSender`. The existing acknowledgement observer still guarantees destination work, then durable compare-and-swap checkpoint, then PostgreSQL feedback.
