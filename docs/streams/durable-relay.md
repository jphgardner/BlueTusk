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

The configured control schema contains versioned storage metadata, source registrations and epochs, binary transaction envelopes, consumer groups/checkpoints/fencing leases, snapshot runs, dead letters, and retention watermarks. `InitializeAsync` takes a row lock on storage metadata and transactionally applies every registered migration in order. It upgrades schema version 1 to version 2 and rejects a database created by a newer, unsupported build instead of guessing at compatibility. `GetSchemaVersionAsync` exposes the installed version for health and upgrade checks.

The envelope is a bounded versioned binary format with a SHA-256 integrity hash. It preserves source and transaction metadata, table/type/column metadata, every explicit row state, changed-column exactness, truncates, logical messages, and prepared-transaction lifecycle state.

`MaxEnvelopeBytes` bounds one transaction and `MaxRelayStorageBytes` atomically reserves total relay storage before insert. Read batches are bounded by transaction count and bytes. The first transaction may exceed the requested batch-byte target because source transactions are never split; it still cannot exceed the configured envelope limit.

Set `EnvelopeProtection` to an `IChangeRelayEnvelopeProtectionProvider` to protect relay payloads before they enter PostgreSQL. Each row stores the provider's current protector ID. Reads pass that ID back to the provider, allowing a key-ring implementation to decrypt older rows after rotation. Rows written before protection was enabled remain readable as integrity-checked plaintext. A missing protector, unknown key ID, failed decrypt, or invalid envelope fails closed.

BlueTusk intentionally does not ship a process-global encryption key. Production providers should use authenticated encryption, keep keys outside the control database, return a new immutable ID when rotating keys, and retain old decrypt-only keys through the relay retention and backup windows. Protection overhead counts against `MaxEnvelopeBytes` and `MaxRelayStorageBytes`.

## Backup and restore

`BackupAsync` writes one source epoch as a bounded, framed stream from a PostgreSQL `REPEATABLE READ` snapshot. The backup includes retained transaction envelopes, consumer-group checkpoints and tombstones, fencing-token history, the source high-watermark, and the retention watermark. Snapshot runs and dead letters are included by default and can be omitted explicitly. Each frame has a SHA-256 integrity hash and `MaxFrameBytes` rejects an oversized or malicious frame.

Relay envelopes remain in their stored form. If envelope protection is configured, the backup therefore retains the protected bytes and protector ID instead of decrypting sensitive payloads into the backup stream. The restore process must have a provider capable of decrypting every retained protector ID so it can validate each envelope before storing it. Keep decrypt-only rotation keys until every backup that references them has expired.

```csharp
await using var backup = File.Create(backupPath);
await relay.BackupAsync(sourceRegistration, backup);

backup.Position = 0;
var restored = await replacementRelay.RestoreAsync(
    backup,
    confirmation: sourceRegistration.Source.Fingerprint);
```

`RestoreAsync` is deliberately a replace/recovery operation, not a merge. It requires an initialized but otherwise empty control schema, a confirmation string exactly matching the backup source fingerprint, and a complete stream with no trailing bytes. Restore runs in one `SERIALIZABLE` database transaction; a truncated frame, invalid hash, malformed identity, envelope decode failure, duplicate record, or write failure rolls back the entire import.

Source and consumer leases are never copied. Consumer checkpoints, inactive-group tombstones, retention protection, store generations, and last fencing tokens are copied, so a newly acquired lease advances beyond every pre-backup token. The source's last sequence is restored even when retention already removed its highest stored transaction, preventing identity reuse after recovery.

Frame hashes detect accidental corruption; they are not a substitute for authenticated backup storage. Encrypt and authenticate the outer backup stream, restrict access to it, and test restore into a disposable schema regularly. This is especially important when relay envelope protection is disabled, because those backup frames contain integrity-checked plaintext transaction envelopes.

## Consumer groups

Create groups at the earliest retained position or at the latest source position. Each group owns its own database-clock lease, monotonically increasing fencing token, checkpoint sequence, and compare-and-swap generation. Reading with a stale lease fails; acknowledgement rechecks the lease and known relay sequence inside a locked PostgreSQL transaction.

`PostgreSqlRelayChangeStream` is the normal application adapter. It exposes a relay group through `IChangeStream`, bounds every read by transaction count and encoded bytes, renews the database-clock lease while destination work is in flight, and acknowledges exactly one sequence only after the delivery is acknowledged. A nack, abandoned delivery, lease loss, or process failure leaves the sequence available for safe redelivery.

```csharp
IChangeStream stream = new PostgreSqlRelayChangeStream(
    relay,
    sourceRegistration,
    new PostgreSqlRelayChangeStreamOptions
    {
        ConsumerGroup = "search-index",
        OwnerId = uniqueWorkerId,
        MaxTransactionsPerRead = 128,
        MaxBytesPerRead = 8 * 1024 * 1024,
        LeaseDuration = TimeSpan.FromSeconds(30),
        LeaseRenewalInterval = TimeSpan.FromSeconds(10),
    });

await foreach (var delivery in stream.ReadTransactionsAsync(stoppingToken))
{
    await ApplyTransactionIdempotentlyAsync(delivery.Transaction, stoppingToken);
    await delivery.AcknowledgeAsync(stoppingToken);
}
```

The stream is single-use and deliberately permits only one outstanding transaction. Its options require an explicit group and unique owner identity; a newly created group starts at the earliest retained position unless `Latest` is explicitly selected. Stable `ChangeId` values remain the destination deduplication key; the relay and Streams still promise at-least-once delivery, not exactly once.

Consumer-group removal is a fenced state transition, not a row deletion. `RemoveConsumerGroupAsync` requires the expected store generation and an exact group-name confirmation. It clears the lease, increments the generation, records removal time, and leaves an inactive tombstone that cannot be silently recreated. The default `PreserveResumeWindow` mode continues to protect the removed group's unacknowledged records for `RemovedConsumerGroupRetentionWindow`. The explicitly destructive `ReleaseRetentionImmediately` mode can release that protection later, again with generation checking and confirmation.

## Retention and health

A transaction is eligible for deletion only when every active or retention-protected removed group to which it applies has checkpointed past it and the resume-retention window has elapsed. A group created at `Latest` does not pin older transactions. `MinimumRetainedTransactions` can keep a source tail even after acknowledgement.

Each `ApplyRetentionAsync` call deletes at most `RetentionDeleteBatchSize` records and reports when it reached that limit. This bounds locks, WAL, and transaction duration. `CompactAsync` runs up to `MaxCompactionBatches`, reports whether it fully caught up, updates the persisted byte reservation and retention high-watermark atomically per batch, and can issue `VACUUM (ANALYZE)` after deletion. Operators can disable that final vacuum when autovacuum or a maintenance service owns physical compaction.

`GetMetricsAsync` reports count, bytes, sequence bounds, minimum group checkpoint, and oldest applicable unacknowledged age. `GetHealthAsync` adds WAL lag against the source's durably appended commit position and explicit danger flags for WAL lag, acknowledgement age, and relay capacity.

The relay's control schema must not be part of the source publication. Use a separate control data source by default and run publication validation during provisioning. The live acceptance suite covers append/retry, two-group fan-out, replay, fencing, bounded retention, confirmed group removal, schema upgrade/future-version rejection, protected envelope round trips, atomic backup/restore with corruption rollback, compaction, capacity exhaustion, integrity decoding, and health signals on PostgreSQL 15–19.
