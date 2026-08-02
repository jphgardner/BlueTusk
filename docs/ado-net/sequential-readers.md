# Sequential readers

Pass `CommandBehavior.SequentialAccess` to keep the result on the PostgreSQL connection instead of buffering every row. BlueTusk binds a generated named portal and streams PostgreSQL's complete `Execute` response by default (`BlueTuskCommand.SequentialFetchSize = 0`). This avoids a network round trip for every portal suspension while fields still flow incrementally from the transport instead of being materialized as a complete result set. Set a positive fetch size to opt into bounded server-side portal executions; BlueTusk resumes the portal after each `PortalSuspended` response.

```csharp
await using var command = new BlueTuskCommand(
    "SELECT id, payload, document FROM app.assets ORDER BY id",
    connection)
{
    SequentialFetchSize = 16,
};

await using var reader = await command.ExecuteReaderAsync(
    CommandBehavior.SequentialAccess,
    cancellationToken);

while (await reader.ReadAsync(cancellationToken))
{
    var id = reader.GetInt64(0);
    await using var payload = reader.GetStream(1);
    using var document = reader.GetTextReader(2);

    // Consume or dispose each field before moving to the next ordinal or row.
}
```

Fields are forward-only. Accessing an earlier ordinal, moving a field offset backwards, or opening another field while a field stream is active throws `InvalidOperationException`. Scalar getters materialize only their field. `GetStream` reads binary `bytea` from the active backend frame; `GetTextReader` incrementally validates UTF-8 for text, JSON, and JSONB. A text-format `bytea` value, such as one returned inside a transaction, retains the codec-backed materialization path so hexadecimal and legacy escape formats remain correct.

The portal owns the physical session until it completes or the reader is disposed. The default unlimited request includes `Sync`, so normal completion consumes the already queued `ReadyForQuery` without another client/server exchange. Large fields use adaptive bounded read-ahead, and one pending socket-read continuation advances protocol, row, and stream positions. Early disposal drains the response without allocating large payloads. A positive bounded fetch size instead closes a suspended portal and sends `Sync`. `CommandTimeout`, `Cancel`, `CancelAsync`, and read cancellation tokens send PostgreSQL `CancelRequest` and perform the same recovery, allowing the connection to be reused. `CommandBehavior.CloseConnection` and commands created from `BlueTuskDataSource` release their logical connection only after portal recovery.

Buffered readers remain the default and support random field access and multiple result sets. A sequential extended-query portal represents one PostgreSQL statement/result at a time; `NextResult` closes the active portal and returns `false`.
