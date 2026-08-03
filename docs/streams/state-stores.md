# Streams checkpoint and lease stores

Every Streams state store implements the same monotonic checkpoint and fenced lease contracts. The public `BlueTusk.Streams.Testing` conformance kit exercises compare-and-swap conflicts, backward movement, mapping incompatibility, exclusive group ownership, fencing-token progression, stale-owner rejection, independent groups, and lease expiry.

## Memory

`MemoryChangeStreamStateStore` is for tests and ephemeral development. Its state disappears with the process and it must not be used to protect a production replication slot.

## File

`BlueTusk.Streams.Storage.File` is the single-node self-hosting backend. Give it a directory on a local durable filesystem:

```csharp
var store = new FileChangeStreamStateStore(
    new FileChangeStreamStateStoreOptions
    {
        DirectoryPath = "/var/lib/bluetusk/streams-state",
        LockTimeout = TimeSpan.FromSeconds(30),
    });
```

Each consumer group is represented by a SHA-256-derived filename so source and group names do not leak through directory listings. Checkpoint, lease, and last-issued fencing token are written together. Writes use a unique temporary file, write-through flush, and atomic replacement. A versioned header, bounded payload length, and SHA-256 checksum make torn, truncated, or modified state fail closed.

The file backend coordinates processes on one host with an exclusive per-group lock file. Do not place it on a network filesystem whose locking or atomic-replace semantics differ from the host filesystem. Restrict directory permissions to the BlueTusk worker identity and use encrypted storage when checkpoint metadata requires encryption at rest. Checksums provide integrity detection, not confidentiality.

Back up the complete directory. Temporary `*.tmp` files are incomplete writes and are never read as state; `*.state` files and persistent `*.lock` filenames contain the recoverable data and coordination namespace.

## PostgreSQL

`BlueTusk.Streams.Storage.PostgreSql` is the production default. Its options require an explicit control `DbDataSource`; the application/source replication data source is never inferred. Provision the versioned control schema before workers start:

```csharp
var options = new PostgreSqlStreamsStorageOptions
{
    ControlDataSource = controlDataSource,
    ControlSchema = "bluetusk_streams",
};
var store = new PostgreSqlChangeStreamStateStore(options);
await store.InitializeAsync();
```

Checkpoint compare-and-swap uses a PostgreSQL transaction and a locked group row. Lease acquisition, renewal, expiry, release, and fencing-token generation use the database clock, avoiding worker clock skew. Checkpoint positions are stored losslessly as 20-digit numerics, and checkpoint identity is stored as separate validated fields rather than an opaque application blob.

The relay and state schema must be excluded from the source publication. Call `PostgreSqlRelayPublicationValidator.Validate` with the discovered publication tables during provisioning; configuration fails if any table belongs to the configured control schema. Prefer a separately credentialed control data source and database so an accidental `FOR ALL TABLES` source publication cannot feed relay writes back into itself.

The live conformance gate runs this backend on PostgreSQL 15–19.

## Custom stores

Run the conformance suite against a real instance before using a custom implementation:

```csharp
var report = await ChangeStreamStateStoreConformance.RunAsync(
    customStore,
    "custom-store");
```

A passing result establishes the shared behavioral contract, not the backend's durability, disaster-recovery, latency, or security properties. Those remain backend-specific release gates.

## Redis

`BlueTusk.Streams.Storage.Redis` is the distributed alternative for checkpoints and leases. It accepts an application-owned `IConnectionMultiplexer`; BlueTusk does not create or dispose the shared Redis connection.

```csharp
var store = new RedisChangeStreamStateStore(
    new RedisChangeStreamStateStoreOptions
    {
        Connection = redisConnection,
        KeyPrefix = "bluetusk:streams",
    });
```

Each consumer group is one Redis hash under a SHA-256-derived cluster hash tag. Lua scripts execute lease acquisition, renewal, release, monotonic compare-and-swap, compatibility checks, and fencing atomically on the Redis server clock. Commit LSNs use fixed-width unsigned decimal strings so comparison does not lose precision through Lua's numeric representation. Generations and fencing tokens remain signed 64-bit values managed by .NET and Redis integer operations.

Configure Redis persistence, replication, authentication, TLS, eviction policy, backup, and failover to match the durability required for replication checkpoints. The package provides atomic state semantics; it cannot turn an ephemeral or evicting Redis deployment into a durable checkpoint service. PostgreSQL remains the default durable control store and the only relay backend in the first preview.

The checked-in integration gate runs the public store conformance suite against Redis 8.
