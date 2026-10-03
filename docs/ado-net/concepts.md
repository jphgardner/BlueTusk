# ADO.NET concepts

This page explains the model behind `BlueTusk.Data`: which object owns what,
how commands reach PostgreSQL, and what happens to a session between uses. Read
[Core concepts](../getting-started/concepts.md) first for the shared terms
(data source, pooling, parameters, type catalogue).

## Who owns what

```text
BlueTuskDataSource        one per configuration, lives as long as the app
 ├─ connection pool       physical PostgreSQL sessions (one pool per host)
 ├─ type catalogue        loaded from the server on first use
 │
 ├─ CreateCommand(sql)    borrows a session only while the command runs
 └─ OpenConnectionAsync() BlueTuskConnection: holds one session until disposed
        ├─ BlueTuskTransaction
        ├─ new BlueTuskCommand(sql, connection)
        └─ CreateBatch(), COPY, LISTEN, large objects
```

- **Data source.** Create one per connection string and keep it. Register it
  as a singleton. Disposing it closes the pool.
- **Connection.** A short-lived lease on one physical session. Open it late,
  dispose it early. Use one when several commands must run on the same session:
  a transaction, a temporary table, `SET`, explicit preparation, COPY or
  `LISTEN`.
- **Command.** Pick the form that matches the work:

| You need | Use |
| --- | --- |
| One independent statement | `dataSource.CreateCommand(sql)` |
| Several statements on one session, or a transaction | `new BlueTuskCommand(sql, connection)` |
| Several independent statements in one round trip | `connection.CreateBatch()` or `dataSource.CreateBatch()` ([Batches](batches.md)) |

`connection.CreateCommand()` is the standard ADO.NET method and is typed as
`DbCommand`, so BlueTusk-only members such as `ExecuteScalarAsync<T>()` are not
visible on it. Use one of the forms in the table instead.

`new BlueTuskConnection(connectionString)` creates a connection outside any
data source. It is not pooled and loads its own type catalogue. Prefer
`dataSource.OpenConnectionAsync()`.

## How a command is sent

PostgreSQL has two query protocols. BlueTusk chooses for you.

| Command | Protocol | Result format |
| --- | --- | --- |
| No parameters | Simple query | Text |
| Parameters, a prepared command, or `CommandBehavior.SequentialAccess` | Extended query (Parse, Bind, Execute) | Binary where possible |

Parameter values always travel separately from the SQL text. You can write
placeholders as `@name`, `:name` or `$1`, `$2`. Do not mix named and
positional placeholders in one command. BlueTusk rewrites named placeholders to
positional ones and skips quoted strings, dollar-quoted bodies and comments.

`BlueTuskCommand.ExecutionMode` overrides the choice: `Auto` (default),
`Simple` or `Extended`. `Simple` cannot be used with parameters or a prepared
command; BlueTusk throws instead of pasting values into SQL.

## Preparation

A prepared statement is parsed and planned once on one PostgreSQL session, then
reused. There are two ways to get one.

**Explicit.** Call `Prepare()` or `PrepareAsync()` on a command that belongs to
an open connection:

```csharp
await using var connection = await dataSource.OpenConnectionAsync();
await using var command = new BlueTuskCommand(
    "SELECT name FROM app.customers WHERE id = $1", connection);
var idParameter = new BlueTuskParameter<long>(0);
command.Parameters.Add(idParameter);

await command.PrepareAsync();
foreach (var id in customerIds)
{
    idParameter.TypedValue = id;
    Console.WriteLine(await command.ExecuteScalarAsync<string>());
}
```

Commands from `dataSource.CreateCommand(...)` cannot be prepared, because they
do not hold a session.

**Automatic.** Set `Max Auto Prepare` to a positive number. After a statement
has run `Auto Prepare Min Usages` times (default 5) on one physical session,
BlueTusk prepares it there. Automatic preparation is off by default.

When a session goes back to the pool, BlueTusk runs `DISCARD ALL`, which drops
every prepared statement on it. Prepared statements therefore last for one
lease. They help code that repeats a statement many times on one open
connection. They do not help code that runs one command per request.

## Transactions

```csharp
await using var connection = await dataSource.OpenConnectionAsync();
await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable);
await using var command = new BlueTuskCommand(
    "UPDATE app.accounts SET balance = balance - @amount WHERE id = @id", connection)
{
    Transaction = transaction,
};
command.Parameters.Add(new BlueTuskParameter<decimal>(10m) { ParameterName = "amount" });
command.Parameters.Add(new BlueTuskParameter<long>(42) { ParameterName = "id" });

await command.ExecuteNonQueryAsync();
await transaction.CommitAsync();
```

