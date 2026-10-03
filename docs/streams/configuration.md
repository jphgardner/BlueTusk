# Streams configuration

This page lists every Streams setting you can change: the configuration keys
and environment variables used by the tooling, and each options class with its
properties and defaults.

All options classes are immutable records. Set them with object initializers;
each validates its values when you pass it to the component that uses it and
throws `ArgumentException` (or `ArgumentOutOfRangeException`) for invalid
values.

## Configuration keys

Streams does **not** bind `IConfiguration` sections by itself. The keys below are
a shared convention: the [Aspire integration](aspire.md) sets them, the
[`bluetusk-streams` tool](cli.md) and the [sample](sample.md) read some of them,
and your worker reads them and passes the values to the options classes (as the
[quick start](quickstart.md) does).

| Environment variable | Configuration key | Used by | Meaning |
| --- | --- | --- | --- |
| `BLUETUSK_STREAMS_SOURCE` | `BLUETUSK_STREAMS_SOURCE` | Aspire, tool, sample | Source database connection string. |
| `BLUETUSK_STREAMS_CONTROL` | `BLUETUSK_STREAMS_CONTROL` | Aspire (relay mode), tool | Control database for the relay and state store. |
| `BlueTusk__Streams__Slot` | `BlueTusk:Streams:Slot` | Aspire, sample | Replication slot name. |
| `BlueTusk__Streams__Publications__0`, `__1`, ... | `BlueTusk:Streams:Publications:0`, ... | Aspire, sample | Publication names, one key per publication. |
| `BlueTusk__Streams__ConsumerGroup` | `BlueTusk:Streams:ConsumerGroup` | Aspire | Consumer group name. |
| `BlueTusk__Streams__ControlSchema` | `BlueTusk:Streams:ControlSchema` | Aspire | Relay and state schema. Default `bluetusk_streams`. |
| `BlueTusk__Streams__DeliveryMode` | `BlueTusk:Streams:DeliveryMode` | Aspire | `DurableRelay` or `Direct`. |
| `BlueTusk__Streams__Sample__Schema`, `__Table` | `BlueTusk:Streams:Sample:Schema`, `:Table` | sample | Table the sample copies. Defaults `app`, `orders`. |

The same keys in `appsettings.json`:

```json
{
  "BlueTusk": {
    "Streams": {
      "Slot": "orders_quickstart",
      "Publications": [ "orders_publication" ],
      "ConsumerGroup": "console"
    }
  }
}
```

Keep connection strings in environment variables or a secret store, not in
`appsettings.json`. Connection-string keywords are described in
[ADO.NET configuration](../ado-net/configuration.md). Replication uses
`BlueTuskDataSource.CreateDedicatedSessionOptions()`, which keeps the data
source's host, credentials and TLS settings.

## Transaction assembly and spooling

`TransactionAssemblyOptions` (namespace `BlueTusk.Streams`) is passed to
`PgOutputChangeStream`, or set as `PostgreSqlConsistentSnapshotOptions.TransactionAssembly`.

| Property | Type | Default | Meaning |
| --- | --- | --- | --- |
| `MaxInMemoryTransactionBytes` | `long` | 4 MiB | Above this size a transaction is written to a spool file. Must not exceed `MaxTransactionBytes`. |
| `MaxTransactionBytes` | `long` | 1 GiB | Largest transaction. Larger ones stop the stream with `TransactionAssemblyLimitExceededException`. |
| `MaxSpoolBytes` | `long` | 10 GiB | Total disk space for spool files, including files left from earlier runs. |
| `MaxChangesPerTransaction` | `int` | 1,000,000 | Most changes in one transaction. |
| `MaxRelationsPerTransaction` | `int` | 4,096 | Most distinct tables in one transaction. |
| `PreparedTransactionMode` | `PreparedTransactionMode` | `Fail` | `Stage` turns on [prepared-transaction](prepared-transactions.md) deliveries. |
| `SpoolDirectory` | `string` | `<temp>/bluetusk-streams-spool` | Spool file directory. Use a dedicated local directory per worker. |

To protect spool files at rest, pass your own spool:
`new FileTransactionSpool(new FileTransactionSpoolOptions { ... })` as the
`spool` argument of `PgOutputChangeStream`.

