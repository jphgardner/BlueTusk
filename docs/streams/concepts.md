# Streams concepts

This page explains the model behind Streams so you can write a consumer that is
correct after crashes and restarts. It builds on the shared
[core concepts](../getting-started/concepts.md): logical replication,
publications, slots, acknowledgement, checkpoints, source identity,
snapshot-then-stream and the relay. Read that page first if those terms are new.

## How a change reaches your code

```text
PostgreSQL commit
   │  WAL, filtered by your publication, read through your replication slot
   ▼
pgoutput messages ──► Streams assembles one complete transaction
                         (in memory, or spooled to disk when large)
   ▼
ChangeTransactionDelivery ──► your code does its work
                                  │
                                  ▼ delivery.AcknowledgeAsync()
                       delivery observer: save checkpoint, then confirm
                       the position to PostgreSQL so the slot can free WAL
```

## Transactions and changes

Streams never splits a source transaction. Each delivery holds one
`ChangeTransaction`:

| Member | Meaning |
| --- | --- |
| `TransactionId` | PostgreSQL transaction ID (xid). |
| `CommitEndPosition` | WAL position just after the commit. This is the position a checkpoint stores. |
| `CommitTimestamp`, `Origin` | When it committed, and its replication origin if any. |
| `Outcome` | `Committed`, or `Prepared` / `RolledBack` for [two-phase transactions](prepared-transactions.md). |
| `Changes` | A `ChangeSet` of the changes in commit order. |

Each change is an `InsertChange`, `UpdateChange`, `DeleteChange`,
`TruncateChange` or `LogicalMessageChange`. Every change has a `ChangeId` made
of the source identity, commit-end position, transaction ID and ordinal. The ID
is the same every time the change is delivered, so use it as the
de-duplication key in your destination.

`ChangeSet` is an asynchronous sequence. A large transaction is read
record by record from disk, so iterate it with `await foreach`.
`MaterializeAsync()` loads every change into a list; use it only when you know
the transaction is small. `Count`, `EstimatedBytes` and `IsSpooled` are
available without reading the changes.

## What a column value can be

A row is a `ChangeRow`: one `ChangeColumnValue` per column, indexed by ordinal
or name. PostgreSQL does not always send a value, so each value has an explicit
`State`:

| `ChangeColumnState` | When you see it |
| --- | --- |
| `Value` | A real value. `Data` holds the bytes and `Encoding` says `Text` or `Binary`. |
| `DatabaseNull` | The column is SQL `NULL`. |
| `NotPublished` | The publication's column list excludes this column. |
| `OldValueUnavailable` | An old row (update or delete) without this column. With the default replica identity only key columns are logged. |
| `UnchangedToast` | A large (TOASTed) value that the update did not change, so PostgreSQL did not resend it. |
| `DecodingFailure` | The value could not be decoded. `DecodingError` explains why. |

Never treat a missing value as `NULL` or as a default. To receive complete old
rows, run `ALTER TABLE app.orders REPLICA IDENTITY FULL;` (this makes PostgreSQL
log more WAL). An update's `ChangedColumns` set has `IsExact = true` only when
Streams had a complete old row to compare.

[Typed mappings](typed-mappings.md) keep these states: a typed row has
`HasValue = false` when any mapped column is missing.

## Acknowledge after your work is durable

Streams hands you one transaction at a time. The rule is:

1. Do your work for the whole transaction and make it durable.
2. Call `delivery.AcknowledgeAsync()`.
3. Only then ask for the next transaction.

If you ask for the next transaction without settling the current one, the
stream stops with `ChangeDeliveryNotAcknowledgedException`. A delivery can be
settled once. `NackAsync()` (or disposing the delivery) rejects it: the stream
stops, nothing is checkpointed, and the transaction is delivered again on the
next start.

Acknowledging calls the stream's **delivery observer**. The observer decides
what "done" means:

| Observer | On acknowledge |
| --- | --- |
| `CheckpointingChangeDeliveryObserver` | Renews the lease, saves the checkpoint with compare-and-swap, then sends the position to PostgreSQL. |
| `PostgreSqlRelayChangeDeliveryObserver` | Appends the transaction to the [durable relay](durable-relay.md), then sends the position to PostgreSQL. |
| None | The stream confirms the commit position to PostgreSQL itself. Nothing is saved, so a restart cannot resume from a known position. |

### How the slot releases WAL

PostgreSQL keeps WAL from the slot's `confirmed_flush_lsn` onwards, and that
position only moves when the consumer confirms a position. A
`PgOutputChangeStream` that reads from a BlueTusk replication connection
confirms it as follows:

- **With no observer**, the stream confirms each transaction's commit-end
  position after `AcknowledgeAsync` completes. This covers streams you build
  over `StartReplicationAsync(...).DecodePgOutputAsync()`, the stream inside
  `PostgreSqlConsistentSnapshotSource`, hosted consumers, and Sync pipelines
  without an `observerFactory`.
- **With an observer**, the observer owns confirmation. The two built-in
  observers confirm after their durable write. A custom observer that never
  sends feedback holds WAL until the slot is dropped.

A delivery that is rejected, disposed or never acknowledged is not confirmed,
so PostgreSQL sends it again after a restart.

