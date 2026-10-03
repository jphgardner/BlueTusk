# Configure Sync

This page lists every option you set for a Sync pipeline and for each
destination, with types, defaults and connection settings. All names and
defaults come from the 1.1.0 source. For the ideas behind them, read
[Sync concepts](concepts.md).

## Where does configuration go?

Sync is configured in code with options records. It does not bind an
`appsettings.json` section by itself. Read secrets and endpoints from
`IConfiguration` (or a secret store) and pass them into the options, as the
examples below do.

The only configuration keys Sync writes are the ones `BlueTusk.Sync.Aspire`
sets on a worker. See [Aspire](#aspire).

## Register pipelines

`AddBlueTuskSync()` (package `BlueTusk.Sync.DependencyInjection`) registers the
hosted worker, a health check named `bluetusk_sync` (tags `bluetusk`, `sync`,
`ready`), `IBlueTuskSyncStatusSource` for per-pipeline status, and the
`BlueTusk.Sync` meter and activity source. It returns a builder:

| Method | Use it for |
| --- | --- |
| `AddHostedPipeline<TTransform, TDestination>(options, source, sourceFactory, snapshotOptions, quarantineFactory)` | A direct slot. `sourceFactory` returns an `IConsistentSnapshotSource`; `snapshotOptions` is `SnapshotThenStreamOptions` (`MaximumSnapshotAttempts`, default `3`). |
| `AddHostedPipelineSource<TTransform, TDestination>(options, source, sourceFactory, quarantineFactory)` | A restart-aware source such as `PostgreSqlRelaySyncPipelineSource` (durable relay). |
| `AddRebuildCutover<TPositionProvider, THandoffHandler>()` | Zero-downtime rebuild cutover for a hosted worker. |
| `AddPostgreSqlRelayRebuildCutover<THandoffHandler>()` | The same, reading the cutover position from the durable relay. |

Each `PipelineId` can be registered once. The transform and destination types
are resolved from dependency injection as singletons. An
`ISyncRetryClassifier` registered in the container is used by every pipeline.

```csharp
builder.Services.AddSingleton(new PostgreSqlSyncDestination(new PostgreSqlSyncOptions
{
    DestinationDataSource = target,
}));
builder.Services.AddBlueTuskSync()
    .AddHostedPipeline<OrdersTransform, PostgreSqlSyncDestination>(
        new SyncPipelineOptions { PipelineId = "orders-replica" },
        sourceIdentity,
        _ => new PostgreSqlConsistentSnapshotSource(source, snapshotOptions));
```

Source options (`PostgreSqlConsistentSnapshotOptions`, relay options) belong to
Streams. See [Streams configuration](../streams/configuration.md).

## Pipeline options

`SyncPipelineOptions`:

| Option | Type | Default | Meaning |
| --- | --- | --- | --- |
| `PipelineId` | `string` | required | Stable pipeline name. The destination keys its checkpoint and transform version by it. |
| `PoisonRecordPolicy` | `SyncPoisonRecordPolicy` | `Pause` | `Pause`, `QuarantineAndPause` or `QuarantineAndAdvance`. Quarantine policies need a quarantine sink. |
| `Retry` | `SyncRetryOptions` | `new()` | Backoff for errors your classifier marks as transient. |
| `RateLimit` | `SyncRateLimitOptions` | `new()` | Optional pacing. No limit by default. |

`SyncRetryOptions`:

| Option | Type | Default | Meaning |
| --- | --- | --- | --- |
| `MaximumAttempts` | `int` | `5` | Total attempts, including the first (1 to 100). |
| `InitialDelay` | `TimeSpan` | 100 ms | Delay before the second attempt. |
| `MaximumDelay` | `TimeSpan` | 10 s | Upper limit for any delay. |
| `BackoffFactor` | `double` | `2` | Multiplier per attempt (at least 1). |
| `JitterRatio` | `double` | `0.2` | Random spread, 0 to 1. |

`SyncRateLimitOptions`:

| Option | Type | Default | Meaning |
| --- | --- | --- | --- |
| `MaximumTransactionsPerSecond` | `double?` | `null` | Caps source transactions per second. |
| `MaximumTransformedBytesPerSecond` | `long?` | `null` | Caps transformed bytes per second, snapshot batches included. |

```csharp
var pipelineOptions = new SyncPipelineOptions
{
    PipelineId = "orders-replica",
    PoisonRecordPolicy = SyncPoisonRecordPolicy.QuarantineAndPause,
    Retry = new SyncRetryOptions
    {
        MaximumAttempts = 5,
        InitialDelay = TimeSpan.FromMilliseconds(100),
        MaximumDelay = TimeSpan.FromSeconds(10),
    },
    RateLimit = new SyncRateLimitOptions
    {
        MaximumTransactionsPerSecond = 500,
        MaximumTransformedBytesPerSecond = 16 * 1024 * 1024,
    },
};
```

## Retry transient destination errors

Sync retries nothing unless an `ISyncRetryClassifier` returns `true` for the
failure. `SyncRetryContext` gives you `PipelineId`, `Destination`, `Operation`
(`SyncPipelineOperation`), `Attempt` and `Exception`. This classifier retries
lost connections and PostgreSQL errors that are safe to repeat:

```csharp
public sealed class TransientFailures : ISyncRetryClassifier
{
    public bool IsTransient(SyncRetryContext context)
    {
        if (context.Exception is BlueTuskException { SqlState: { } state })
        {
            return state.StartsWith("08", StringComparison.Ordinal) ||
                state is "40001" or "40P01" or "53300" or "57P01";
        }

        for (var error = context.Exception; error is not null; error = error.InnerException)
        {
            if (error is TimeoutException or System.Net.Sockets.SocketException)
            {
                return true;
            }
        }

        return false;
    }
}
```

```csharp
builder.Services.AddSingleton<ISyncRetryClassifier, TransientFailures>();
```

Built-in destinations detect duplicates, so repeating the same batch after an
unclear outcome is safe. Kafka is the exception: after
`KafkaSyncDeliveryException` the destination must be provisioned again, which
means restarting the worker.

## Transform options

`JsonSyncTransformStageOptions` (for `JsonSyncTransformStage`, used inside a
`CompositeSyncTransform`):

| Option | Type | Default | Meaning |
| --- | --- | --- | --- |
| `Name`, `Version` | `string` | required | Part of the transform fingerprint. |
| `RedactedPaths` | `IReadOnlyList<string>` | empty | Dotted JSON paths to remove. |
| `EnrichmentJson` | `IReadOnlyDictionary<string, string>` | empty | Root properties to add (values are JSON). |
| `FlattenObjects` | `bool` | `false` | Flattens nested objects. |
| `FlattenSeparator` | `string` | `"."` | Separator for flattened names (up to 8 characters). |
| `TenantPropertyPath` | `string?` | `null` | Path whose value becomes the partition key. |
| `RequireTenant` | `bool` | `true` | Rejects documents without a tenant value. |
| `MaximumDocumentBytes` | `int` | 1 MiB | Largest document in or out. |

`SyncTransformSandboxOptions` (for `SandboxedSyncTransformStage`) take
`Name`, `Version` and `Instructions`, plus limits: `MaximumMutationsPerBatch`
(10,000), `MaximumDocumentBytes` (1 MiB), `MaximumBatchBytes` (16 MiB),
`MaximumOperationsPerBatch` (1,000,000), `MaximumJsonDepth` (64),
`MaximumExecutionTime` (5 s) and `RequirePartitionedDeletes` (`true`). Every
option is part of the fingerprint, so changing one requires a
[rebuild](concepts.md#changing-the-transform-rebuild-and-repair).

## Rebuild and reconciliation options

| Class | Option | Default | Meaning |
| --- | --- | --- | --- |
| `SyncRebuildOptions` | `PipelineId` | required | Pipeline to rebuild. |
| | `MaximumSnapshotAttempts` | `3` | Snapshot attempts after a lost exporter session. |
| | `RetirePreviousGeneration` | `false` | Delete the old generation after activation. |
| `SyncReconciliationRequest` | `PipelineId`, `Collection` | required | What to compare. |
| | `Mode` | `PartitionedContentHash` | `Count`, `KeySet` or `PartitionedContentHash`. |
| | `PartitionCount` | `256` | Key partitions (1 to 65,536). |
| | `MaxReportedDifferences` | `1000` | Sample differences kept in the result. |
| | `Repair` | `false` | Fix differences (not with `Count`). |
| | `RepairBatchSize` | `500` | Repairs per destination call (1 to 10,000). |
| | `MaxBufferedRepairsPerPartition` | `100000` | Memory limit per partition. |

## Destinations

Install only the package for the destination you use.

### PostgreSQL

Package `BlueTusk.Sync.PostgreSql`. Connection: a `DbDataSource` for the
target database, usually a `BlueTuskDataSource`. The role needs permission to
create the control schema on first start, plus rights on your own tables when
you use a custom writer.

| Option | Type | Default | Meaning |
| --- | --- | --- | --- |
| `DestinationDataSource` | `DbDataSource` | required | Target database. |
| `ControlSchema` | `string` | `"bluetusk_sync"` | Schema for `pipelines`, `documents`, `quarantine` and `storage_metadata`. |
| `MaxDocumentBytes` | `int` | 16 MiB | Largest single document. |
| `MaxTransactionBytes` | `long` | 256 MiB | Largest transformed transaction. |
| `MutationWriter` | `IPostgreSqlSyncMutationWriter?` | `null` | Writes into your own tables. `null` stores JSON documents in `<ControlSchema>.documents`. |

The [quick start](quickstart.md) shows a custom writer. A custom writer turns
off built-in reconciliation.

### Redis

Package `BlueTusk.Sync.Redis`. Connection: a StackExchange.Redis
`IConnectionMultiplexer`.

| Option | Type | Default | Meaning |
| --- | --- | --- | --- |
| `Connection` | `IConnectionMultiplexer` | required | Redis connection. |
| `Database` | `int` | `-1` | Database number (`-1` is the default database). |
| `KeyPrefix` | `string` | `"bluetusk:sync"` | Key prefix. Must not contain `{` or `}`. |
| `MaxDocumentBytes` | `int` | 8 MiB | Largest document. |
| `MaxTransactionBytes` | `long` | 32 MiB | Largest transaction. |
| `MaxMutationsPerTransaction` | `int` | `10000` | Most mutations in one script call. |

### NATS JetStream

Package `BlueTusk.Sync.Nats`. Connection: an `INatsJSContext` from NATS.Net.

| Option | Type | Default | Meaning |
| --- | --- | --- | --- |
| `JetStream` | `INatsJSContext` | required | JetStream context. |
| `StreamName` | `string` | required | Stream name (no `.`, `*`, `>` or spaces). |
| `SubjectPrefix` | `string` | required | Messages go to `<SubjectPrefix>.>`. |
| `CreateStream` | `bool` | `true` | Create the stream if missing; the contract is validated either way. |
| `MaxAge` | `TimeSpan` | 7 days | Stream retention. |
| `MaxBytes` | `long` | 10 GiB | Stream size limit. |
| `MaxMessageBytes` | `int` | 8 MiB | Largest envelope. |
| `DuplicateWindow` | `TimeSpan` | 24 hours | JetStream de-duplication window. Must cover your longest outage. |
| `Replicas` | `int` | `1` | Stream replicas (1 to 5). |
| `PublishRetryAttempts` | `int` | `3` | Publish attempts (1 to 20). |
| `PublishRetryDelay` | `TimeSpan` | 100 ms | Delay between publish attempts. |

### OpenSearch

Package `BlueTusk.Sync.OpenSearch`. Connection: an `HttpClient` whose
`BaseAddress` is the cluster URL; add authentication to the client.

| Option | Type | Default | Meaning |
| --- | --- | --- | --- |
| `Client` | `HttpClient` | required | Must have an absolute `BaseAddress`. |
| `IndexPrefix` | `string` | `"bluetusk-sync"` | Lowercase prefix for indexes and aliases. |
| `NumberOfShards` | `int` | `1` | Primary shards per index. |
| `NumberOfReplicas` | `int` | `0` | Replica shards. |
| `WaitForActiveShards` | `string` | `"all"` | `all` or a positive number. |
| `MaxDocumentBytes` | `int` | 8 MiB | Largest document. |
| `MaxBulkBytes` | `long` | 32 MiB | Largest bulk request. |
| `MaxMutationsPerTransaction` | `int` | `10000` | Most mutations per transaction. |
| `MaxReconciliationKeyBytes` | `int` | 8 KiB | Longest key during reconciliation. |
| `ReconciliationPageSize` | `int` | `512` | Page size for reconciliation reads. |
| `RefreshAfterWrite` | `bool` | `false` | Wait until writes are searchable. |

### Kafka

> **Note:** New in 1.1.0.

Package `BlueTusk.Sync.Kafka`. Connection: `BootstrapServers` plus any
Confluent client settings in `ClientConfiguration`.

| Option | Type | Default | Meaning |
| --- | --- | --- | --- |
| `BootstrapServers` | `string` | required | Broker list. |
| `TopicPrefix` | `string` | required | Uses `<prefix>.events` and the compacted `<prefix>.state`. |
| `TransactionalId` | `string` | required | Unique per active pipeline writer. |
| `ClientId` | `string` | `"bluetusk-sync"` | Client ID. |
| `CreateTopics` | `bool` | `true` | Create missing topics. |
| `PartitionCount` | `int` | `1` | Must be `1` to keep commit order. |
| `ReplicationFactor` | `short` | `3` | Replication for created topics (1 to 5). |
| `MaxEnvelopeBytes` | `int` | 8 MiB | Largest envelope. |
| `InitializationTimeout` | `TimeSpan` | 30 s | Producer start-up timeout. |
| `TransactionTimeout` | `TimeSpan` | 30 s | Kafka transaction timeout. |
| `ClientConfiguration` | `IReadOnlyDictionary<string, string>` | empty | Extra Confluent settings such as SASL and TLS. |

```csharp
var kafka = new KafkaSyncDestination(new KafkaSyncOptions
{
    BootstrapServers = configuration["Kafka:BootstrapServers"]!,
    TopicPrefix = "bluetusk.orders",
    TransactionalId = "orders-sync-primary",
    ClientConfiguration = new Dictionary<string, string>
    {
        ["security.protocol"] = "SaslSsl",
        ["sasl.mechanism"] = "SCRAM-SHA-512",
        ["sasl.username"] = configuration["Kafka:Username"]!,
        ["sasl.password"] = configuration["Kafka:Password"]!,
    },
});
```

### S3 and Parquet

> **Note:** New in 1.1.0.

Package `BlueTusk.Sync.S3`. Connection: an `IAmazonS3` client using the normal
AWS credential chain.

| Option | Type | Default | Meaning |
| --- | --- | --- | --- |
| `Client` | `IAmazonS3` | required | S3 client. |
| `BucketName` | `string` | required | Bucket. |
| `Prefix` | `string` | required | Object prefix owned by this pipeline. Use a new prefix per transform version. |
| `ServerSideEncryption` | `ServerSideEncryptionMethod` | `AES256` | Encryption requested on write. |
| `KmsKeyId` | `string?` | `null` | KMS key; requires `AWSKMS`. |
| `MaxMutationCount` | `int` | `100000` | Most mutations per object. |
| `MaxParquetBytes` | `int` | 64 MiB | Largest Parquet object. |

### Webhooks

> **Note:** New in 1.1.0.

Package `BlueTusk.Sync.Webhooks`. Connection: an `HttpClient` and an HTTPS
endpoint.

| Option | Type | Default | Meaning |
| --- | --- | --- | --- |
| `Client` | `HttpClient` | required | HTTP client. |
| `Endpoint` | `Uri` | required | Absolute HTTPS URI. |
| `KeyId` | `string` | required | Key name sent in `BlueTusk-Key-Id`. |
| `SigningKey` | `ReadOnlyMemory<byte>` | required | HMAC-SHA256 key, at least 32 bytes. |
| `AllowInsecureHttp` | `bool` | `false` | Allows `http://` for local tests only. |
| `MaxEnvelopeBytes` | `int` | 8 MiB | Largest request body. |
| `MaximumAttempts` | `int` | `5` | Attempts for 408, 425, 429, 5xx and network errors (1 to 10). |
| `InitialRetryDelay` | `TimeSpan` | 100 ms | First retry delay. |
| `MaximumRetryDelay` | `TimeSpan` | 5 s | Longest retry delay. |
| `TimeProvider` | `TimeProvider` | `TimeProvider.System` | Clock for signatures and delays. |

```csharp
var webhook = new WebhookSyncDestination(new WebhookSyncOptions
{
    Client = new HttpClient(),
    Endpoint = new Uri("https://receiver.example.com/bluetusk/sync"),
    KeyId = "orders-2026-10",
    SigningKey = Convert.FromBase64String(configuration["WebhookSigningKey"]!),
});
```

Your receiver must check the `BlueTusk-Signature` header, store
`BlueTusk-Delivery-Id` with its result, and reply with
`BlueTusk-Delivery-Status: applied` or `duplicate`.

## Aspire

`BlueTusk.Sync.Aspire` adds `WithBlueTuskSync(source, control, destination,
options)` (durable relay) and `WithBlueTuskSyncDirect(source, destination,
options)` to a worker resource. `BlueTuskSyncAspireOptions` has `PipelineId`,
`ConsumerGroup`, `TransformVersion`, `Destination` (`PostgreSql`, `Nats`,
`Redis`, `OpenSearch`), `ControlSchema` (`"bluetusk_streams"`), `DeliveryMode`
(`DurableRelay`), `ReconciliationEnabled` (`true`) and `RebuildEnabled`
(`true`). The worker receives:

| Variable | Value |
| --- | --- |
| `BLUETUSK_SYNC_SOURCE`, `BLUETUSK_SYNC_DESTINATION` | Connection strings. |
| `BLUETUSK_SYNC_CONTROL` | Relay control connection string (relay mode only). |
| `BlueTusk__Sync__PipelineId`, `__ConsumerGroup`, `__TransformVersion`, `__Destination`, `__ControlSchema`, `__DeliveryMode`, `__ReconciliationEnabled`, `__RebuildEnabled` | The options above. |

Your worker reads these values and builds the pipeline; the Aspire package
does not register it for you.

Full engineering detail is in the [Sync reference](reference.md).
