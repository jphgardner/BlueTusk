# ADO.NET

The 0.1.0 development line provides native synchronous and asynchronous `BlueTuskConnection`, `BlueTuskCommand`, `BlueTuskTransaction`, `BlueTuskBatch`, buffered and sequential `BlueTuskDataReader`, provider factory, and pooled `BlueTuskDataSource` paths. Synchronous operations use blocking socket, TLS, protocol, authentication, pool, and query implementations rather than blocking asynchronous I/O.

Commands without parameters use PostgreSQL's simple-query protocol and receive text fields. Commands with positional `$1`, `$2`, and subsequent placeholders use Parse, Bind, Describe, Execute, and Sync and prefer binary fields. Named `@name` and `:name` placeholders are rewritten to positional placeholders by a PostgreSQL-aware lexer that skips quoted strings, quoted identifiers, dollar-quoted bodies, and comments. If PostgreSQL reports that a selected type has no binary output function, an autocommit command retries once with text fields. Commands inside explicit transactions request text fields up front so format negotiation cannot abort the transaction. Parameter values are encoded separately as typed text or binary payloads and are never interpolated into SQL. The [type mapping reference](../types/README.md) lists the formats, CLR types, and edge-case behavior implemented by the current provider.

```csharp
await using var dataSource = BlueTuskDataSource.Create(connectionString);
await using var command = dataSource.CreateCommand("SELECT $1::int4 + $2::int4");
command.Parameters.Add(new BlueTuskParameter<int>(20));
command.Parameters.Add(new BlueTuskParameter<int>(22));

var answer = await command.ExecuteScalarAsync<int>();
```

Set `ExecutionMode` to `Auto` (the default), `Simple`, or `Extended` to control protocol selection. Extended mode can be selected for parameterless commands; simple mode rejects parameters and prepared commands rather than interpolating values.

Automatic preparation is opt-in per physical connection. `Max Auto Prepare` bounds the server statements and `Auto Prepare Min Usages` controls promotion (defaults: disabled and five uses). The cache keys statements by rewritten SQL and PostgreSQL parameter OIDs, evicts the least-recently-used statement, and invalidates itself after `DISCARD ALL` or `DEALLOCATE ALL`.

```text
Max Auto Prepare=100;Auto Prepare Min Usages=5
```

BlueTusk infers built-in PostgreSQL type OIDs from `DbType` or the CLR value. A null parameter must set `DbType` or `PostgreSqlTypeOid`; this avoids relying on ambiguous server inference.

Explicit preparation is available synchronously and asynchronously on an open, connection-owned command. BlueTusk creates a named server statement and reuses it across executions; changing the command text or parameter type identity closes and prepares the statement again.

```csharp
await using var connection = await dataSource.OpenConnectionAsync();
await using var command = new BlueTuskCommand("SELECT $1::int4 + $2::int4", connection);
command.Parameters.Add(new BlueTuskParameter<int>(20));
command.Parameters.Add(new BlueTuskParameter<int>(22));

await command.PrepareAsync();
var answer = await command.ExecuteScalarAsync<int>();
```

The equivalent synchronous path includes data-source ownership, pool warm-up and checkout, type discovery, preparation, transactions, readers, batches, timeouts, and PostgreSQL cancellation:

```csharp
using var dataSource = BlueTuskDataSource.Create(connectionString);
dataSource.WarmUp();
using var connection = dataSource.OpenConnection();
using var command = new BlueTuskCommand("SELECT @value::int4 + 1", connection);
command.Parameters.Add(new BlueTuskParameter<int>(41) { ParameterName = "value" });

command.Prepare();
var answer = (int)command.ExecuteScalar()!;
```

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

[Multi-host connections](multi-host.md) support ordered or randomized attempts, shared or per-host ports, and primary/standby/read-write/read-only target selection.

Connection-owned [COPY APIs](copy.md) stream raw text, CSV, or binary payloads to and from PostgreSQL while preserving exclusive use of the physical session.

Connection-owned [`LISTEN`/`NOTIFY` APIs](notifications.md) deliver PostgreSQL notifications through a bounded asynchronous stream while leaving the primary connection session available for commands.

Transactional [large-object streams](large-objects.md) support asynchronous creation, deletion, reads, writes, 64-bit seeks, and truncation.

[`BlueTuskBatch`](batches.md) implements `DbBatch`/`DbBatchCommand` with parameters, ordered multiple results, preparation, transactions, timeouts, cancellation, and data-source-owned execution.

[Sequential readers](sequential-readers.md) use bounded named portals and incremental backend-frame reads. Their `GetStream` and `GetTextReader` paths consume binary `bytea`, text, JSON, and JSONB directly from the active network payload. Buffered readers retain the existing random-access behavior. Raw/text/typed-binary COPY, notification subscription and waiting, and large-object streams have separate native synchronous and asynchronous paths.
