# Start PostgreSQL replication

Use `BlueTusk.Replication` when you need direct access to PostgreSQL's logical
or physical replication protocol. If your goal is an application change feed,
start with [Streams](../streams/README.md); it adds transaction assembly,
spooling, checkpoints, leases, and safe acknowledgement.

## Choose the right layer

| Need                                      | Start with                      |
| ----------------------------------------- | ------------------------------- |
| Decode raw `pgoutput` messages            | This replication guide          |
| Receive complete committed transactions   | [Streams](../streams/README.md) |
| Copy those transactions to another system | [Sync](../sync/README.md)       |
| Keep browser clients updated              | [Live](../live/README.md)       |

Replication sessions are dedicated. They do not come from the normal ADO.NET
pool.

## 1. Configure PostgreSQL

Set `wal_level = logical`, then create a publication and a replication role
with only the required rights:

```sql
CREATE PUBLICATION app_changes FOR TABLE app.orders;
```

Create the logical slot through controlled provisioning or the application
bootstrap process. Give every consumer its own slot; two independent consumers
must not race over one checkpoint. Do not create a slot while migrations or
other table creation run in the same database: PostgreSQL can create a slot that
then fails on every attempt to decode writes to the new tables. See
[creating a slot while the schema changes](../streams/troubleshooting.md#a-new-slot-fails-with-could-not-map-filenumber)
for the symptoms and the recovery.

## 2. Open a dedicated session

```csharp
using BlueTusk.Data;
using BlueTusk.Replication;
using BlueTusk.Replication.PgOutput;

await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();
await using var replication = await BlueTuskLogicalReplicationConnection.OpenAsync(
    dataSource.CreateDedicatedSessionOptions(),
    cancellationToken);

var server = await replication.IdentifySystemAsync(cancellationToken);
Console.WriteLine($"System={server.SystemIdentifier} WAL={server.WalPosition}");
```

## 3. Read `pgoutput`

```csharp
var stream = replication.StartReplicationAsync(
    slotName: "orders_app",
    publicationName: "app_changes",
    cancellationToken: cancellationToken);

await foreach (var envelope in stream.DecodePgOutputAsync(
    cancellationToken: cancellationToken))
{
    await HandleMessageAsync(envelope.Message, cancellationToken);

    if (envelope.TryGetTransactionEndPosition(out var applied))
    {
        // Send feedback only after the complete transaction and its checkpoint
        // are durable in your system.
        await replication.SendStandbyStatusUpdateAsync(
            new BlueTuskStandbyStatus(applied, applied, applied),
            cancellationToken);
    }
}
```

Never acknowledge a payload merely because it was received. The safe point is
the transaction-end LSN after the downstream effect and checkpoint are durable.

## Run the complete example

```powershell
$env:BLUETUSK_CONNECTION_STRING = "Host=localhost;Database=app;Username=replicator;Password=local-only;SSL Mode=Disable;Channel Binding=Disable"
dotnet run --project samples/BlueTusk.Samples.Replication -- orders_app app_changes
```

Use TLS outside an isolated local environment.

## Operate it safely

- Alert on retained WAL and slot inactivity.
- Persist the source system identifier, database, slot, publication fingerprint,
  and last acknowledged transaction-end LSN together.
- Stop on a source-identity mismatch instead of silently continuing.
- Bound message size and reconnect delay.
- Drop unused slots deliberately; an abandoned slot can retain WAL indefinitely.

The [replication reference](reference.md) covers slot discovery, physical
replication, feedback timing, restart LSNs, custom output plug-ins, reconnect,
and resume behavior.
