# ADO.NET V1 compatibility

This matrix is the V1 contract for provider-neutral ADO.NET consumers. A
capability marked excluded fails explicitly; it is not silently approximated.
The live acceptance suite is
`tests/BlueTusk.CompatibilityTests/AdoNetV1CompatibilityTests.cs`.

| Surface | V1 status | Contract |
| --- | --- | --- |
| Text commands | Supported | `CommandType.Text`, named or positional parameters, sync/async execution and local transactions. |
| Stored procedures and functions | Supported through SQL text | Use `CALL ...` for procedures and `SELECT ...` for functions. PostgreSQL `OUT` and `INOUT` values are read from returned result rows. |
| `CommandType.StoredProcedure` | Excluded | Setting it throws `NotSupportedException`; BlueTusk does not invent a provider-specific routine-name convention. |
| Parameter directions | `Input` supported | `Output`, `InputOutput` and `ReturnValue` throw `NotSupportedException`. Use PostgreSQL result rows for output values. |
| Local transactions | Supported | `BeginTransaction`, command enlistment, commit, rollback, savepoints and async equivalents. |
| `System.Transactions` ambient/distributed enlistment | Excluded | V1 does not promise promotable, distributed or ambient enlistment. Keep work inside an explicit `DbTransaction`. |
| `CommandBehavior.Default` | Supported | All rows and result sets are buffered unless sequential access is selected. |
| `SingleRow` | Supported | At most the first row is exposed. |
| `SingleResult` | Supported | `NextResult` returns false after the first result set. |
| `SequentialAccess` | Supported | Uses the incremental portal reader; combine with `SingleRow`, `SingleResult` or `CloseConnection` as needed. |
| `CloseConnection` | Supported | Closing or disposing the reader closes its logical connection. |
| `SchemaOnly` and `KeyInfo` | Excluded | Both throw `NotSupportedException`; they are never silently ignored. |
| Reader schema | Supported | `GetColumnSchema`, `GetSchemaTable` and async equivalents expose names, ordinals, CLR/provider types and available origin metadata. |
| Connection schema | Supported | `MetaDataCollections`, `DataSourceInformation`, `DataTypes`, `Restrictions`, `ReservedWords`, `Databases`, `Schemas`, `Tables` and `Columns`. Live catalogue collections require an open connection. |
| Dapper | Supported | Parameter binding, command execution and POCO materialisation are covered by live acceptance tests. |
| Dependency injection | Supported | `BlueTusk.Data.DependencyInjection` registers one shared `BlueTuskDataSource` as both its concrete type and `DbDataSource`. |
| Readiness health check | Supported | The DI integration registers a `bluetusk` check tagged `bluetusk` and `ready`; it opens a connection and executes `SELECT 1`. |

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
Applications that require ambient/distributed transactions, output parameters,
`SchemaOnly` or `KeyInfo` must stay on a provider that implements those
contracts for V1.
