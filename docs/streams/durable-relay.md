# Durable relay

This guide shows you how to feed several independent consumers from one
replication slot. A source worker stores each committed transaction in
PostgreSQL tables (the **relay**), and each **consumer group** reads them back at
its own pace.

## When to use the relay

| Direct consumers | Relay |
| --- | --- |
| One slot per consumer group. | One slot for all groups. |
| Each slot holds WAL until its slowest reader catches up. | PostgreSQL releases WAL as soon as the relay has stored a transaction. |
| A stopped consumer makes WAL grow on the source server. | A stopped group only keeps rows in the relay tables. |
| A new consumer needs its own slot and snapshot. | A new group can start from the oldest retained transaction or from now. |

Use the relay when you have more than one consumer, or when consumers may be
stopped for a while. [Sync](../sync/README.md) pipelines can read from the
relay directly.

## Prepare the relay storage

Keep the relay in a **control database** that is separate from the source
database. If the relay tables were in a published source table set, the relay
would read its own writes. The [`bluetusk-streams` tool](cli.md) creates the
publication, slot and relay schema and checks this for you:

```powershell
$env:BLUETUSK_STREAMS_SOURCE = "Host=source;Database=app;Username=streams;Password=..."
$env:BLUETUSK_STREAMS_CONTROL = "Host=control;Database=streams;Username=streams;Password=..."
bluetusk-streams provision --publication app_changes --slot app_relay --table app.orders
```

In code, `InitializeAsync()` creates or upgrades the relay schema
(`bluetusk_streams` by default). It is safe to call on every start:

```csharp
var relay = new PostgreSqlDurableChangeRelay(new PostgreSqlStreamsStorageOptions
{
    ControlDataSource = controlDataSource,
    ControlSchema = "bluetusk_streams",
});
await relay.InitializeAsync();
```

`InitializeAsync()` refuses a schema created by a newer BlueTusk version with
`ChangeRelaySchemaVersionException`.

## 1. Run the source worker

Run exactly one source worker per slot. It reads the slot like any Streams
consumer, but its delivery observer appends each transaction to the relay:

```csharp
await using var replication = await BlueTuskLogicalReplicationConnection.OpenAsync(
    sourceDataSource.CreateDedicatedSessionOptions(), stoppingToken);
var server = await replication.IdentifySystemAsync(stoppingToken);
var source = new ChangeSourceIdentity(
    server.SystemIdentifier, server.DatabaseName!, slot, publicationFingerprint);

await using var relayWriter = await PostgreSqlRelayChangeDeliveryObserver.AcquireAsync(
    relay,
    source,
    workerId,
    TimeSpan.FromSeconds(30),
    new LogicalReplicationFeedbackSender(replication),
    stoppingToken);

var changes = new PgOutputChangeStream(
    replication
        .StartReplicationAsync(
            new BlueTuskPgOutputReplicationOptions
            {
                SlotName = slot,
                PublicationNames = [publication],
                StartPosition = relayWriter.Source.LastCommitPosition,
                ProtocolVersion = 2,
                StreamingMode = BlueTuskLogicalStreamingMode.On,
            },
            stoppingToken)
        .DecodePgOutputAsync(
            new BlueTuskPgOutputDecoderOptions
            {
                ProtocolVersion = 2,
                StreamingMode = BlueTuskPgOutputStreamingMode.On,
            },
            stoppingToken),
    source,
    observer: relayWriter);

await foreach (var delivery in changes.ReadTransactionsAsync(stoppingToken))
{
    // Acknowledging appends the transaction to the relay, then confirms
    // the position to PostgreSQL.
    await delivery.AcknowledgeAsync(stoppingToken);
}
```

Each acknowledgement renews the source lease, then appends the transaction and
moves the relay's source watermark in one control-database transaction, and
only then sends the position to PostgreSQL. If the worker crashes before the append
commits, PostgreSQL sends the transaction again. If it crashes after, the retry
finds the identical transaction already stored and does not store it twice.

`relayWriter.Source.LastCommitPosition` is the last stored position, so a
restarted worker carries on from there. A second worker for the same slot fails
with `ChangeRelayLeaseUnavailableException`. The observer also renews the
source lease on a timer, three times per lease duration, so a quiet source
keeps it. Disposing `relayWriter` stops renewal and releases the lease.

## 2. Read as a consumer group

Each consumer process builds the same `ChangeSourceIdentity` and reads its
group:

```csharp
var registration = await relay.RegisterSourceAsync(source, cancellationToken: stoppingToken);
IChangeStream stream = new PostgreSqlRelayChangeStream(
    relay,
    registration,
    new PostgreSqlRelayChangeStreamOptions
    {
        ConsumerGroup = "search-index",
        OwnerId = workerId,
    });

await foreach (var delivery in stream.ReadTransactionsAsync(stoppingToken))
{
    await ApplyTransactionIdempotentlyAsync(delivery.Transaction, stoppingToken);
    await delivery.AcknowledgeAsync(stoppingToken);
}
```

The stream creates the group if it does not exist, takes the group's lease and
renews it in the background, and reads at most `MaxTransactionsPerRead` (128)
transactions or `MaxBytesPerRead` (8 MiB) per query. A single transaction is
never split, so one read can exceed the byte target. Acknowledging moves the
group's checkpoint. A rejected delivery, a crash or a lost lease leaves the
transaction to be read again. Use `ChangeId` to make your writes idempotent;
delivery is at least once.

