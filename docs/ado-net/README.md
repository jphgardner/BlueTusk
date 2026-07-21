# ADO.NET

Version 0.0.3 provides an initial asynchronous `BlueTuskConnection`, `BlueTuskCommand`, buffered `BlueTuskDataReader`, provider factory, and unpooled `BlueTuskDataSource`.

Commands without parameters use PostgreSQL's simple-query protocol. Commands with positional `$1`, `$2`, and subsequent placeholders use Parse, Bind, Describe, Execute, and Sync. Parameter values are encoded separately as typed text or binary payloads and are never interpolated into SQL.

```csharp
await using var dataSource = BlueTuskDataSource.Create(connectionString);
await using var command = dataSource.CreateCommand("SELECT $1::int4 + $2::int4");
command.Parameters.Add(new BlueTuskParameter<int>(20));
command.Parameters.Add(new BlueTuskParameter<int>(22));

var answer = await command.ExecuteScalarAsync<int>();
```

BlueTusk infers built-in PostgreSQL type OIDs from `DbType` or the CLR value. A null parameter must set `DbType` or `PostgreSqlTypeOid`; this avoids relying on ambiguous server inference. Transactions, preparation, pooling, and cancellation remain future milestones.
