# Asynchronous notifications

`BlueTuskConnection` exposes PostgreSQL `LISTEN`/`NOTIFY` as an asynchronous stream:

```csharp
await using var connection = new BlueTuskConnection(connectionString);
await connection.OpenAsync();
await connection.ListenAsync("orders");

await foreach (var notification in connection.Notifications)
{
    Console.WriteLine(
        $"{notification.ProcessId}: {notification.Channel} — {notification.Payload}");
}
```

The synchronous path uses a dedicated blocking listener session and a long-running worker, while normal commands continue on the connection's primary session:

```csharp
connection.Listen("orders");
var notification = connection.WaitForNotification();
Console.WriteLine(notification.Payload);
connection.Unlisten("orders");
```

`UnlistenAll()` synchronously stops every listener. Synchronous listener disposal closes the blocking socket to interrupt its receive loop; it does not block on asynchronous network I/O.

`BlueTuskNotification` contains the publishing backend process ID, the channel reported by PostgreSQL, and the payload. PostgreSQL delivers a notification only after the publishing transaction commits; notifications produced by an aborted transaction are discarded.

Channel names are PostgreSQL identifiers. `ListenAsync` quotes them through BlueTusk's central identifier-quoting path, so mixed case, whitespace, Unicode, reserved words, and embedded double quotes are safe. Empty names and names containing a null character are rejected. Payload values should be sent as parameters:

```csharp
await using var notify = new BlueTuskCommand(
    "SELECT pg_notify($1, $2)",
    connection);
notify.Parameters.Add(new BlueTuskParameter<string>("orders"));
notify.Parameters.Add(new BlueTuskParameter<string>("order-42-created"));
await notify.ExecuteNonQueryAsync();
```

Calling `ListenAsync` repeatedly for the same channel is idempotent. Use `UnlistenAsync(channel)` for one channel or `UnlistenAllAsync()` for all channels. Closing or disposing the connection stops every listener and completes the current `Notifications` enumeration. Reopening the same connection creates a fresh notification stream.

BlueTusk uses a dedicated, non-pooled physical session for each active channel. This keeps the logical connection's normal session available for commands while the notification consumer is waiting and prevents session-level `LISTEN` state from leaking through the connection pool. Listener sessions do not count toward the data source's configured pool limit or pool statistics, so applications with many channels should include them in server connection-capacity planning.

The stream has a bounded 1,024-notification buffer and applies backpressure rather than dropping messages. Consume a connection's `Notifications` stream from one enumeration. A listener transport or protocol failure faults that enumeration; close the connection and reopen it to establish a new notification lifetime.
