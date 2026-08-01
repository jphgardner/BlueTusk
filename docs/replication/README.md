# Replication

`BlueTusk.Replication` exposes PostgreSQL's physical and logical streaming
replication protocol directly. It uses a dedicated replication session and
`COPY BOTH`; it does not borrow an ADO.NET pooled connection.

Build one long-lived data source for the application configuration, then derive
a fresh dedicated-session option snapshot for replication:

```csharp
await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();
await using var replication = await BlueTuskLogicalReplicationConnection.OpenAsync(
    dataSource.CreateDedicatedSessionOptions(),
    cancellationToken);
```

The data source remains the configuration root, but it does not own the
replication connection. The replication object owns one unpooled physical
session and must be disposed independently. Its lifetime may be much longer
than an ADO.NET command or pooled checkout. Configured credentials, application
name, timeout, TLS mode, and channel binding are copied into the snapshot;
ADO.NET codecs and runtime catalogue state are intentionally irrelevant to raw
replication payloads.

The stream is pull-based and does not maintain a background prefetch queue.
At most the current `CopyData` payload and decoded message are owned by the
consumer path; PostgreSQL/socket flow control supplies backpressure until the
consumer requests the next element. Process or hand off each payload promptly,
and put an application-owned bounded queue in front of slower durable work only
when that queue's capacity and failure semantics are deliberate.

For a multi-host data source, select a configured endpoint explicitly:

```csharp
var endpoint = new BlueTuskHostEndpoint("primary.example.test", 5432);
var options = dataSource.CreateDedicatedSessionOptions(endpoint);
```

BlueTusk does not silently fail a replication stream over to another host.
The application must establish that the replacement server and slot are safe
for its persisted resume position.

BlueTusk supports PostgreSQL 15 through 18 and provides:

- physical and logical replication connections;
- system identification, settings, replication-slot discovery, and slot
  lifecycle commands;
- publication and publication-table discovery;
- WAL data, primary keepalives, standby status updates, and hot-standby
  feedback;
- transaction streaming and two-phase startup options;
- raw payloads for any logical decoding output plugin; and
- complete `pgoutput` decoding in `BlueTusk.Replication.PgOutput`.

## Server setup

The server must admit a role with the `REPLICATION` attribute in `pg_hba.conf`.
Logical replication also requires `wal_level = logical`; replication capacity
is controlled by `max_wal_senders` and `max_replication_slots`.

Use a dedicated least-privilege role in production. Keep credentials out of
logs and source control. BlueTusk verifies the server certificate and hostname
by default; local environments without TLS must explicitly select
`SSL Mode=Disable`.

## Logical replication

Create the publication and slot once through deployment tooling or through the
connection:

```csharp
await using var replication =
    await BlueTuskLogicalReplicationConnection.OpenAsync(
        dataSource.CreateDedicatedSessionOptions());

var slot = await replication.CreateReplicationSlotAsync(
    slotName: "app_slot",
    outputPlugin: "pgoutput");
```

The convenience overload exactly configures pgoutput protocol version 1 for
one publication:

```csharp
await foreach (var message in replication.StartReplicationAsync(
    slotName: "app_slot",
    publicationName: "app_publication",
    cancellationToken))
{
    if (message is BlueTuskXLogData data)
    {
        // data.Data contains one output-plugin message.
    }
}
```

Use typed startup options for binary tuples, logical messages, large
in-progress transactions, multiple publications, two-phase transactions, or
origin filtering:

```csharp
var stream = replication.StartReplicationAsync(
    new BlueTuskPgOutputReplicationOptions
    {
        SlotName = "app_slot",
        PublicationNames = ["app_publication", "audit_publication"],
        ProtocolVersion = 3,
        StreamingMode = BlueTuskLogicalStreamingMode.On,
        TwoPhase = true,
        Messages = true,
    },
    cancellationToken);
```

The selected protocol features must be supported by the server. Protocol
version 2 adds streamed transactions, version 3 adds two-phase messages, and
version 4 adds parallel-stream abort metadata.

## pgoutput decoding

Reference `BlueTusk.Replication.PgOutput` and apply the decoder extension:

```csharp
var decoderOptions = new BlueTuskPgOutputDecoderOptions
{
    ProtocolVersion = 3,
    StreamingMode = BlueTuskPgOutputStreamingMode.On,
    TwoPhase = true,
};

await foreach (var envelope in stream.DecodePgOutputAsync(
    decoderOptions,
    cancellationToken))
{
    switch (envelope.Message)
    {
        case BlueTuskPgOutputRelation relation:
            // Cache relation.Columns by relation.RelationId.
            break;
        case BlueTuskPgOutputInsert insert:
            // Tuple values retain null, unchanged-TOAST, text, or binary form.
            break;
        case BlueTuskPgOutputStreamStart streamStart:
            // A segment of a large in-progress transaction has started.
            break;
        case BlueTuskPgOutputPrepare prepare:
            // A two-phase transaction reached PREPARE TRANSACTION.
            break;
    }
}
```

