# ADO.NET compatibility

This page helps you check which standard ADO.NET features `BlueTusk.Data`
supports before you port code to it, including code written for Npgsql. A
feature marked **Not supported** throws an exception; BlueTusk never silently
approximates it.

| Feature | Status | Details |
| --- | --- | --- |
| Text commands | Supported | `CommandType.Text`, named or positional parameters, sync/async execution and local transactions. |
| Stored procedures and functions | Supported through SQL text | Use `CALL ...` for procedures and `SELECT ...` for functions. PostgreSQL `OUT` and `INOUT` values are read from returned result rows. |
| `CommandType.StoredProcedure` | Not supported | Setting it throws `NotSupportedException`. Write `CALL` or `SELECT` SQL instead. |
| Parameter directions | `Input` supported | `Output`, `InputOutput` and `ReturnValue` throw `NotSupportedException`. Use PostgreSQL result rows for output values. |
| Local transactions | Supported | `BeginTransaction`, command enlistment, commit, rollback and async equivalents. |
| Savepoints | Supported through SQL | Run `SAVEPOINT`, `ROLLBACK TO SAVEPOINT` and `RELEASE SAVEPOINT` as enlisted commands. `DbTransaction.Save`, `Release` and named `Rollback` throw `NotSupportedException`. |
| `System.Transactions` ambient/distributed enlistment | Not supported | No promotable, distributed or ambient enlistment. Keep work inside an explicit `DbTransaction`. |
| `CommandBehavior.Default` | Supported | All rows and result sets are buffered unless sequential access is selected. |
| `SingleRow` | Supported | At most the first row is exposed. |
| `SingleResult` | Supported | `NextResult` returns false after the first result set. |
| `SequentialAccess` | Supported | Uses the incremental portal reader; combine with `SingleRow`, `SingleResult` or `CloseConnection` as needed. |
| `CloseConnection` | Supported | Closing or disposing the reader closes its logical connection. |
| `SchemaOnly` and `KeyInfo` | Not supported | Both throw `NotSupportedException`; they are never silently ignored. |
| Reader schema | Supported | `GetColumnSchema`, `GetSchemaTable` and async equivalents expose names, ordinals, CLR/provider types and available origin metadata. |
| Connection schema | Supported | `MetaDataCollections`, `DataSourceInformation`, `DataTypes`, `Restrictions`, `ReservedWords`, `Databases`, `Schemas`, `Tables` and `Columns`. Live catalogue collections require an open connection. |
| Dapper | Supported | Parameter binding, command execution and mapping to your classes are tested against a real server. |
| Dependency injection | Supported | `BlueTusk.Data.DependencyInjection` registers one shared `BlueTuskDataSource` as both its concrete type and `DbDataSource`. |
| Readiness health check | Supported | The DI integration registers a `bluetusk` check tagged `bluetusk` and `ready`; it opens a connection and executes `SELECT 1`. |
| PostgreSQL 19 native `REPACK` | New in 1.1.0; preview | Requires PostgreSQL 19, which is in preview. See [Native REPACK](repack.md). |

## Host registration

```csharp
services.AddDataSource(
    configuration.GetConnectionString("PostgreSQL")!,
    builder => builder.ConfigureDiagnostics(diagnostics),
    healthCheckName: "postgresql");
```

Resolve `BlueTuskDataSource` for provider-specific features or `DbDataSource`
for provider-neutral application code. Keep the registered data source
long-lived so it owns and reuses the physical pool.

## Migration from Npgsql

For provider-neutral code, change the data-source registration and retain
`DbConnection`, `DbCommand`, `DbDataReader`, Dapper and explicit
`DbTransaction` usage. Replace provider-specific parameter types with
`BlueTuskParameter`, `DbType`, `PostgreSqlTypeOid` or
`PostgreSqlTypeName`.

Rewrite `CommandType.StoredProcedure` calls as explicit PostgreSQL SQL:

```csharp
command.CommandType = CommandType.Text;
command.CommandText = "CALL app.rotate_keys(@tenant_id)";
```

Read procedure/function output from the returned row rather than output
parameters. Replace `TransactionScope` with an explicit local transaction.
If your app needs ambient or distributed transactions, output parameters,
`SchemaOnly` or `KeyInfo`, keep it on a provider that implements them.

Also review connection strings when you migrate: BlueTusk has no keyword
aliases, ignores unknown keywords by default (Npgsql rejects them; see
[Reject unknown keywords](configuration.md#reject-unknown-keywords)) and
defaults to `SSL Mode=VerifyFull`. See
[Configuration](configuration.md#how-the-connection-string-is-parsed).
