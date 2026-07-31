# PostgreSQL pipeline mode

`BlueTusk.Client` supports PostgreSQL pipeline mode on PostgreSQL 14 and later. A pipeline sends multiple extended-query synchronization groups in one network flush, preserves result order, and avoids waiting for each group before sending the next one.

This feature is independent of the .NET `System.IO.Pipelines` buffering library. BlueTusk currently implements PostgreSQL pipeline semantics over its existing synchronous and asynchronous transport; any transport rewrite remains subject to separate benchmarks.

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

`BlueTuskSession.Capabilities` and `BlueTuskConnection.ServerCapabilities` expose the authenticated server version and version-gated features. `SupportsSqlPgq` and `SupportsOAuthBearer` remain false until BlueTusk implements explicit negotiation or catalogue probes; a PostgreSQL version number alone is not treated as product support.
