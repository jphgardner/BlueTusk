# Troubleshoot Sync

This page helps you find out why a Sync pipeline stopped or fell behind, and
how to fix it. Each entry lists the symptom, the cause and the fix, with the
exception types and messages BlueTusk actually raises.

## Why did my pipeline stop?

When a hosted pipeline hits an error it does not retry, the worker stops that
pipeline and logs (event ID 1, `SyncPipelineStopped`):

```text
fail: BlueTusk.Sync.DependencyInjection.BlueTuskSyncHostedService[1]
      BlueTusk Sync pipeline orders-replica stopped and requires operator action.
```

The exception follows on the next lines. The host keeps running and other
pipelines continue. A stopped pipeline does not restart by itself: fix the
cause, then restart the process. The checkpoint never moves past the failed
transaction.

In an ASP.NET Core host, expose health and status so you notice:

```csharp
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
});
app.MapGet("/sync/status", (IBlueTuskSyncStatusSource status) => status.GetStatuses());
```

The `bluetusk_sync` check is **Unhealthy** when a pipeline is `Faulted`,
`Rebuilding` or has a diagnostic code, and **Degraded** when nothing is
applying changes. `BlueTuskSyncWorkerStatus.DiagnosticCode` is one of:

| Code | Meaning |
| --- | --- |
| `transform-version-mismatch` | The transform changed. See [rebuild needed](#the-transform-changed-and-a-rebuild-is-needed). |
| `destination-durability-failure` | The destination did not confirm the exact commit position. |
| `worker-fault` | Any other exception stopped the worker. Read the log. |
| `worker-cancelled` | The worker was cancelled. |
| `pipeline-fault` | The pipeline recorded an error (`LastError`). |

Metrics come from the `BlueTusk.Sync` meter, tagged `sync.pipeline.id`:

| Metric | Unit | What it tells you |
| --- | --- | --- |
| `bluetusk.sync.transactions` | `{transaction}` | Transactions applied. Flat while the source changes means stuck. |
| `bluetusk.sync.transaction.duration` | ms | Time per transaction, including retries. |
| `bluetusk.sync.retries` | `{attempt}` | Retry attempts. |
| `bluetusk.sync.throttle.duration` | ms | Time spent waiting on `RateLimit`. |
| `bluetusk.sync.snapshot.rows` | `{row}` | Rows copied by snapshots. |
| `bluetusk.sync.errors` | `{error}` | Pipelines stopped by an error. |

Traces use the `BlueTusk.Sync` activity source (`sync.transaction.consume`,
`sync.snapshot.consume`).

## The destination is unavailable

| Symptom | Cause | Fix |
| --- | --- | --- |
| `BlueTuskException: Could not open a PostgreSQL connection matching Any across 1 configured host(s).` after several attempts; `bluetusk.sync.retries` rose | The PostgreSQL destination retried until `MaximumAttempts` (default 5) ran out. | Fix the destination, then restart. Raise `MaximumAttempts` or `MaximumDelay` to ride out longer outages. |
| A NATS, Redis, OpenSearch, Kafka or S3 error stops the pipeline at once | These destinations do not classify errors, so nothing is retried. | Register an `ISyncRetryClassifier` ([how](configuration.md#retry-transient-destination-errors)). |
| A PostgreSQL error you expect to be retried stops the pipeline at once | Your registered classifier replaces the destination's own, or the SQLSTATE is not transient. | Return `true` for it in your classifier. |
| Your `IPostgreSqlSyncMutationWriter` runs the same transaction more than once | The destination retried a transient error, such as a lock timeout, in a new database transaction. | Expected. Keep non-database side effects out of the writer. |
| `WebhookSyncDeliveryException: Webhook receiver '<host>' returned HTTP 400; the Sync checkpoint was not advanced.` | A non-transient status. Only 408, 425, 429 and 5xx are retried. | Fix the receiver. |
| `WebhookSyncProtocolException` about `BlueTusk-Delivery-Status` | The receiver replied 2xx without `applied` or `duplicate`. | Return the header. Sync never treats an unclear success as applied. |
| `KafkaSyncDeliveryException: Kafka did not confirm an atomic transaction...` | The commit outcome is unknown. | Restart the worker; provisioning reloads the state topic and resolves it. |
| `S3SyncDeliveryException: S3 did not confirm immutable object '<key>'...` | The write failed or timed out. | Retry is safe: keys are deterministic. |

## I see duplicate rows or repeated events

Delivery is at least once ([why duplicates are safe](concepts.md#why-duplicates-are-safe)).

| Symptom | Cause | Fix |
| --- | --- | --- |
| Extra rows in a PostgreSQL table written by your `IPostgreSqlSyncMutationWriter` | The writer inserts instead of upserting. | Use `INSERT ... ON CONFLICT (key) DO UPDATE`, as in the [quick start](quickstart.md). |
| The same document under two keys | The key is not deterministic (time, random value, or a column that changes). | Build `Key` from the primary key only. |
| A NATS, Kafka or webhook consumer processes an event twice | Redelivery after a crash, or after the NATS `DuplicateWindow`. | Store the stable delivery or mutation ID with your consumer's effect and skip repeats. |
| Two pipelines overwrite each other | Two `PipelineId` values write the same table or index. | Give each target one pipeline. |

## Mapping and schema errors

| Symptom | Cause | Fix |
| --- | --- | --- |
| `NotSupportedException: No default binary change decoder is registered for System.String with N bytes.` during the snapshot | Snapshot values arrive in binary format; `ChangeValueDecoders.Decode<string>` only decodes text-format strings. | Decode text columns as UTF-8 (`Encoding.UTF8.GetString(value.Data.Span)`), or use a [typed mapping](../streams/typed-mappings.md). |
| `InvalidOperationException: Column state NotPublished does not contain a decodable value.` (or `UnchangedToast`, `OldValueUnavailable`) | A delete carries only key columns, or an update left a large value unchanged. | Read only the key from `OldRow`. Set `REPLICA IDENTITY FULL` on the source table if you need old values. |
| `ChangeDeliveryNotAcknowledgedException: The previous change transaction was not acknowledged; its final state is Nacked.` | Your transform threw `SyncPoisonRecordException` and the policy is `Pause`. | Fix the data or transform and restart, or choose a quarantine policy. |
| `BlueTuskException` such as `column "x" of relation "orders" does not exist` | The target table does not match what your writer sends. | Change the target schema first, then the transform, then [rebuild](#the-transform-changed-and-a-rebuild-is-needed). |
| `PostgreSqlSyncException: A <n>-byte document exceeds the <m>-byte limit.` or `The transformed transaction exceeds the <m>-byte limit.` | A document or transaction is larger than `MaxDocumentBytes` or `MaxTransactionBytes`. | Raise the limit or send less content. Redis, NATS, Kafka, OpenSearch and S3 have similar limits in their options. |
| A new source column never reaches the target | Changes carry it, but your transform and the snapshot's `ChangeTable` do not. | Add it to both, bump the transform version and rebuild. |

## The transform changed and a rebuild is needed

```text
BlueTusk.Sync.SyncTransformVersionMismatchException: The destination transform fingerprint '<old>' does not match requested fingerprint '<new>'. An explicit rebuild or migration is required.
```

The stored transform version differs from the running one. Either revert the
version, or rebuild the target ([options per destination](concepts.md#changing-the-transform-rebuild-and-repair)).
For the PostgreSQL destination, the quickest rebuild is to remove the pipeline
record and restart; the worker provisions again and re-copies the source:

```sql
DELETE FROM bluetusk_sync.pipelines WHERE pipeline_id = 'orders-replica';
```

A related error is `PostgreSqlSyncSourceMismatchException: Pipeline '<id>' belongs to source '<a>', not '<b>'.`
(Redis, OpenSearch, Kafka and S3 have equivalents). The source identity
changed: a different database, slot name or publication fingerprint, or a
restored cluster. Point the pipeline back at its source, or use a new
`PipelineId` and rebuild.

## Lag keeps growing

A pipeline applies one transaction at a time. If the source writes faster than
the destination accepts, lag grows.

1. Check `bluetusk.sync.transaction.duration`. Slow transactions point at the
   destination or your writer; batch statements in a custom writer.
2. Check `bluetusk.sync.throttle.duration`. Non-zero means `RateLimit` is
   holding the pipeline back.
3. Check `bluetusk.sync.retries`. Steady retries mean the destination is
   failing intermittently.
4. Split the work into several pipelines (by table or destination). Ordering
   is only kept within a pipeline.

Check the slot on the source:

```sql
SELECT slot_name, active,
       pg_size_pretty(pg_wal_lsn_diff(pg_current_wal_lsn(), confirmed_flush_lsn)) AS behind
FROM pg_replication_slots;
```

## Why does WAL keep growing with a direct pipeline?

A running direct pipeline confirms each transaction to PostgreSQL after the
destination commits it, so `confirmed_flush_lsn` moves forward and the slot
releases WAL ([how](../streams/concepts.md#how-the-slot-releases-wal)). If WAL
still grows:

| Cause | Fix |
| --- | --- |
| The worker or pipeline stopped. The slot keeps WAL from its last confirmed position. | Fix the cause and restart, or drop a slot you no longer need. |
| The pipeline is slow. | See [lag keeps growing](#lag-keeps-growing). |
| Your `observerFactory` observer never sends feedback; it replaces the stream's own confirmation. | Send the position from its `AcknowledgeAsync`, or remove the `observerFactory`. |
| The worker runs 1.0.0 or 1.1.0-rc.1, where a source without an observer never confirmed positions. | Upgrade to 1.1.0, or pass an observer that sends the position. |

## Permission and start-up errors

| Symptom | Cause | Fix |
| --- | --- | --- |
| `BlueTuskServerException: permission denied to start WAL sender` | The source role lacks `REPLICATION`. | `ALTER ROLE ... REPLICATION`. |
| `SnapshotRestartLimitExceededException: Snapshot bootstrap failed after 3 attempts...` with inner `permission denied for table orders` | The source role cannot `SELECT` a published table. | `GRANT SELECT ON ... TO` the replication role. |
| `BlueTuskException: permission denied for table orders` from the worker | The target role cannot write your table. | Grant `SELECT, INSERT, UPDATE, DELETE` on the target table. |
| `permission denied for database` on first start | The target role cannot create the `bluetusk_sync` schema. | `GRANT CREATE ON DATABASE`, or let an owner create the schema first. |
| `SnapshotAttemptException: Existing slot orders_sync is active or does not belong to the configured pgoutput snapshot source; it cannot be replaced safely.` | Another worker is using the slot, or the slot belongs to something else. | Run one worker per pipeline. Use a distinct slot name per pipeline. |
| `InvalidOperationException: A BlueTusk Sync pipeline named '<id>' is already registered.` | Two registrations share a `PipelineId`. | Use unique IDs. |
| `ArgumentException: A quarantine poison policy requires a durable quarantine sink.` | A quarantine policy with a destination that is not a sink. | Pass `quarantineFactory`, or use `Pause`. |

## Destination-specific gotchas

| Destination | Watch out for |
| --- | --- |
| PostgreSQL | A custom `MutationWriter` disables built-in reconciliation. Keep the target and source in separate databases. |
| Redis | `KeyPrefix` cannot contain `{` or `}`. Large transactions hit `MaxMutationsPerTransaction` and stop the pipeline instead of blocking Redis. |
| NATS JetStream | If the stream's settings differ from `NatsSyncOptions` (changed by hand or in code), provisioning stops with `NatsSyncStreamConfigurationException`; use a new stream. `DuplicateWindow` must not exceed `MaxAge`. |
| OpenSearch | Bulk items apply separately; `OpenSearchSyncBulkException` means a partial failure that is replayed after you fix the cause (often a mapping conflict). |
| Kafka | Topics must have one partition. The state topic needs `cleanup.policy=compact`. Each running pipeline needs its own `TransactionalId`. |
| S3 | Readers must list `commits/`, not `data/`; orphan data objects after a crash are expected. Use a new `Prefix` per transform version. |
| Webhooks | HTTPS only, unless `AllowInsecureHttp` for local tests. Store `BlueTusk-Delivery-Id` in the same transaction as your effect. |

Still stuck? The [Sync reference](reference.md) describes each connector's
recovery rules in depth, and [Streams troubleshooting](../streams/troubleshooting.md)
covers slot and publication problems.