`BlueTuskPgOutputEnvelope` retains the enclosing `BlueTuskXLogData`, including
its WAL start, end, server end, and server clock. The decoder validates message
lengths, flags, tuple markers, protocol-version capabilities, and stream
segment state.

## Feedback and durability

BlueTusk automatically answers primary keepalives that request an immediate
reply. Applications control acknowledged positions:

```csharp
var applied = envelope.XLogData.WalEnd;
await replication.SendStandbyStatusUpdateAsync(
    new BlueTuskStandbyStatus(
        Written: applied,
        Flushed: applied,
        Applied: applied),
    cancellationToken);
```

Advance `Flushed` or `Applied` only after the corresponding data is durable.
PostgreSQL can reclaim WAL based on slot progress; acknowledging data that can
still be lost breaks recovery guarantees. Physical standbys can also call
`SendHotStandbyFeedbackAsync` with their `xmin` horizons.

## Physical replication

Identify the system and begin at a retained WAL position:

```csharp
await using var replication =
    await BlueTuskPhysicalReplicationConnection.OpenAsync(
        dataSource.CreateDedicatedSessionOptions());
var identity = await replication.IdentifySystemAsync(cancellationToken);

await foreach (var message in replication.StartReplicationAsync(
    identity.WalPosition,
    cancellationToken: cancellationToken))
{
    switch (message)
    {
        case BlueTuskXLogData wal:
            await PersistWalAsync(wal.Data, cancellationToken);
            await replication.SendStandbyStatusUpdateAsync(
                new BlueTuskStandbyStatus(wal.WalEnd, wal.WalEnd, wal.WalEnd),
                cancellationToken);
            break;
        case BlueTuskPrimaryKeepalive keepalive:
            Console.WriteLine($"Primary WAL end: {keepalive.ServerWalEnd}");
            break;
    }
}
```

For a physical slot, prefer the retained restart position returned by
`ReadReplicationSlotAsync` over an older caller-cached position.

## Discovery and custom plugins

`GetReplicationSlotsAsync` lists slot activity and progress.
`GetPublicationsAsync` and `GetPublicationTablesAsync` expose publication
ownership, operations, columns, and row filters.

For another logical decoding plugin, create the slot with its plugin name and
pass arbitrary plugin options:

```csharp
var stream = replication.StartReplicationAsync(
    new BlueTuskLogicalReplicationRequest
    {
        SlotName = "audit_slot",
        PluginOptions = new Dictionary<string, string?>
        {
            ["include-xids"] = "true",
        },
    },
    cancellationToken);
```

Each `BlueTuskXLogData.Data` value is the plugin's raw payload. BlueTusk does
not interpret custom formats.

Cancellation or asynchronous enumerator disposal sends `CopyDone`, drains the
server back to `ReadyForQuery`, and releases the replication operation.

## Reconnect and resume

Persist the last durably applied WAL position outside the replication process.
On a transient disconnect, create a new dedicated replication connection from
the data source and request that persisted position. Do not resume from the
largest position merely received in memory.

```csharp
var resumePosition = await checkpoints.LoadAppliedPositionAsync(cancellationToken);

while (!cancellationToken.IsCancellationRequested)
{
    try
    {
        await using var replication =
            await BlueTuskLogicalReplicationConnection.OpenAsync(
                dataSource.CreateDedicatedSessionOptions(),
                cancellationToken);

        var request = new BlueTuskPgOutputReplicationOptions
        {
            SlotName = "app_slot",
            PublicationNames = ["app_publication"],
            StartPosition = resumePosition,
        };

        await foreach (var envelope in replication
            .StartReplicationAsync(request, cancellationToken)
            .DecodePgOutputAsync(cancellationToken: cancellationToken))
        {
            await ApplyAndCommitAsync(envelope.Message, cancellationToken);
            resumePosition = envelope.XLogData.WalEnd;
            await checkpoints.StoreAppliedPositionAsync(
                resumePosition,
                cancellationToken);
            await replication.SendStandbyStatusUpdateAsync(
                new BlueTuskStandbyStatus(
                    resumePosition,
                    resumePosition,
                    resumePosition),
                cancellationToken);
        }
    }
    catch (Exception exception) when (
        IsTransientReplicationFailure(exception) &&
        !cancellationToken.IsCancellationRequested)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
    }
}
```

The slot must still retain WAL at or before the checkpoint. If it was removed,
invalidated, or advanced beyond recoverable application state, stop and repair
from an application-specific snapshot rather than skipping data. Recreate the
decoder after reconnect so relation and streamed-transaction state cannot leak
across sessions.
