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

Typed binary import/export is tracked separately in the [roadmap](../roadmap.md).
