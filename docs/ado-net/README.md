# ADO.NET

Version 0.0.5 provides an asynchronous `BlueTuskConnection`, `BlueTuskCommand`, `BlueTuskTransaction`, buffered `BlueTuskDataReader`, provider factory, and pooled `BlueTuskDataSource`.

Commands without parameters use PostgreSQL's simple-query protocol. Commands with positional `$1`, `$2`, and subsequent placeholders use Parse, Bind, Describe, Execute, and Sync. Parameter values are encoded separately as typed text or binary payloads and are never interpolated into SQL.

```csharp
await using var dataSource = BlueTuskDataSource.Create(connectionString);
await using var command = dataSource.CreateCommand("SELECT $1::int4 + $2::int4");
command.Parameters.Add(new BlueTuskParameter<int>(20));
command.Parameters.Add(new BlueTuskParameter<int>(22));

var answer = await command.ExecuteScalarAsync<int>();
```

BlueTusk infers built-in PostgreSQL type OIDs from `DbType` or the CLR value. A null parameter must set `DbType` or `PostgreSqlTypeOid`; this avoids relying on ambiguous server inference.

Transactions use PostgreSQL transaction blocks and require explicit command enlistment:

```csharp
await using var connection = await dataSource.OpenConnectionAsync();
await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable);
await using var command = new BlueTuskCommand("UPDATE app.accounts SET balance = balance - $1 WHERE id = $2", connection)
{
    Transaction = transaction,
};
command.Parameters.Add(new BlueTuskParameter<decimal>(10m));
command.Parameters.Add(new BlueTuskParameter<int>(42));

await command.ExecuteNonQueryAsync();
await transaction.CommitAsync();
```

Cancellation tokens and `CommandTimeout` send PostgreSQL `CancelRequest` on a separate connection. BlueTusk drains the original connection through `ReadyForQuery` before returning, so a cancelled connection remains reusable. `Cancel()` and `CancelAsync()` provide explicit cancellation. Cancellation inside a transaction leaves PostgreSQL's transaction in the failed state and requires rollback.

`BlueTuskDataSource` owns a bounded physical connection pool by default. Logical connections return their physical session when closed or disposed; reuse rolls back an unfinished transaction when necessary and issues `DISCARD ALL` before handing the session to another caller. See [Connection pooling](pooling.md) for sizing, lifetime, warm-up, statistics, and drain controls.

Preparation, batches, streaming readers, and synchronous query execution remain future milestones.
