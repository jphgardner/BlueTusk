# Checkpoint and lease stores

This guide helps you choose where a Streams consumer keeps its checkpoint and
lease, and shows how to set up each store. For what checkpoints and leases are,
read [concepts](concepts.md#checkpoints-leases-and-fencing).

## Choose a store

| Store | Package | Use it when |
| --- | --- | --- |
| `PostgreSqlChangeStreamStateStore` | `BlueTusk.Streams.Storage.PostgreSql` | Production. The default choice. |
| `FileChangeStreamStateStore` | `BlueTusk.Streams.Storage.File` | All workers run on one host with a local disk. |
| `RedisChangeStreamStateStore` | `BlueTusk.Streams.Storage.Redis` | You already run a durable, replicated Redis. |
| `MemoryChangeStreamStateStore` | `BlueTusk.Streams` | Tests only. State is lost when the process exits. |

Every store implements `IChangeStreamStateStore` and passes the same
conformance suite: compare-and-swap writes, no backward movement, exclusive
leases, increasing fencing tokens and lease expiry.

## Use the PostgreSQL store

Give the store a data source for the database that should hold the state, then
create its schema:

```csharp
var store = new PostgreSqlChangeStreamStateStore(new PostgreSqlStreamsStorageOptions
{
    ControlDataSource = controlDataSource,
    ControlSchema = "bluetusk_streams",
});
await store.InitializeAsync();
```

`InitializeAsync()` creates the schema and a `stream_state` table if they do
not exist, so the login needs `CREATE` on the database the first time. Lease
expiry uses the database clock, so workers with skewed clocks still agree.

Then take the lease for your consumer group and wrap the store in a delivery
observer:

```csharp
await using var checkpoints = await CheckpointingChangeDeliveryObserver.AcquireAsync(
    store,
    ChangeStreamStateKey.Create(source, "search-index"),
    ownerId: workerId,
    leaseDuration: TimeSpan.FromSeconds(30),
    ChangeStreamCheckpoint.CreateInitial(
        source,
        databaseIdentity: server.SystemIdentifier,
        outputPlugin: "pgoutput",
        mappingFingerprint: "search-index-v1"),
    new LogicalReplicationFeedbackSender(replication));

Console.WriteLine(checkpoints.Checkpoint is null
    ? "No checkpoint yet: start from the slot's confirmed position."
    : $"Resume after {checkpoints.Checkpoint.AcknowledgedCommitPosition}.");
```

- `ownerId` must be unique per running process, for example machine name plus
  process ID.
- `mappingFingerprint` is a value you choose. Change it when your consumer's
  output changes in an incompatible way; an old checkpoint then fails with
  `ChangeStreamCheckpointMismatchException` instead of being reused.
- Pass the observer to `PgOutputChangeStream` and start replication at
  `AcknowledgedCommitPosition`. The [quick start](quickstart.md) shows the
  whole worker.

The observer renews the lease in the background, three times per lease
duration, until you dispose it. Disposing it stops renewal and releases the
lease, so a clean shutdown lets the next process start straight away. See
[leases](concepts.md#checkpoints-leases-and-fencing) for what happens when a
lease is lost.

> **Note:** Keep the state schema out of your source publication. A
> `FOR ALL TABLES` publication in the same database would capture checkpoint
> writes. Prefer a separate control database, or publish named tables only.

## Check the slot before resuming

To catch a restored or replaced database before you read, validate the
checkpoint against the slot:

```csharp
if (checkpoints.Checkpoint is { } checkpoint)
{
    await replication.ValidateResumeCheckpointAsync(new BlueTuskLogicalReplicationCheckpoint(
        server.SystemIdentifier,
        server.DatabaseName!,
        source.SlotName,
        "pgoutput",
        checkpoint.AcknowledgedCommitPosition));
}
```

It throws `BlueTuskReplicationCheckpointException` if the slot is missing,
active elsewhere, temporary, has lost WAL, or does not match the server and
database. See [troubleshooting](troubleshooting.md#the-database-was-restored-or-replaced).

## Use the file store

The file store suits a single host:

```csharp
var fileStore = new FileChangeStreamStateStore(new FileChangeStreamStateStoreOptions
{
    DirectoryPath = stateDirectory,
    LockTimeout = TimeSpan.FromSeconds(30),
});
```

- Each consumer group is one `*.state` file (named by a hash, so names do not
  leak) and a `*.lock` file. Writes go to a temporary file, are flushed to disk
  and then atomically replace the old file. A checksum detects torn or modified
  files and stops with `FileChangeStreamStateStoreException`.
- Processes on the same host coordinate through the lock file. Do not use a
  network file system: its locking and rename behaviour may differ.
- Restrict the directory to the worker's account, and use an encrypted volume if
  checkpoint metadata must be encrypted at rest.
- Back up the whole directory. `*.tmp` files are unfinished writes and are never
  read.

## Use Redis

```csharp
var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
var redisStore = new RedisChangeStreamStateStore(new RedisChangeStreamStateStoreOptions
{
    Connection = redis,
    KeyPrefix = "bluetusk:streams",
});
```

- You own the `IConnectionMultiplexer`; the store never creates or disposes it.
- Each consumer group is one hash. Lua scripts make every lease and checkpoint
  operation atomic and use the Redis server clock. Keys use a hash tag, so
  Redis Cluster works; do not put `{` or `}` in `KeyPrefix`.
- The store cannot make Redis durable. Turn on persistence (AOF), replication
  and authentication, and use an eviction policy that never evicts these keys.

The relay always uses PostgreSQL, whichever checkpoint store you choose.

## Test a custom store

To write your own store, implement `IChangeStreamStateStore` and run the
conformance suite from `BlueTusk.Streams.Testing` against a real instance:

```csharp
var report = await ChangeStreamStateStoreConformance.RunAsync(customStore, "custom-store");
Console.WriteLine($"{report.StoreName}: {report.Assertions} checks passed in {report.Elapsed}");
```

```text
custom-store: 13 checks passed in 00:00:00.5634765
```

A failure throws `ChangeStreamStateStoreConformanceException`. Passing proves
the shared behaviour only; durability, backup, latency and security of your
backend are still up to you.

## Related pages

- [Configuration: state store options](configuration.md#state-stores)
- [Durable relay](durable-relay.md)
- [Troubleshooting: lease errors](troubleshooting.md#another-worker-owns-the-consumer-group)
