# PostgreSQL pipeline mode

`BlueTusk.Client` supports PostgreSQL pipeline mode on PostgreSQL 14 and later. A pipeline sends multiple extended-query synchronization groups in one network flush, preserves result order, and avoids waiting for each group before sending the next one.

This feature is independent of the .NET `System.IO.Pipelines` buffering library. BlueTusk implements PostgreSQL pipeline semantics over its existing synchronous and asynchronous transport. A separate benchmark-backed evaluation retained the ArrayPool/Span/Memory transport; see [ADR 0005](architecture/decisions/0005-postgresql-pipeline-mode-and-transport-pipelines.md).

Each `BlueTuskPipelineGroup` ends with an explicit protocol `Sync`. A server error is attached to the affected group's result after BlueTusk drains through that group's `ReadyForQuery`. PostgreSQL can then execute the next already-sent group:

```csharp
await using var session = await BlueTuskSession.OpenAsync(options);

var pipeline = await session.ExecutePipelineAsync(
[
    new BlueTuskPipelineGroup(
    [
        new BlueTuskBatchQuery("INSERT INTO app.items(value) VALUES (1)", []),
        new BlueTuskBatchQuery("INSERT INTO app.items(value) VALUES (2)", []),
    ]),
    new BlueTuskPipelineGroup(
    [
        new BlueTuskBatchQuery("SELECT count(*) FROM app.items", []),
    ]),
]);

foreach (var group in pipeline.Groups)
{
    if (group.Error is { } error)
    {
        Console.Error.WriteLine($"{error.SqlState}: {error.Message}");
    }
}
```

The synchronous `ExecutePipeline` method has the same ordered result and error contract. Query parameters use the same `BlueTuskExtendedQueryParameter` representation as other Client-layer extended-query APIs.

Cancellation sends PostgreSQL `CancelRequest`, drains the active group and every already-sent group to their synchronization boundaries, and throws `OperationCanceledException` only after the session is safe to reuse. A connection that fails during a partial write or cannot recover to `ReadyForQuery` is closed instead of being reused.

`BlueTuskSession.Capabilities` and `BlueTuskConnection.ServerCapabilities` expose the authenticated server version and detected features. ADO.NET physical sessions probe PostgreSQL 19's documented `information_schema.property_graphs` view before enabling `SupportsSqlPgq`; low-level normal SQL sessions can call `ProbeOptionalCapabilities` or its asynchronous counterpart explicitly. `SupportsOAuthBearer` becomes true only on a connection that actually completed native OAUTHBEARER negotiation. A version number alone is not treated as product support.