`ApplyTransactionIdempotentlyAsync` is your own code.

### Choose where a new group starts

`NewGroupStart` decides where a group that does not exist yet begins:

- `EarliestAvailable` (default): the oldest transaction still in the relay.
- `Latest`: only transactions stored from now on.

A group that needs the full current state should take a snapshot first. Use
`PostgreSqlRelayConsumerGroupSession.AcquireAsync` to hold the group's lease
during the copy, record it with `BeginSnapshotRunAsync`, and call
`CompleteSnapshotRunAsync` after your destination has stored the snapshot. The
relay then keeps every transaction the group still needs while it copies.
[Sync](../sync/README.md) does all of this for you in
`PostgreSqlRelaySyncPipelineSource`.

## Keep the relay small

A transaction is deleted only when every active group has acknowledged it and
it is older than `ResumeRetentionWindow` (1 hour by default).
`MinimumRetainedTransactions` keeps a tail even after that. Groups created at
`Latest` do not hold older transactions.

Run compaction regularly, for example from a timer:

```csharp
var compaction = await relay.CompactAsync(registration);
Console.WriteLine(
    $"Deleted {compaction.DeletedTransactions} transactions ({compaction.DeletedBytes} bytes) " +
    $"in {compaction.Batches} batches; caught up: {compaction.FullyApplied}");
```

`CompactAsync` runs up to `MaxCompactionBatches` (100) batches of
`RetentionDeleteBatchSize` (1,000) deletions, then runs `VACUUM (ANALYZE)`. Pass
`vacuum: false` if autovacuum or a maintenance job handles that.
`ApplyRetentionAsync` runs a single batch.

`MaxRelayStorageBytes` (100 GiB) caps total relay storage and
`MaxEnvelopeBytes` (256 MiB) caps one transaction. When an append would pass
either limit it fails with `ChangeRelayStorageExhaustedException`; nothing is
dropped.

## Monitor the relay

```csharp
var registration = await relay.RegisterSourceAsync(source);
var metrics = await relay.GetMetricsAsync(registration);
var health = await relay.GetHealthAsync(registration, serverWalEnd);
Console.WriteLine(
    $"{metrics.TransactionCount} transactions, {metrics.StorageBytes} bytes, " +
    $"WAL lag {health.WalLagBytes} bytes, oldest unacknowledged {metrics.OldestUnacknowledgedAge}");
if (health.IsWalRetentionDanger || health.IsAcknowledgementOverdue || health.IsStorageExhausted)
{
    Console.WriteLine("The relay needs attention.");
}
```

`serverWalEnd` is the source server's current WAL position, for example
`IdentifySystemAsync()` → `WalPosition`. The flags compare against
`MaxWalLagBytes` (10 GiB), `MaxAcknowledgementAge` (5 minutes) and
`MaxRelayStorageBytes`.

## Remove a consumer group

Removal needs the group's current generation and its exact name as a
confirmation:

```csharp
var group = await relay.CreateConsumerGroupAsync(registration, "old-reporting");
var removal = await relay.RemoveConsumerGroupAsync(
    group,
    expectedGeneration: group.StoreGeneration,
    confirmation: "old-reporting");
Console.WriteLine(removal.Status);
```

The group is marked inactive, not deleted, and cannot be silently recreated.
By default (`PreserveResumeWindow`) its unacknowledged transactions are kept for
`RemovedConsumerGroupRetentionWindow` (1 hour). Call again with
`ChangeRelayConsumerGroupRemovalMode.ReleaseRetentionImmediately` to release
them sooner.

## Protect stored payloads

Set `EnvelopeProtection` to your own `IChangeRelayEnvelopeProtectionProvider` to
encrypt each stored transaction. Each row records the provider's
`CurrentProtectorId`, and reads pass it back to `Unprotect`, so you can rotate
keys and still read older rows. BlueTusk does not ship a key. Use authenticated
encryption, keep keys outside the control database, and keep old decrypt-only
keys for as long as rows or backups use them. A missing or wrong key stops the
read with `ChangeRelayProtectionException`.

## Back up and restore

```csharp
await using var backup = File.Create(backupPath);
await relay.BackupAsync(sourceRegistration, backup);

backup.Position = 0;
var restored = await replacementRelay.RestoreAsync(
    backup,
    confirmation: sourceRegistration.Source.Fingerprint);
```

- A backup covers one source: stored transactions, group checkpoints, removed
  groups, fencing tokens and watermarks. Snapshot runs and dead letters are
  included unless you turn them off in `ChangeRelayBackupOptions`.
- Restore needs an initialized, empty relay schema (call `InitializeAsync()` on
  `replacementRelay` first) and the source fingerprint as confirmation. It runs
  in one transaction: a damaged or truncated backup changes nothing and throws
  `ChangeRelayBackupException`.
- Leases are not restored. New owners get fencing tokens above every token in
  the backup.
- Protected payloads stay protected in the backup. Without protection the
  backup contains row data in plain form, so encrypt and restrict the backup
  file.

## Related pages

- [Concepts: direct consumers and the relay](concepts.md#direct-consumers-and-the-relay)
- [Configuration: relay options](configuration.md#postgresql-storage-and-relay)
- [Troubleshooting](troubleshooting.md)
- [Format compatibility registry](format-compatibility.md)