When a consumer must resume after a restart, use
`CheckpointingChangeDeliveryObserver` and a [state store](state-stores.md).

> **New in 1.1.0:** In 1.0.0 and 1.1.0-rc.1 a stream without an observer never
> confirmed a position, so its slot kept all WAL. An observer you added only to
> send feedback still works, but you can remove it.

## Checkpoints, leases and fencing

A **state store** keeps one record per source and **consumer group** (a name
you choose for one independent reader). The record holds:

- the **checkpoint**: the last acknowledged commit-end position, plus the
  source identity, output plug-in, a mapping fingerprint and a format version;
- a **generation** number that increases with every write; and
- the current **lease**: owner ID, expiry time and **fencing token**.

Only the lease owner may write. Every write is a compare-and-swap on the
generation, a checkpoint can never move backwards, and a worker whose lease
expired is rejected even if it is still running. A new owner always receives a
higher fencing token than any earlier owner.

`CheckpointingChangeDeliveryObserver` renews its lease on a timer, three times
per lease duration, whether or not transactions arrive. An idle worker, or one
still working on a slow transaction, keeps its lease. Each acknowledgement also
renews and checks the lease first. If the lease expired (for example because
the store could not be reached for longer than the lease duration) or another
worker took it over, the acknowledgement fails with
`ChangeStreamLeaseLostException` before any checkpoint is written. Disposing
the observer stops renewal and releases the lease.
`PostgreSqlRelayChangeDeliveryObserver` renews its source lease the same way.

> **New in 1.1.0:** In 1.0.0 and 1.1.0-rc.1 the observers renewed the lease
> only when you acknowledged, so an idle worker needed its own renewal timer.
> You can remove that timer.

Choose a store in [checkpoint and lease stores](state-stores.md).

## Source identity

`ChangeSourceIdentity` names where changes come from: the PostgreSQL system
identifier, database, slot name and a publication fingerprint. Its `Fingerprint`
is part of the state-store key and of every `ChangeId`.

If any part changes, for example after a restore into a new cluster, the old
checkpoint no longer matches. Streams refuses to reuse it instead of silently
skipping or repeating data. A checkpoint whose plug-in or mapping fingerprint
differs fails with `ChangeStreamCheckpointMismatchException`. For the
publication fingerprint, use either the publication name or the canonical value
that [`bluetusk-streams validate`](cli.md) prints as `BTS008`, and keep it stable.

## Large transactions are spooled to disk

Streams buffers each transaction in memory up to
`MaxInMemoryTransactionBytes` (4 MiB by default), then writes it to a spool file
in `SpoolDirectory`. Spool records carry checksums; a damaged file stops the
stream with `TransactionSpoolIntegrityException`. A spool file is deleted when
its delivery is acknowledged, rejected or disposed.

When a limit is reached (bytes, changes or relations per transaction, or total
spool space) the stream stops with `TransactionAssemblyLimitExceededException`
or `TransactionSpoolLimitExceededException`. It never drops a change or splits
a transaction. Raise the limit and restart. See
[configuration](configuration.md#transaction-assembly-and-spooling).

## Snapshot, then stream

A new consumer often needs the rows that already exist. The snapshot source
creates the slot, copies the tables from the same consistent point, then streams
from exactly that point. Your `IChangeStreamConsumer` receives:

```text
ResetSnapshotAsync → StartSnapshotAsync → ConsumeSnapshotBatchAsync (repeated)
  → CompleteSnapshotAsync → ConsumeTransactionAsync (repeated)
```

Each attempt has a new **snapshot epoch**. If the copy fails, the next attempt
calls `ResetSnapshotAsync` with the abandoned epoch so you can discard partial
rows. See [snapshot and catch-up](snapshot-bootstrap.md).

## Direct consumers and the relay

```text
Direct:  slot A ──► consumer group "search"
         slot B ──► consumer group "audit"          (one slot per group)

Relay:   slot ──► source worker ──► relay tables ──► group "search"
                                               └──► group "audit"
```

A slot can be read by one connection at a time. With direct consumers, each
group needs its own slot, and each slot holds WAL until its consumer
catches up. The [durable relay](durable-relay.md) reads one slot, stores
transactions in PostgreSQL, and lets each group read and acknowledge at its own
pace.

## Restarts and redelivery

| The worker stops... | On restart |
| --- | --- |
| Before your work is durable | The transaction is delivered again. |
| After your work, before the checkpoint is saved | The transaction is delivered again. Your work must be safe to repeat. |
| After the checkpoint, before PostgreSQL is told | Pass the checkpoint as the start position and PostgreSQL skips what you already acknowledged. |
| During a snapshot copy | A new snapshot epoch starts. Discard rows from older epochs in `ResetSnapshotAsync`. |

Delivery is at least once. Use `ChangeId` for idempotent writes, or save your
progress in the same database transaction as your work. Read
[delivery guarantees](../realtime-platform/contracts.md) for the boundaries of
each product.

## Related pages

- [Configuration](configuration.md): every option and default
- [Troubleshooting](troubleshooting.md)
- [Format compatibility registry](format-compatibility.md)
