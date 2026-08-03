# Prepared and two-phase transactions

Prepared-transaction delivery is an opt-in Streams preview feature. The default
`PreparedTransactionMode.Fail` behavior rejects every two-phase pgoutput message
before changing assembler state. Enable `PreparedTransactionMode.Stage` only
when the destination can durably stage source changes without making them
visible.

```csharp
var assembly = new TransactionAssemblyOptions
{
    PreparedTransactionMode = PreparedTransactionMode.Stage,
};
```

`PostgreSqlConsistentSnapshotSource` automatically selects pgoutput protocol 3
and enables the PostgreSQL `two_phase` option when staging is enabled. Code that
manually composes a replication stream must configure both the replication
request and decoder consistently:

```csharp
var source = replication.StartReplicationAsync(
    new BlueTuskPgOutputReplicationOptions
    {
        SlotName = slotName,
        PublicationNames = [publicationName],
        ProtocolVersion = 3,
        StreamingMode = BlueTuskLogicalStreamingMode.On,
        TwoPhase = true,
    }).DecodePgOutputAsync(
    new BlueTuskPgOutputDecoderOptions
    {
        ProtocolVersion = 3,
        StreamingMode = BlueTuskPgOutputStreamingMode.On,
        TwoPhase = true,
    });
```

PostgreSQL can emit a two-phase transaction as one ordinary committed
transaction, including its changes, when logical decoding did not process that
transaction at `PREPARE TRANSACTION` time. This can occur while a consumer is
starting or catching up. It is PostgreSQL's documented fallback and does not
lose changes: consumers must always handle ordinary committed deliveries in
addition to the staged lifecycle below. A workflow that must observe a staged
delivery can first emit and consume a non-transactional logical message as a
stream-readiness barrier before it begins the prepared transaction.

## Lifecycle deliveries

Streams does not keep an acknowledged prepared transaction only in process
memory. It exposes three ordered delivery states instead:

| `ChangeTransaction.Outcome` | Changes | Required destination action |
| --- | --- | --- |
| `Prepared` | Complete source transaction | Durably stage all changes under the source identity, transaction ID, and `GlobalTransactionId`; do not expose them. |
| `Committed` with `IsTwoPhase == true` | Empty | Atomically make the corresponding staged changes visible and record the final lifecycle delivery. |
| `RolledBack` | Empty | Atomically discard the corresponding staged changes and record the final lifecycle delivery. |

Ordinary commits and synthetic logical-message transactions have
`Outcome == Committed`, a null `GlobalTransactionId`, and `IsTwoPhase == false`.
Prepared, commit-prepared, and rollback-prepared deliveries preserve the
PostgreSQL transaction ID and global transaction ID. Streamed prepared
transactions use the same bounded memory and disk-spool limits as ordinary
streamed transactions.

Acknowledgement means the lifecycle action is durable. For `Prepared`, the
consumer must finish durable staging before acknowledging. For the two final
states, it must atomically finalize or discard the staged state before
acknowledging. The normal destination → checkpoint → replication-feedback
ordering then applies independently to every lifecycle delivery.

Crashes can redeliver any state, so all three actions must be idempotent. A
destination should retain a compact final-state tombstone for at least its
configured replay/resume window. BlueTusk continues to advertise at-least-once
delivery and does not infer cross-system exactly-once behavior from PostgreSQL
two-phase commit.

## Relay compatibility

Transaction relay envelopes use format 2 for lifecycle outcome and global
transaction ID metadata. The decoder continues to accept integrity-checked
format 1 envelopes, treating them as ordinary committed transactions. New
writers always emit format 2. Unknown future versions and invalid lifecycle
combinations fail closed.

The feature is covered by fake ordinary and streamed pgoutput sequences, disk
spilling, format 1-to-2 upgrade fixtures, and live PostgreSQL prepared-commit
acceptance. It remains preview until the complete Streams 1.0 format-upgrade and
72-hour fault-injected endurance gates pass.
