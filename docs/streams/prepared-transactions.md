# Prepared (two-phase) transactions

This guide shows you how to receive PostgreSQL prepared transactions
(`PREPARE TRANSACTION`) as separate prepare, commit and rollback deliveries, so
a destination can stage changes before they are final.

> **Note:** This is a preview feature. It is off by default.

Without two-phase decoding (the default), PostgreSQL sends a prepared
transaction only when it commits, as an ordinary transaction. If two-phase
messages arrive while `PreparedTransactionMode` is `Fail` (the default), Streams
stops with `PreparedTransactionNotSupportedException`.

Turn staging on only if your destination can store changes durably without
making them visible, and later publish or discard them.

## Turn it on

Set the mode on the transaction assembly options:

```csharp
var assembly = new TransactionAssemblyOptions
{
    PreparedTransactionMode = PreparedTransactionMode.Stage,
};
```

With the [snapshot source](snapshot-bootstrap.md), pass these options as
`PostgreSqlConsistentSnapshotOptions.TransactionAssembly`. The source then
creates the slot with two-phase decoding and uses pgoutput protocol 3.

If you read a slot yourself, the slot must have two-phase decoding enabled, and
the replication request and decoder must agree:

```csharp
await replication.CreateReplicationSlotAsync(slotName, twoPhase: true);
```

```csharp
var changes = new PgOutputChangeStream(
    replication
        .StartReplicationAsync(new BlueTuskPgOutputReplicationOptions
        {
            SlotName = slotName,
            PublicationNames = [publicationName],
            ProtocolVersion = 3,
            StreamingMode = BlueTuskLogicalStreamingMode.On,
            TwoPhase = true,
        })
        .DecodePgOutputAsync(new BlueTuskPgOutputDecoderOptions
        {
            ProtocolVersion = 3,
            StreamingMode = BlueTuskPgOutputStreamingMode.On,
            TwoPhase = true,
        }),
    sourceIdentity,
    assembly);
```

The source server also needs `max_prepared_transactions` above 0, or
applications cannot run `PREPARE TRANSACTION` at all.

## Handle the three lifecycle deliveries

A two-phase transaction arrives as two deliveries: the prepare, then either a
commit or a rollback. Check `Outcome`:

```csharp
await foreach (var delivery in changes.ReadTransactionsAsync())
{
    var transaction = delivery.Transaction;
    switch (transaction.Outcome)
    {
        case ChangeTransactionOutcome.Prepared:
            // Stage every change under transaction.GlobalTransactionId; keep it hidden.
            break;
        case ChangeTransactionOutcome.Committed when transaction.IsTwoPhase:
            // Make the staged changes for transaction.GlobalTransactionId visible.
            break;
        case ChangeTransactionOutcome.RolledBack:
            // Discard the staged changes for transaction.GlobalTransactionId.
            break;
        default:
            // An ordinary committed transaction: apply it as usual.
            break;
    }

    await delivery.AcknowledgeAsync();
}
```

| `Outcome` | `Changes` | What your destination must do before acknowledging |
| --- | --- | --- |
| `Prepared` | All of the transaction's changes | Store them durably under the `GlobalTransactionId`, hidden. |
| `Committed` with `IsTwoPhase == true` | Empty | Make the staged changes visible, atomically. |
| `RolledBack` | Empty | Discard the staged changes, atomically. |
| `Committed` with `IsTwoPhase == false` | All changes | An ordinary transaction. Apply it as usual. |

`GlobalTransactionId` is the name given to `PREPARE TRANSACTION`, and is `null`
for ordinary transactions. Each lifecycle delivery is acknowledged and
checkpointed on its own, following the usual
[acknowledgement rule](concepts.md#acknowledge-after-your-work-is-durable).
Large prepared transactions are spooled to disk under the same limits as any
other transaction.

## Expect ordinary commits too

PostgreSQL may send a two-phase transaction as one ordinary committed
transaction, with all its changes, if decoding had not reached the transaction
when it was prepared. This happens while a consumer is starting or catching up.
Nothing is lost, but your code must handle ordinary commits as well as the
staged lifecycle. If a workflow must see the staged form, emit and consume a
non-transactional logical message (`pg_logical_emit_message(false, ...)`) before
it prepares the transaction, to be sure the consumer has caught up.

## Make every step safe to repeat

After a crash, any of the three deliveries can arrive again. Make staging,
publishing and discarding idempotent, keyed by `GlobalTransactionId`. Keep a
small record of finished transactions for as long as a redelivery can happen.
Delivery is at least once; PostgreSQL two-phase commit does not make it
exactly once across systems.

## Relay and stored formats

The [durable relay](durable-relay.md) stores the outcome and global transaction
ID using envelope format 2. It still reads format 1 envelopes written before
this feature, as ordinary committed transactions. Unknown future formats stop
the read with an error. See the
[format compatibility registry](format-compatibility.md).

## Related pages

- [Concepts](concepts.md)
- [Configuration: transaction assembly](configuration.md#transaction-assembly-and-spooling)
