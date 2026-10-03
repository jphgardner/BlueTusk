# Troubleshooting Streams

This page helps you fix the most common Streams errors. Run
[`bluetusk-streams validate`](cli.md) first: it checks the server version,
`wal_level`, publications and slot in one go.

PostgreSQL errors reach you as `BlueTusk.Client.BlueTuskServerException` with
PostgreSQL's own message.

## Setting up the source

| Symptom | Cause | Fix |
| --- | --- | --- |
| Creating or starting a slot fails with an error that says logical decoding requires `wal_level` >= `logical`; or `ERROR BTS002 wal_level is 'replica'; logical is required.` | The server is not configured for logical replication. | Run `ALTER SYSTEM SET wal_level = logical;` and restart PostgreSQL. On managed services, use the provider's logical-replication setting. |
| `permission denied to start WAL sender` | The login lacks the `REPLICATION` attribute. | `ALTER ROLE <login> WITH REPLICATION;` (or grant your provider's replication role). |
| `permission denied for schema bluetusk_streams` | The state store or relay schema is owned by another login. | Use the login that created the schema, or grant `USAGE` on the schema and rights on its tables. |
| `replication slot "<name>" does not exist` | The slot was never created, was dropped, or the worker points at another database. | Create it (`SELECT pg_create_logical_replication_slot('<name>', 'pgoutput');` or `bluetusk-streams provision`). A snapshot source creates its own slot. |
| `replication slot "<name>" is active for PID <pid>` | Another connection is reading the slot. A slot has one reader at a time. | Stop the other reader, or give each direct consumer its own slot. Use the [relay](durable-relay.md) for several consumers. Find the reader with `SELECT active_pid FROM pg_replication_slots WHERE slot_name = '<name>';`. |
| `publication "<name>" does not exist` (PostgreSQL 15 to 17), or no changes arrive and the server log shows `skipped loading publication "<name>"` (PostgreSQL 18) | The publication name is wrong or the publication was created after the slot position. | Create the publication, or fix the name. `bluetusk-streams validate` reports `BTS003`. |
| Changes for a table never arrive | The table is not in the publication. | `ALTER PUBLICATION <name> ADD TABLE <table>;` `BTS004` lists how many tables are published. |

## Another worker owns the consumer group

```text
ChangeStreamLeaseUnavailableException: Consumer group 'console' is already owned by 'HACKITRON:31400'.
```

Another process holds the group's lease. It may still be running, or it
crashed and its lease has not expired yet (leases are not released on a
crash). Stop the other process, or wait for the lease duration (30 seconds in
the samples) and start again. Give every process a unique owner ID, such as
machine name plus process ID; two processes sharing an ID would share a lease.

The relay equivalents are `ChangeRelayLeaseUnavailableException`:
"Relay source slot '...' is already owned by '...'." and "Relay consumer group
'...' is already owned by another worker."

## The lease was lost

| Message | Cause | Fix |
| --- | --- | --- |
| `ChangeStreamLeaseLostException`: "The lease for consumer group '...' was lost." | No acknowledgement renewed the lease for longer than its duration, or another worker took over. | Renew the lease on a timer while idle, as the [quick start](quickstart.md) does. Restart the worker; it resumes from the checkpoint. |
| `ChangeRelayLeaseLostException`: "The relay source lease was lost before append." | Same, for a relay source worker. | Renew with `relay.RenewSourceLeaseAsync` on a timer. |
| `ChangeRelayLeaseLostException`: "The relay consumer-group lease was lost." | A relay group's background renewal failed, for example during a database outage. | Restart the consumer. |
| `ChangeStreamCheckpointWriteException`: "The change-stream checkpoint write failed with status Fenced." | A newer owner holds the lease. Other statuses: `Conflict` (concurrent write), `BackwardMovement`, `Incompatible`. | Stop this worker. Do not retry the write; let the current owner continue. |

## The database was restored or replaced

A checkpoint belongs to one server, database, slot and publication. After a
restore, a failover to a server with a different system identifier, or a
recreated slot, Streams refuses to reuse it:

| Message | Meaning |
| --- | --- |
| `ChangeStreamCheckpointMismatchException`: "The checkpoint belongs to a different source, slot, publication, output plug-in, database, mapping, or format." | The stored checkpoint does not match the identity you passed (often a changed `mappingFingerprint`). |
| `BlueTuskReplicationCheckpointException`: "The checkpoint belongs to a different PostgreSQL system identifier." | From `ValidateResumeCheckpointAsync`: the server was replaced. Other messages report a missing or temporary slot, lost WAL, or a checkpoint ahead of the server. |
| `SnapshotAttemptException`: "The connected PostgreSQL system/database identity does not match the configured change source." | A snapshot source connected to a different server or database than its `ChangeSourceIdentity`. |
| `ChangeRelaySourceMismatchException` | The relay's registered source differs from the transaction's source. |

Because the system identifier is part of the state key, a worker on a restored
server usually finds no checkpoint at all and its slot is missing (slots are
not part of most backups). In every case, rebuild the consumer: create the slot
again, take a new [snapshot](snapshot-bootstrap.md), and treat the destination
as needing a reset. Do not copy the old checkpoint across.

## The same transaction arrives twice

This is expected after a crash, a rejected delivery or a failed acknowledgement:
delivery is at least once. Make your writes idempotent using `ChangeId`. Start
replication at `StartPosition = checkpoint` so PostgreSQL skips what you already
acknowledged. See [restarts and redelivery](concepts.md#restarts-and-redelivery).

## The stream stops on a delivery

| Message | Cause | Fix |
| --- | --- | --- |
| `ChangeDeliveryNotAcknowledgedException`: "The previous change transaction was not acknowledged; its final state is Active." | Your loop moved to the next transaction without calling `AcknowledgeAsync`. `Nacked` or `Disposed` means you rejected it. | Acknowledge every delivery after your work. A rejected one is redelivered on restart. |
| `PreparedTransactionNotSupportedException` | Two-phase messages arrived while `PreparedTransactionMode` is `Fail`. | Turn off two-phase decoding on the slot, or opt in to [prepared transactions](prepared-transactions.md). |
| `ChangeSchemaReloadRequiredException` | A [typed mapping](typed-mappings.md) saw a changed table. | Rebuild the mapping, then restart. |

## A large transaction stops the stream

| Message | Fix |
| --- | --- |
| `TransactionAssemblyLimitExceededException`: "Transaction 1249 exceeds the 262144-byte limit." (or the `-change` or `-relation` limit) | Raise `MaxTransactionBytes`, `MaxChangesPerTransaction` or `MaxRelationsPerTransaction`, or split the job that writes such transactions. |
| `TransactionSpoolLimitExceededException`: "The transaction spool limit of ... bytes would be exceeded." | Raise `MaxSpoolBytes`, or free the spool directory. |
| `TransactionSpoolLimitExceededException`: "Existing transaction spool artifacts consume ... bytes, which exceeds the ...-byte storage limit." | Files from an earlier run fill the spool. With the worker stopped, delete the spool directory's contents. |
| `TransactionSpoolIntegrityException` | A spool file is damaged. Stop the worker, empty the spool directory and restart; the transaction is read again from PostgreSQL. |
| `ChangeRelayStorageExhaustedException` | Raise `MaxRelayStorageBytes` or `MaxEnvelopeBytes`, or run relay compaction. |

Streams never skips or splits a transaction to get past a limit. Until you fix
the cause, the same transaction fails again after every restart. See
[configuration](configuration.md#transaction-assembly-and-spooling).

## The snapshot does not start

| Message | Cause | Fix |
| --- | --- | --- |
| `SnapshotRestartLimitExceededException`: "Snapshot bootstrap failed before an epoch could be established after 3 attempts." with an inner `replication slot "<name>" already exists` | The slot exists and `ExistingSlotMode` is `Fail`. | Drop the slot, or use `ExistingSlotMode.RestartSnapshot` if your consumer handles a reset. |
| `SnapshotAttemptException`: "Existing slot ... is active or does not belong to the configured pgoutput snapshot source; it cannot be replaced safely." | `RestartSnapshot` found an active slot, or one for another plug-in or database. | Stop its reader, or choose another slot name. |
| `SnapshotAttemptException`: "Snapshot row in ... uses ... bytes; the configured maximum is ... bytes." | A row is larger than `MaximumRowBytes`. | Raise `MaximumRowBytes` (and `MaximumBatchBytes`). |

## WAL keeps growing on the source server

A slot keeps every WAL file from its confirmed position onwards. Check how
much each slot holds:

```sql
SELECT slot_name, active, confirmed_flush_lsn,
       pg_size_pretty(pg_wal_lsn_diff(pg_current_wal_lsn(), restart_lsn)) AS retained
FROM pg_replication_slots;
```

| Cause | Fix |
| --- | --- |
| A consumer is stopped or slow. | Restart it or speed it up. With many consumers, use the [relay](durable-relay.md). |
| A slot is no longer used. | Drop it: `SELECT pg_drop_replication_slot('<name>');` The slot must be inactive. Its consumer group must then start again from a snapshot. |
| The consumer acknowledges, but `confirmed_flush_lsn` never moves. | The stream has no delivery observer, so PostgreSQL is never told. Add one (see [hosting](hosting-observability.md#confirm-positions-to-postgresql)). |
| A relay group is stopped. | The relay frees WAL anyway; check relay storage with `GetHealthAsync` instead. |

As a safety net, set PostgreSQL's `max_slot_wal_keep_size` so a forgotten slot
cannot fill the disk. A slot that passes the limit loses WAL and must be
recreated, and its consumers re-snapshotted.

## Related pages

- [Configuration](configuration.md)
- [Hosting and observability](hosting-observability.md)
- [Delivery guarantees](../realtime-platform/contracts.md)
