# BlueTusk Streams

BlueTusk Streams is the transaction-preserving application CDC layer above `BlueTusk.Replication.PgOutput`. The transaction kernel, typed core mappings, no-gap snapshot bootstrap, hosted consumers, health/telemetry, snapshot/transaction consumer lifecycle, memory/file/PostgreSQL/Redis state stores, direct groups, and PostgreSQL durable relay fan-out are implemented and tested but are not yet published: the first preview remains gated on remaining packaging and preview operational tooling.

## Implemented kernel

- immutable source, relation, column, row, transaction, change, and stable change-ID models;
- explicit value, database-null, not-published, unavailable-old-value, unchanged-TOAST, and decoding-failure column states;
- exact/unknown changed-column sets that require a complete old row before claiming exactness;
- ordinary and streamed transaction assembly by PostgreSQL transaction ID;
- insert, update, delete, truncate, transactional/nontransactional logical message, origin, timestamp, LSN, and ordering preservation;
- bounded change, relation, transaction-memory, individual-record, and total spool-storage accounting;
- versioned disk envelopes with completion footers, per-record CRC32 integrity, atomic `.partial` to `.ready` publication, and pluggable at-rest protection;
- streaming materialisation of spooled changes, with spool deletion tied to delivery acknowledgement, nack, or disposal;
- explicit one-shot acknowledgement semantics that stop a source read if a delivery is skipped or rejected; and
- public API baselines plus fake pgoutput and PostgreSQL 15–19 integration coverage.

Prepared/two-phase transactions deliberately fail with `PreparedTransactionNotSupportedException` until the Phase 4 preview feature is implemented.

## Reading transactions

`PgOutputChangeStream` accepts the decoded pgoutput sequence from a dedicated logical replication connection. This low-level composition is the Phase 1 integration surface; hosted configuration and durable acknowledgement arrive in later phases.

```csharp
var identity = new ChangeSourceIdentity(
    systemIdentifier,
    databaseName,
    slotName,
    canonicalPublicationFingerprint);

IChangeStream changes = new PgOutputChangeStream(
    replication.StartReplicationAsync(slotName, publicationName)
        .DecodePgOutputAsync(),
    identity,
    new TransactionAssemblyOptions
    {
        MaxInMemoryTransactionBytes = 4 * 1024 * 1024,
        MaxTransactionBytes = 1024L * 1024 * 1024,
        MaxSpoolBytes = 10L * 1024 * 1024 * 1024,
        SpoolDirectory = dedicatedSpoolDirectory,
    });

await foreach (var delivery in changes.ReadTransactionsAsync())
{
    await foreach (var change in delivery.Transaction.Changes)
    {
        await ApplyIdempotentlyAsync(change);
    }

    await delivery.AcknowledgeAsync();
}
```

The transaction change set is asynchronous so a large transaction can be read record-by-record from disk. `MaterializeAsync` is a deliberate convenience for bounded transactions and allocates the complete result.

## Failure behavior

Incomplete ordinary or streamed transactions are discarded when the source ends, allowing PostgreSQL to redeliver them from the last durable checkpoint. Stream abort removes its partial spool. Tampered, truncated, incompatible, or wrong-protector spool data fails closed. Limit exhaustion pauses the read with a diagnostic exception; it never drops a change or splits a source transaction.

`CheckpointingChangeDeliveryObserver` implements the locked destination → compare-and-swap checkpoint → PostgreSQL feedback sequence. The checkpoint includes the source system/database/slot/publication identity, output plug-in, mapping fingerprint, acknowledged commit-end LSN, format version, and store generation. `MemoryChangeStreamStateStore` supplies the same monotonic compare-and-swap and fencing behavior as the durable stores for tests and ephemeral development only.

```csharp
var checkpointIdentity = ChangeStreamCheckpoint.CreateInitial(
    identity,
    databaseIdentity,
    "pgoutput",
    mappingFingerprint);
var stateKey = ChangeStreamStateKey.Create(identity, consumerGroup);

await using var acknowledgement =
    await CheckpointingChangeDeliveryObserver.AcquireAsync(
        memoryStateStore,
        stateKey,
        uniqueWorkerId,
        TimeSpan.FromSeconds(30),
        checkpointIdentity,
        new LogicalReplicationFeedbackSender(replication));
```

Only the active fenced lease may mutate a checkpoint. Backward positions, stale generations, incompatible source/mapping identities, and expired owners fail closed. If feedback fails after the checkpoint is durable, retry sends feedback from the stored position without rewriting or advancing the checkpoint. A nack never advances either checkpoint or feedback.

See [checkpoint and lease stores](state-stores.md) for backend guarantees, file-store deployment constraints, and the custom-store conformance kit.

See the [PostgreSQL durable relay](durable-relay.md) for source append ordering, group fan-out/replay, retention, storage bounds, and health signals.

See [typed mappings](typed-mappings.md) for convention and explicit mappings, schema and mapping fingerprints, partial-row safety, decoding policy, and the snapshot consumer lifecycle.

See [consistent snapshot bootstrap](snapshot-bootstrap.md) for exported-snapshot lifetime, keyset binary COPY, bounded parallelism, restart epochs, and the PostgreSQL 15–19 no-gap proof.

See [hosting and observability](hosting-observability.md) for hosted-worker registration, health states, readiness behavior, and exporter-neutral OpenTelemetry instruments.

See [CloudEvents](cloudevents.md) for transaction-preserving structured JSON, deterministic IDs, integrity-checked payloads, and acknowledgement responsibility.

See the [validation and provisioning CLI](cli.md) for idempotent source/relay setup, safe shared-control checks, and machine-readable diagnostic codes.

See [Aspire integration](aspire.md) for secret-preserving source/control resource wiring and explicit relay versus direct delivery configuration.

See the [snapshot-then-stream sample](sample.md) for a runnable hosted consumer using exported-snapshot binary COPY followed by transaction-preserving CDC.

## Performance baseline

The checked-in Ryzen 7 5800X/.NET 10 ShortRun measures 422 ns and 852 B per change for a materialised 1,000-insert transaction. A 4 MiB durable spill, integrity check, streamed read, flush, and cleanup measures 38.3 ms and 12.1 MiB. See the [benchmark report](../../benchmarks/baselines/windows-ryzen7-5800x-dotnet10/results/BlueTusk.Benchmarks.StreamsTransactionBenchmarks-report-github.md). ShortRun values guide regression work and are not universal production claims.
