# COPY

`BlueTuskConnection.CopyFromAsync` and `CopyToAsync` stream raw PostgreSQL COPY payloads without buffering the complete transfer. The SQL command selects text, CSV, or binary format, so the same APIs can preserve any PostgreSQL-supported COPY representation.

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

For text and CSV data, `CopyTextFromAsync` and `CopyTextToAsync` accept caller-owned `TextReader` and `TextWriter` instances. They transcode strict UTF-8 incrementally, including Unicode values split across COPY chunks:

```csharp
using var source = new StringReader("1,\"Chloé 🐘\"\n");
await connection.CopyTextFromAsync(
    "COPY app.people (id, name) FROM STDIN WITH (FORMAT CSV)",
    source);
```

Only the supplied SQL determines COPY options such as delimiter, quote, escape, null representation, encoding, and header handling. Values are not interpolated by these raw APIs; construct commands from trusted SQL and use PostgreSQL identifier quoting for dynamic object names.

The physical session remains exclusively leased for the full transfer. If the source, destination, or cancellation token fails, BlueTusk sends `CopyFail` or a cancellation request as appropriate and drains through `ReadyForQuery` before allowing the connection to be reused.

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

Both typed operations use a bounded producer/consumer pipe, so application code and the network apply backpressure to one another. Disposing before the trailer aborts and drains COPY, leaving the connection reusable.