| `FileTransactionSpoolOptions` | Type | Default | Meaning |
| --- | --- | --- | --- |
| `DirectoryPath` | `string` | (required) | Spool directory. |
| `MaxStorageBytes` | `long` | 10 GiB | Total spool space. |
| `MaxRecordBytes` | `int` | 256 MiB | Largest single record. The built-in spool uses the smaller of `MaxTransactionBytes` and `int.MaxValue`. |
| `Protector` | `ITransactionSpoolProtector?` | `null` | Encrypts each record before it is written. |

## Snapshot bootstrap

`PostgreSqlConsistentSnapshotOptions` configures `PostgreSqlConsistentSnapshotSource`.

| Property | Type | Default | Meaning |
| --- | --- | --- | --- |
| `Source` | `ChangeSourceIdentity` | (required) | Must match the connected server and database. |
| `PublicationNames` | `IReadOnlyList<string>` | (required) | At least one publication. |
| `Tables` | `IReadOnlyList<PostgreSqlSnapshotTable>` | (required) | Tables to copy, each with key ordinals. |
| `CopyPageRows` | `int` | 2,048 | Rows per keyset `COPY` page. |
| `MaximumBatchRows` | `int` | 512 | Rows per consumer batch. |
| `MaximumBatchBytes` | `long` | 4 MiB | Bytes per consumer batch. |
| `MaximumRowBytes` | `long` | 4 MiB | Largest row. Must not exceed `MaximumBatchBytes`. |
| `MaximumParallelTables` | `int` | 4 | Tables copied at the same time. |
| `ExistingSlotMode` | `PostgreSqlExistingSnapshotSlotMode` | `Fail` | `RestartSnapshot` replaces an inactive slot left by an earlier run. |
| `TransactionAssembly` | `TransactionAssemblyOptions` | defaults above | Used while streaming after the copy. |

`SnapshotThenStreamOptions.MaximumSnapshotAttempts` (default 3) limits how many
times a lost snapshot is retried.

## State stores

| `PostgreSqlStreamsStorageOptions` | Type | Default | Meaning |
| --- | --- | --- | --- |
| `ControlDataSource` | `DbDataSource` | (required) | Database that holds state and relay tables. |
| `ControlSchema` | `string` | `bluetusk_streams` | Schema name, at most 63 bytes. |

