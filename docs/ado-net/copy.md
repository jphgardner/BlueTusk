# COPY

`BlueTuskConnection.CopyFrom`/`CopyTo` and `CopyFromAsync`/`CopyToAsync` stream raw PostgreSQL COPY payloads without buffering the complete transfer. The SQL command selects text, CSV, or binary format, so the same APIs can preserve any PostgreSQL-supported COPY representation.

```csharp
await using var source = File.OpenRead("people.csv");
var imported = await connection.CopyFromAsync(
    """
    COPY app.people (id, name)
    FROM STDIN WITH (FORMAT CSV, HEADER true)
    """,
    source);

Console.WriteLine($"Imported {imported.RowsAffected} rows");
```

```csharp
await using var destination = File.Create("people.copy");
var exported = await connection.CopyToAsync(
    """
    COPY (
        SELECT id, name
        FROM app.people
        ORDER BY id
    ) TO STDOUT WITH (FORMAT BINARY)
    """,
    destination);
```

The result reports PostgreSQL's overall and per-column COPY formats, rows affected, and payload bytes transferred. BlueTusk does not dispose the caller-owned stream.

For text and CSV data, the synchronous `CopyTextFrom`/`CopyTextTo` and asynchronous `CopyTextFromAsync`/`CopyTextToAsync` APIs accept caller-owned `TextReader` and `TextWriter` instances. They transcode strict UTF-8 incrementally, including Unicode values split across COPY chunks:

```csharp
using var source = new StringReader("1,\"Chloé 🐘\"\n");
await connection.CopyTextFromAsync(
    "COPY app.people (id, name) FROM STDIN WITH (FORMAT CSV)",
    source);
```

Only the supplied SQL determines COPY options such as delimiter, quote, escape, null representation, encoding, and header handling. Values are not interpolated by these raw APIs; construct commands from trusted SQL and use PostgreSQL identifier quoting for dynamic object names.

The physical session remains exclusively leased for the full transfer. If the source, destination, or cancellation token fails, BlueTusk sends `CopyFail` or a cancellation request as appropriate and drains through `ReadyForQuery` before allowing the connection to be reused. COPY OUT cleanup also synchronizes once after cancellation so a late PostgreSQL cancel signal cannot affect the caller's next command.

## Typed binary COPY

`BeginBinaryImportAsync` writes PostgreSQL's binary COPY header, rows, field lengths, null markers, and trailer while using the data source's catalogue-loaded binary codecs:

```csharp
await using var importer = await connection.BeginBinaryImportAsync(
    "COPY app.people (id, name) FROM STDIN WITH (FORMAT BINARY)");

await importer.StartRowAsync();
await importer.WriteAsync(42);
await importer.WriteAsync("Chloé 🐘");

var rows = await importer.CompleteAsync();
```

`StartRowAsync` uses the server-reported column count and requires every field to be written before another row or completion. `WriteAsync<T>` infers the PostgreSQL type from the same registry used for parameters; an overload accepts an explicit PostgreSQL type OID when a CLR type is ambiguous. Null fields are written with PostgreSQL's `-1` length marker.

Binary export validates the signature, flags, extension length, row shape, field lengths, trailer, and final server row count:

```csharp
await using var exporter = await connection.BeginBinaryExportAsync(
    """
    COPY (
        SELECT id, name
        FROM app.people
        ORDER BY id
    ) TO STDOUT WITH (FORMAT BINARY)
    """);

while (await exporter.StartRowAsync() != -1)
{
    var id = await exporter.ReadAsync<int>();
    var name = await exporter.ReadAsync<string>();
    Console.WriteLine($"{id}: {name}");
}
```

Arrays and other catalogue-composed values use their existing binary codecs. `ReadAsync<T>` also has an explicit-OID overload. Reading a PostgreSQL null into a non-nullable value type fails instead of silently substituting its CLR default.

Typed imports and exports stream through a stateful protocol operation; neither buffers the complete transfer. The asynchronous importer uses a pooled 64 KiB write buffer, flushing it before accepting more data when it fills. Large fields and general-purpose codecs can require additional field-sized storage: 64 KiB is not a maximum field size or a total-memory guarantee. Supplying a cancellation token uses the same direct import path, without an extra producer/consumer pipe or background transfer task.

COPY-mode initialization is ordered independently from transfer completion, including immediate empty exports under concurrent load. Always use `await using` with an asynchronous importer or exporter. Disposing an unfinished operation aborts and drains COPY before releasing its connection.

### Cancellation, errors, and transactions

Pass the token to startup, row writes, and completion. Cancellation is cooperative: BlueTusk finishes an in-progress protocol write or drains the server response rather than leaving a partial message for the next command. A token is not a guarantee of an immediate return, and cancellation after completion has begun does not guarantee that PostgreSQL rolled the operation back.

If cancellation wins while an import is waiting to start, BlueTusk sends a PostgreSQL cancellation request, drains the response, and synchronizes before reusing the connection. If cleanup cannot establish a safe state, the physical connection is discarded. If the import was inside an explicit transaction, roll that transaction back before issuing more commands; cancellation or a constraint error can leave it aborted.

Startup and completion errors reported by PostgreSQL are exposed as `BlueTuskException`, including their `SqlState`. A fully drained server error does not by itself make the physical connection unusable. For example, a failed check constraint reports `23514`; the transaction may still need a rollback.

`CompleteAsync` finishes COPY and checks PostgreSQL's row count. It does not commit an enclosing transaction: call that transaction's `CommitAsync` separately. Do not assume a successful return from a rollback-based benchmark measures durable commit performance. For request-level timing and allocation measurement, see the [Provider capture guide](../operations/provider-request-capture.md).

The asynchronous import path retains the `bluetusk.copy.bytes` counter and records `bluetusk.commands.duration` from startup through completion or cleanup. Buffered bytes that were never sent are not counted as transferred bytes.

Synchronous typed binary COPY is also incremental and uses a stateful protocol operation rather than buffering the transfer:

```csharp
using var importer = connection.BeginBinaryImport(
    "COPY app.people (id, name) FROM STDIN BINARY");
importer.StartRow();
importer.Write(42);
importer.Write("Chloé 🐘");
var rows = importer.Complete();

using var exporter = connection.BeginBinaryExport(
    "COPY app.people (id, name) TO STDOUT BINARY");
while (exporter.StartRow() != -1)
{
    var id = exporter.Read<int>();
    var name = exporter.Read<string>();
}
```

Disposing a synchronous importer before `Complete` sends `CopyFail`; disposing an exporter before its trailer sends a cancellation request. Both paths drain through `ReadyForQuery` before releasing the connection.
