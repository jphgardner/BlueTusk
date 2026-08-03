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

## Custom stores

Run the conformance suite against a real instance before using a custom implementation:

```csharp
var report = await ChangeStreamStateStoreConformance.RunAsync(
    customStore,
    "custom-store");
```

A passing result establishes the shared behavioral contract, not the backend's durability, disaster-recovery, latency, or security properties. Those remain backend-specific release gates.

The PostgreSQL production store and Redis alternative are introduced in the next Phase 2 slice. PostgreSQL remains the default durable control store and relay backend.