The same class configures the relay; see [below](#postgresql-storage-and-relay).

| `FileChangeStreamStateStoreOptions` | Type | Default | Meaning |
| --- | --- | --- | --- |
| `DirectoryPath` | `string` | (required) | Local directory for state files. |
| `LockTimeout` | `TimeSpan` | 30 s | How long to wait for another process's lock. |
| `LockRetryDelay` | `TimeSpan` | 20 ms | Pause between lock attempts. Must not exceed `LockTimeout`. |

| `RedisChangeStreamStateStoreOptions` | Type | Default | Meaning |
| --- | --- | --- | --- |
| `Connection` | `IConnectionMultiplexer` | (required) | Your Redis connection. |
| `Database` | `int` | -1 | Redis database (-1 is the connection's default). |
| `KeyPrefix` | `string` | `bluetusk:streams` | Key prefix. Must not contain `{` or `}`. |

`CheckpointingChangeDeliveryObserver.AcquireAsync` takes the lease duration as
an argument; the samples use 30 seconds. Renew at about a third of that. See
[checkpoint and lease stores](state-stores.md).

## PostgreSQL storage and relay

The remaining `PostgreSqlStreamsStorageOptions` properties apply to the
[durable relay](durable-relay.md):

| Property | Type | Default | Meaning |
| --- | --- | --- | --- |
| `MaxRelayStorageBytes` | `long` | 100 GiB | Total stored transactions. |
| `MaxEnvelopeBytes` | `int` | 256 MiB | Largest stored transaction. |
| `ResumeRetentionWindow` | `TimeSpan` | 1 hour | Minimum time a transaction is kept after all groups acknowledge it. |
| `RemovedConsumerGroupRetentionWindow` | `TimeSpan` | 1 hour | How long a removed group's unacknowledged transactions are kept. |
| `MinimumRetainedTransactions` | `int` | 0 | Transactions always kept, even when acknowledged. |
| `RetentionDeleteBatchSize` | `int` | 1,000 | Deletions per retention batch. |
| `MaxCompactionBatches` | `int` | 100 | Batches per `CompactAsync` call. |
| `MaxAcknowledgementAge` | `TimeSpan` | 5 minutes | `GetHealthAsync` flags older unacknowledged transactions. |
| `MaxWalLagBytes` | `long` | 10 GiB | `GetHealthAsync` flags a larger WAL lag. |
| `EnvelopeProtection` | `IChangeRelayEnvelopeProtectionProvider?` | `null` | Encrypts stored transactions. |

| `PostgreSqlRelayChangeStreamOptions` | Type | Default | Meaning |
| --- | --- | --- | --- |
| `ConsumerGroup` | `string` | (required) | Group name. |
| `OwnerId` | `string` | (required) | Unique ID of this worker process. |
| `NewGroupStart` | `ChangeRelayConsumerGroupStart` | `EarliestAvailable` | Where a new group starts; or `Latest`. |
| `MaxTransactionsPerRead` | `int` | 128 | Transactions per read query. |
| `MaxBytesPerRead` | `long` | 8 MiB | Bytes per read query (one transaction may exceed it). |
| `EmptyReadDelay` | `TimeSpan` | 100 ms | Wait before polling again when nothing is new. |
| `LeaseDuration` | `TimeSpan` | 30 s | Group lease length. |
| `LeaseRenewalInterval` | `TimeSpan` | 10 s | Must be shorter than `LeaseDuration`. |

`ChangeRelayBackupOptions`: `MaxFrameBytes` (257 MiB), `IncludeSnapshotRuns`
(`true`), `IncludeDeadLetters` (`true`). `ChangeRelayRestoreOptions`:
`MaxFrameBytes` (257 MiB).

## Typed mappings

`ChangeMappingPolicy` is the optional second argument of
`ChangeEntityMappingBuilder<T>.Build` and `BlueTuskEfChangeMappingFactory.Create`.

| Property | Default | Values |
| --- | --- | --- |
| `SchemaChangeMode` | `PauseAndReload` | `Fail`, `ContinueDynamically`, `ApplicationCallback` |
| `DecodingFailureMode` | `Pause` | `ContinueDynamically`, `ApplicationCallback` |
| `SchemaChangeCallback` | `null` | Required for `ApplicationCallback`. |
| `DecodingFailureCallback` | `null` | Required for `ApplicationCallback`. |

See [typed mappings](typed-mappings.md).

## CloudEvents

| `ChangeTransactionCloudEventOptions` | Default |
| --- | --- |
| `EventType` | `io.bluetusk.streams.transaction.v1` |
| `DataContentType` | `application/vnd.bluetusk.change-transaction+binary;version=1` |
| `MaximumEventBytes` | 384 MiB |
| `Envelope` | `ChangeTransactionEnvelopeOptions` |

| `ChangeTransactionEnvelopeOptions` | Default |
| --- | --- |
| `MaxEnvelopeBytes` | 256 MiB |
| `MaxChanges` | 1,000,000 |
| `MaxTables` | 4,096 |
| `MaxColumnsPerTable` | 16,384 |
| `MaxStringBytes` | 1 MiB |

## Aspire

`BlueTuskStreamsAspireOptions`: `Slot`, `Publications` and `ConsumerGroup` are
required; `ControlSchema` defaults to `bluetusk_streams`; `DeliveryMode`
defaults to `DurableRelay`. See [Aspire](aspire.md).

## Health and telemetry

| Item | Name |
| --- | --- |
| Health check | `bluetusk_streams`, tags `bluetusk`, `streams`, `ready` |
| Meter and activity source | `BlueTusk.Streams` (`BlueTuskStreamsDiagnostics.InstrumentationName`) |
| Snapshot activity | `bluetusk.streams.snapshot` |
| Metrics | `bluetusk.streams.*`; listed in [hosting and observability](hosting-observability.md#collect-metrics-and-traces) |

There are no options to turn these on; subscribe with your telemetry library.

## Replication options

The replication request and decoder options belong to
`BlueTusk.Replication`:

| `BlueTuskPgOutputReplicationOptions` | Default | Note |
| --- | --- | --- |
| `SlotName`, `PublicationNames` | (required) | |
| `StartPosition` | `0/0` | Pass your checkpoint to skip acknowledged transactions. |
| `ProtocolVersion` | 1 | Use 2 for `StreamingMode.On`, 3 for `TwoPhase`. |
| `StreamingMode` | `Off` | `On` streams large in-progress transactions. |
| `Messages` | `false` | Include logical messages (`pg_logical_emit_message`). |
| `Binary` | `false` | Binary column values. |
| `TwoPhase` | `false` | Two-phase decoding. |
| `OriginMode` | `Any` | `None` skips changes that came from replication. |

`BlueTuskPgOutputDecoderOptions` must use the same `ProtocolVersion`,
`StreamingMode` and `TwoPhase`. See the [replication guide](../replication/README.md).
