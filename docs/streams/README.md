# BlueTusk Streams

BlueTusk Streams turns PostgreSQL logical replication into complete committed
transactions an application can process and acknowledge. It handles large
transaction spooling, source identity, checkpoints, leases, restart, and a
no-gap initial snapshot.

Use Streams when application code needs a reliable change feed. Use the lower
level [replication API](../replication/README.md) only when you need raw protocol
messages.

## Run the sample

The sample creates a hosted snapshot-then-stream consumer and prints each
committed transaction:

```powershell
$env:BLUETUSK_STREAMS_SOURCE = "Host=localhost;Database=app;Username=replicator;Password=local-only;SSL Mode=Disable;Channel Binding=Disable"
$env:BlueTusk__Streams__Slot = "orders_sample"
$env:BlueTusk__Streams__Publications__0 = "app_changes"
dotnet run --project samples/BlueTusk.Samples.Streams
```

Create the PostgreSQL publication and replication role first. The TLS-disabled
connection is for an isolated local database only.

## The processing rule

```csharp
await foreach (var delivery in changes.ReadTransactionsAsync())
{
    await ApplyTheCompleteTransactionAsync(delivery.Transaction);
    await SaveCheckpointAsync(delivery.Transaction.CommitEndPosition);
    await delivery.AcknowledgeAsync();
}
```

Do not acknowledge before the downstream effect and checkpoint are durable. A
crash can redeliver the last unconfirmed transaction, so the downstream write
must use stable change identities or an atomic checkpoint.

For a new data set, use [snapshot and catch-up](snapshot-bootstrap.md) rather
than combining an unrelated table export with a later WAL position.

## What Streams provides

- immutable source, relation, column, row, transaction, change, and stable change-ID models;
- explicit value, database-null, not-published, unavailable-old-value, unchanged-TOAST, and decoding-failure column states;
- exact/unknown changed-column sets that require a complete old row before claiming exactness;
- ordinary, streamed, and opt-in prepared transaction assembly by PostgreSQL transaction ID;
- insert, update, delete, truncate, transactional/nontransactional logical message, origin, timestamp, LSN, and ordering preservation;
- bounded change, relation, transaction-memory, individual-record, and total spool-storage accounting;
- versioned disk envelopes with completion footers, per-record CRC32 integrity, atomic `.partial` to `.ready` publication, and pluggable at-rest protection;
- restart-safe spool accounting that includes pre-existing `.partial` and `.ready` artifacts in the configured disk ceiling;
- streaming materialisation of spooled changes, with spool deletion tied to delivery acknowledgement, nack, or disposal;
- explicit one-shot acknowledgement semantics that stop a source read if a delivery is skipped or rejected; and
- public API baselines plus fake pgoutput and PostgreSQL 15–19 integration coverage.

Prepared/two-phase transactions fail closed by default. The opt-in
experimental mode outside the default delivery contract exposes durable prepare,
commit-prepared, and rollback-prepared lifecycle deliveries for destinations
that explicitly support invisible staging. See
[prepared and two-phase transactions](prepared-transactions.md).

## Reading transactions

`PgOutputChangeStream` accepts the decoded pgoutput sequence from a dedicated logical replication connection. Use the hosted snapshot-then-stream consumer when you need provisioning and durable acknowledgement.

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
        // Opt in only when ApplyIdempotentlyAsync durably stages PREPARE.
        PreparedTransactionMode = PreparedTransactionMode.Fail,
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

See [prepared and two-phase transactions](prepared-transactions.md) for the
opt-in staging contract, final lifecycle deliveries, decoder configuration, and
relay format 1-to-2 compatibility.

The [format compatibility registry](format-compatibility.md) records every
durable or externally visible Streams format, its readable range, and the test
fixture that proves its compatibility policy.

`BlueTusk.Streams.Testing` includes `ChangeDeliveryTestFactory`, allowing
downstream products to exercise acknowledge/nack ordering through the public
Streams contract without importing replication protocol types.

See [typed mappings](typed-mappings.md) for convention and explicit mappings, schema and mapping fingerprints, partial-row safety, decoding policy, and the snapshot consumer lifecycle.

See [consistent snapshot bootstrap](snapshot-bootstrap.md) for exported-snapshot lifetime, keyset binary COPY, bounded parallelism, restart epochs, and the PostgreSQL 15–19 no-gap proof.

See [hosting and observability](hosting-observability.md) for hosted-worker registration, health states, readiness behavior, and exporter-neutral OpenTelemetry instruments.

See [CloudEvents](cloudevents.md) for transaction-preserving structured JSON, deterministic IDs, integrity-checked payloads, and acknowledgement responsibility.

See the [validation and provisioning CLI](cli.md) for idempotent source/relay setup, safe shared-control checks, and machine-readable diagnostic codes.

See [Aspire integration](aspire.md) for secret-preserving source/control resource wiring and explicit relay versus direct delivery configuration.

See the [snapshot-then-stream sample](sample.md) for a runnable hosted consumer using exported-snapshot binary COPY followed by transaction-preserving CDC.
