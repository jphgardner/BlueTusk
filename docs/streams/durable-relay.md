# PostgreSQL durable relay

The PostgreSQL relay lets one logical replication slot feed multiple independently checkpointed consumer groups. The source worker remains a normal transaction-preserving Streams consumer; `PostgreSqlRelayChangeDeliveryObserver` changes its acknowledgement target from an application destination to a durable relay append.

The ordering is fixed:

1. renew and verify the fenced source-owner lease;
2. encode and append the complete source transaction;
3. update the relay source watermark in the same PostgreSQL transaction;
4. commit the control-store transaction; and
5. send PostgreSQL replication feedback.

If the worker fails before the relay commit, PostgreSQL redelivers. If feedback fails after the commit, retry finds the identical transaction identity and envelope and returns `AlreadyPresent`; it does not duplicate relay storage. A duplicate identity with different bytes fails as an integrity violation.

## Storage model

The configured control schema contains versioned storage metadata, source registrations and epochs, binary transaction envelopes, consumer groups/checkpoints/fencing leases, snapshot runs, dead letters, and retention watermarks. The envelope is a bounded versioned binary format with a SHA-256 integrity hash. It preserves source and transaction metadata, table/type/column metadata, every explicit row state, changed-column exactness, truncates, and logical messages.

`MaxEnvelopeBytes` bounds one transaction and `MaxRelayStorageBytes` atomically reserves total relay storage before insert. Read batches are bounded by transaction count and bytes. The first transaction may exceed the requested batch-byte target because source transactions are never split; it still cannot exceed the configured envelope limit.

## Consumer groups

Create groups at the earliest retained position or at the latest source position. Each group owns its own database-clock lease, monotonically increasing fencing token, checkpoint sequence, and compare-and-swap generation. Reading with a stale lease fails; acknowledgement rechecks the lease and known relay sequence inside a locked PostgreSQL transaction.

```csharp
var group = await relay.CreateConsumerGroupAsync(
    sourceRegistration,
    "search-index",
    ChangeRelayConsumerGroupStart.EarliestAvailable);
var lease = await relay.AcquireConsumerGroupAsync(
    group,
    uniqueWorkerId,
    TimeSpan.FromSeconds(30));

var batch = await relay.ReadConsumerGroupAsync(
    lease!,
    maxTransactions: 128,
    maxBytes: 8 * 1024 * 1024);

foreach (var record in batch.Records)
{
    await ApplyTransactionIdempotentlyAsync(record.Transaction);
    await relay.AcknowledgeConsumerGroupAsync(
        lease!,
        batch.Group.StoreGeneration,
        record.Sequence);
}
```

Applications should normally acknowledge and refresh the returned group generation after each record. Stable `ChangeId` values remain the destination deduplication key; the relay and Streams still promise at-least-once delivery, not exactly once.

## Retention and health

A transaction is eligible for deletion only when every active group to which it applies has checkpointed past it and the resume-retention window has elapsed. A group created at `Latest` does not pin older transactions. Retention updates the persisted byte reservation and high-watermark in the same database transaction.

`GetMetricsAsync` reports count, bytes, sequence bounds, minimum group checkpoint, and oldest applicable unacknowledged age. `GetHealthAsync` adds WAL lag against the source's durably appended commit position and explicit danger flags for WAL lag, acknowledgement age, and relay capacity.

The relay's control schema must not be part of the source publication. Use a separate control data source by default and run publication validation during provisioning. The live acceptance suite covers append/retry, two-group fan-out, replay, fencing, retention, capacity exhaustion, integrity decoding, and health signals on PostgreSQL 15–19.
