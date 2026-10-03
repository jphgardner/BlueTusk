# ADO.NET troubleshooting

This page helps you match an error from `BlueTusk.Data` to its cause and fix.
For problems outside the provider, see the
[platform troubleshooting guide](../operations/troubleshooting.md).

## Read the whole exception chain

Connection failures arrive as a `BlueTuskException` that wraps the real
cause. Log `exception.ToString()` (never the connection string) and read the
innermost exception. To find a SQLSTATE anywhere in the chain:

```csharp
static string? FindSqlState(Exception exception)
{
    for (Exception? current = exception; current is not null; current = current.InnerException)
    {
        if (current is BlueTuskException { SqlState: { Length: > 0 } state })
        {
            return state;
        }

        if (current is BlueTuskServerException { SqlState: { Length: > 0 } serverState })
        {
            return serverState;
        }
    }

    return null;
}
```

`BlueTuskServerException` is in the `BlueTusk.Client` namespace.

## Connection failures

Outer message: `Could not open a PostgreSQL connection matching Any across 1 configured host(s).`

| Innermost exception | Cause | Fix |
| --- | --- | --- |
| `BlueTuskTransportException`: `... (ConnectionRefused)` | Nothing is listening on that host and port. | Start PostgreSQL, or fix `Host` and `Port`. Check firewalls. |
| `BlueTuskTransportException`: `... (NameResolution)` | The host name does not resolve. | Fix `Host` or DNS. |
| `BlueTuskTransportException`: `... (Timeout)` | No connection within `Timeout` (default 15 s). | Check the network path. |
| `BlueTuskServerException`: `database "orders" does not exist` | Wrong database name. | Create the database or fix `Database`. |
| `ArgumentException`: `... (Parameter 'Database')` or `(Parameter 'Username')` | The keyword is missing. Aliases such as `User ID` are not recognized. | Use `Database=` and `Username=`. See [parsing rules](configuration.md#how-the-connection-string-is-parsed). |
| `ObjectDisposedException`: `... 'BlueTusk.Data.BlueTuskConnectionPool'` | The data source was disposed. | Keep one for the app's lifetime. |
| `ArgumentException`: `The connection-string keyword 'usernme' is not supported by BlueTusk.` | You turned on [rejection of unknown keywords](configuration.md#reject-unknown-keywords); by default they are ignored. | Fix or remove the keyword. |

## TLS failures

| Error | Cause | Fix |
| --- | --- | --- |
| `BlueTuskAuthenticationException`: `PostgreSQL refused the required TLS connection.` | The default `SSL Mode=VerifyFull` needs TLS and the server has none. A misspelled `SslMode=Disable` is ignored. | Turn on TLS on the server. For a local test container only, use `SSL Mode=Disable;Channel Binding=Disable`. |
| A TLS handshake error from .NET's `SslStream`, such as `AuthenticationException` | The certificate is not trusted, or its name does not match `Host`. `Require` validates too. | Use the name on the certificate and trust its CA. For a private CA, see `UseRemoteCertificateValidationCallback`. |
| `ArgumentException`: `Required channel binding cannot be used when TLS is disabled.` | `Channel Binding=Require` with `SSL Mode=Disable`. | Turn on TLS, or use `Channel Binding=Prefer`. |
| `BlueTuskAuthenticationException`: `Channel binding is required, but PostgreSQL did not offer SCRAM-SHA-256-PLUS.` | The server, or a TLS-terminating proxy, cannot bind SCRAM to TLS. | Use `Channel Binding=Prefer`. |

## Authentication failures

| Error | Cause | Fix |
| --- | --- | --- |
| `BlueTuskServerException`: `password authentication failed for user "app"` (SQLSTATE `28P01`) | Wrong user or password. | Check the credentials. |
| `BlueTuskAuthenticationException`: `PostgreSQL requested password authentication, but no configured credential source produced a value.` | No `Password`, password file entry or callback. | Add one. See [Authentication](authentication.md). |
| `BlueTuskAuthenticationException`: `PostgreSQL requested cleartext password authentication over an unencrypted connection. ...` | Cleartext password without TLS. | Use TLS. `Allow Unencrypted Password=true` is for trusted test setups only. |
| `Synchronous connection opening requires a synchronous password provider.` | `Open()` with only an async callback. | Register a sync callback too, or use `OpenAsync`. |

## Pool exhaustion and timeouts

| Symptom or error | Cause | Fix |
| --- | --- | --- |
| `TimeoutException`: `The connection pool for db:5432 is exhausted: no connection became available within the 15-second Timeout. ...` | Every pooled connection stayed busy for `Timeout` (default 15 s). **Behaviour change in 1.1.0:** earlier versions waited forever. See [Pooling](pooling.md#what-happens-when-the-pool-is-full). | Dispose connections, readers and transactions promptly. Watch the `bluetusk.pool.waiters` metric. Raise `Maximum Pool Size` or `Timeout` if the load is expected. |
| `TimeoutException`: `The command exceeded its 30-second timeout.` (inner: `canceling statement due to user request`) | The statement ran longer than `CommandTimeout`. | Tune the query or raise `CommandTimeout` (`0` = no limit). |
| `OperationCanceledException`: `The PostgreSQL operation was cancelled.` | Your token was cancelled; the server statement was cancelled too. | Expected. In a transaction, roll back next. |
| `sorry, too many clients already` (SQLSTATE `53300`) | `Maximum Pool Size` times app instances exceeds the server's `max_connections`. | Lower `Maximum Pool Size`, or use PgBouncer. |

See [Connection pooling](pooling.md) and [timeouts](concepts.md#timeouts-and-cancellation).

## Commands and transactions

| Error | Cause | Fix |
| --- | --- | --- |
| `A command executed while the connection has an active transaction must enlist in that transaction.` | `Transaction` not set on the command. | Set `command.Transaction = transaction`. |
| `current transaction is aborted, commands ignored until end of transaction block` (SQLSTATE `25P02`) | An earlier command in the transaction failed. | Roll back, or use SQL savepoints around statements that may fail. |
| `NotSupportedException`: `Specified method is not supported.` from `transaction.Save(...)` | `DbTransaction` savepoint methods are not implemented. | Run `SAVEPOINT name` and `ROLLBACK TO SAVEPOINT name` as enlisted commands. |
| `A command cannot mix positional and named parameters.` | `@name` and `$1` in one command. | Use one style. |
| `Command text references named parameter 'id', but the parameter collection does not contain it.` | Missing or misspelled parameter. | Add it. |
| `Explicit preparation requires a command associated with an open connection.` | `Prepare` on a data-source command. | Prepare a connection-owned command. |
| `NotSupportedException`: `BlueTusk currently supports text commands only.` | `CommandType.StoredProcedure`. | Use `CALL proc(...)` or `SELECT func(...)`. |
| `The active field stream must be consumed or disposed before accessing another field.` | Sequential reader: a field opened while another stream is open. | Finish each field first. See [Sequential readers](sequential-readers.md). |

## Type mapping

| Error or symptom | Cause | Fix |
| --- | --- | --- |
| `A null parameter requires DbType, PostgreSqlTypeOid, or PostgreSqlTypeName so PostgreSQL can determine its type.` | A `null` or `DBNull.Value` parameter has no type, even in `BlueTuskParameter<string?>`. | Set `DbType` or `PostgreSqlTypeName`. |
| `PostgreSQL type app.order_status is not present in the loaded type catalogue.`, `CLR type ... does not have a BlueTusk parameter encoder yet. ...` for a mapped enum, or values read back as `BlueTuskUnknownValue` | The type was created after the data source loaded its catalogue (a data source can start before a mapped type exists), or the name is wrong. | Call `await dataSource.ReloadTypesAsync()` after `CREATE TYPE` or `ALTER TYPE`, or restart. EF Core `Migrate` and `MigrateAsync` reload it for you. |
| `FormatException`: `PostgreSQL type name 'jsonb' must be qualified as schema.name.` | `PostgreSqlTypeName` needs a schema. | Use `pg_catalog.jsonb`, `pg_catalog.text[]`, `app.order_status`. |
| `CLR type System.String[] does not have a BlueTusk parameter encoder yet. ...` | `string[]` is ambiguous. | Set `PostgreSqlTypeName = "pg_catalog.text[]"`. |
| `column "payload" is of type jsonb but expression is of type text` (SQLSTATE `42804`) | A `string` is sent as `text`. | Set `PostgreSqlTypeName = "pg_catalog.jsonb"`. |
| `'Paid' is not a catalogue label for PostgreSQL enum app.order_status.` | `MapEnum` without labels uses member names unchanged. | Pass a labels dictionary. |
| `PostgreSQL time must be between 00:00:00 and 24:00:00.` | `TimeSpan` maps to `time`. | For `interval`, use `BlueTuskInterval`. |
| `ExecuteScalarAsync<int>()` returns `0` for SQL `NULL` | A non-nullable `T` gets its default. | Use `ExecuteScalarAsync<int?>()`. |

See [PostgreSQL types](../types/README.md).

## PgBouncer and prepared statements

PgBouncer in **transaction** mode can give each transaction a different
server session.

- Session state (`SET`, temporary tables, `LISTEN`) does not survive between
  transactions. Keep it in one transaction, or use session mode.
- Preparation needs PgBouncer's prepared-statement support
  (`max_prepared_statements` above zero). Without it you can see
  `prepared statement "bluetusk_1" does not exist` (SQLSTATE `26000`). Turn it
  on, or do not prepare (`Max Auto Prepare=0`, no `Prepare` calls).
- Multiplexing works through PgBouncer in session and transaction mode. See
  [Multiplexing compatibility](multiplexing-compatibility.md#pgbouncer).

## Multi-host routing

See [Multi-host connections](multi-host.md) for how hosts are chosen.

| Error or symptom | Cause | Fix |
| --- | --- | --- |
| `Could not open a PostgreSQL connection matching Standby across 1 configured host(s).` (inner: `Host db-a:5432 does not match Standby (primary=True, read-only=False).`) | No host has the role in `Target Session Attributes`. | Add the right hosts, or use `prefer-standby` to fall back. |
| `Could not rent a PostgreSQL connection matching Any from 2 configured host pool(s).` | Every host failed. | The inner exceptions name each host and its error. |
| Login fails and other hosts are not tried | Authentication failures stop routing on purpose. | Fix the credentials. |
| A recovered host is not used straight away | After a network failure a host is skipped for 10 seconds. | Wait, or call `ClearPoolAsync()`. |

## Multiplexing

- `Multiplexing requires connection pooling.`: remove `Pooling=false`.
- `Multiplexing workers cannot exceed Maximum Pool Size.`: lower `WorkerCount`.
- `The command cannot be multiplexed because ...`: the command uses
  `MultiplexingMode.Require` but needs its own session. Use `Auto`.