Rules:

- Set `Transaction` on every command that runs while the transaction is open.
  An unenlisted command throws `InvalidOperationException`.
- `await using` a transaction that was not committed rolls it back. A
  synchronous `Dispose()` of an uncommitted transaction closes the connection
  instead, and PostgreSQL rolls back.
- After any error inside a transaction, PostgreSQL rejects further commands
  with SQLSTATE `25P02` until you roll back.
- For savepoints, run `SAVEPOINT name`, `ROLLBACK TO SAVEPOINT name` and
  `RELEASE SAVEPOINT name` as enlisted commands. `DbTransaction.Save()` and the
  named `Rollback(...)` overloads throw `NotSupportedException`.
- Ambient `TransactionScope` and distributed transactions are not supported.
  See [Compatibility](compatibility.md).

## Timeouts and cancellation

| Limit | Default | What it limits | On expiry |
| --- | --- | --- | --- |
| `Timeout` keyword | 15 s | Resolving and connecting the socket to one host | Connection error |
| `CommandTimeout` | 30 s (`0` = none) | One command's execution | `TimeoutException` |
| Your `CancellationToken` | none | Whatever you pass it to | `OperationCanceledException` |

When a command times out or is cancelled, BlueTusk sends a PostgreSQL cancel
request on a separate connection, then reads the original connection until it
is idle again. The connection stays usable. Inside a transaction, the
transaction is now aborted and must be rolled back.

Waiting for a free pooled connection is not covered by `Timeout` or
`CommandTimeout`. When the pool is full, an open waits until a connection is
returned or its token is cancelled. Pass a token with a deadline.

## Pooling and reset

Each data source has its own pool (one pool per host for
[multi-host](multi-host.md) sources). `Maximum Pool Size` defaults to 100.

Before a used session is handed to another caller, BlueTusk:

1. rolls back any open or failed transaction;
2. runs `DISCARD ALL`, which clears temporary tables, `SET` values, prepared
   statements, `LISTEN` registrations and advisory locks;
3. checks that the session is still open and idle.

So session state never leaks between callers, and you cannot rely on it
surviving between leases either. Set per-session values inside the connection
that needs them. See [Connection pooling](pooling.md).

## Multiplexing

Multiplexing is an opt-in way to run many independent commands over a few
sessions. It applies only to commands created with
`dataSource.CreateCommand(...)` whose SQL does not depend on session state.
Everything else (connection-owned commands, transactions, `SET`, COPY,
`LISTEN`, cursors) uses a normal session. Turn it on with `Multiplexing=true` or
`EnableMultiplexing(...)`, and control one command with
`BlueTuskCommand.MultiplexingMode` (`Auto`, `Require`, `Disable`). See
[Pooling and multiplexing](pooling.md#statement-multiplexing) and the
[routing table](multiplexing-compatibility.md).

## How types are resolved

The data source loads the server's type catalogue the first time it opens a
connection. For each parameter, BlueTusk picks the PostgreSQL type in this
order:

1. `PostgreSqlTypeOid`;
2. `PostgreSqlTypeName` (schema-qualified, for example `pg_catalog.jsonb` or
   `app.order_status[]`);
3. `DbType`, when you set it;
4. the CLR type of the value (`int` is `int4`, `string` is `text`, `Guid` is
   `uuid`, and so on).

A `null` value has no CLR type to go on, so it needs one of the first three,
even in a typed `BlueTuskParameter<string?>`. Some CLR types are ambiguous. For example, `string[]` needs
`PostgreSqlTypeName = "pg_catalog.text[]"`, and a `string` that must be `jsonb`
needs `PostgreSqlTypeName = "pg_catalog.jsonb"`. `TimeSpan` maps to `time`, not
`interval`.

Map your own enums and composites once on the builder with `MapEnum` and
`MapComposite`. If you create or change a type while the app runs, call
`dataSource.ReloadTypesAsync()`. Until then, values of the new type are read as
an opaque `BlueTuskUnknownValue`, and `PostgreSqlTypeName` cannot find it. See
[PostgreSQL types](../types/README.md).

## Errors

Errors reported by PostgreSQL arrive as `BlueTuskException`, which derives
from `DbException`. Check `SqlState` (for example `23505` for a unique
violation) and `Severity`. Most connection failures are also
`BlueTuskException`, with the cause in `InnerException`. See [Troubleshooting](troubleshooting.md).

## Next steps

- [Quick start](quickstart.md)
- [Configuration](configuration.md)
- [Connection pooling](pooling.md)
- [Batches](batches.md)
