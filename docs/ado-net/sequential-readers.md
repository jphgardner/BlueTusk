# Sequential readers

This page helps you read large values, such as files in `bytea` columns or big
JSON documents, without loading whole rows into memory.

By default a data reader buffers each result before you read it. Pass
`CommandBehavior.SequentialAccess` to stream the result from the connection
instead. You can then read a field as a `Stream` (`GetStream`) or a
`TextReader` (`GetTextReader`) while its bytes are still arriving.

By default the whole result is streamed in one request
(`SequentialFetchSize = 0`). Set `SequentialFetchSize` to a positive number to
fetch that many rows at a time; this makes the reader available sooner for a
long-running query and lets you cancel between fetches.

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

    // Read each field in column order and finish it before opening the next.
    await using (var payload = reader.GetStream(1))
    {
        await payload.CopyToAsync(destination, cancellationToken);
    }

    using (var document = reader.GetTextReader(2))
    {
        var json = await document.ReadToEndAsync(cancellationToken);
    }
}
```

Here `destination` is any writable `Stream`, such as a file.

## Rules for sequential access

- Read fields in column order. Going back to an earlier column, or opening a
  field while another field's stream is still open, throws
  `InvalidOperationException`
  (`The active field stream must be consumed or disposed before accessing another field.`).
- `GetStream` reads `bytea`. `GetTextReader` reads `text`, `json` and `jsonb`
  and checks the UTF-8 as it goes.
- `Stream.ReadAsync` can return fewer bytes than you asked for. Keep reading
  until it returns zero, or use `CopyToAsync`.
- Scalar getters such as `GetInt64` read only their own field.
- The reader holds its connection until it finishes or is disposed. Dispose it
  promptly; disposing early skips the remaining rows without loading them.
- `CommandTimeout`, `Cancel()`, `CancelAsync()` and cancellation tokens cancel
  the query on the server and leave the connection usable.
- With the default fetch size, `ExecuteReaderAsync` waits until PostgreSQL
  starts sending results. Pass a cancellation token or set `CommandTimeout` if
  that wait must be limited.
- A sequential reader returns one result set. `NextResult` returns `false`.
  Do not use `SequentialAccess` with a multi-statement command.
- Inside an explicit transaction, `bytea` values arrive in text format and are
  decoded in memory rather than streamed.

Buffered readers (the default) allow random field access and multiple result
sets.
