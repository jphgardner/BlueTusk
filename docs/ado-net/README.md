# ADO.NET

Use `BlueTusk.Data` when you want direct PostgreSQL commands, transactions,
batches, COPY, notifications, or replication from .NET. If your application is
primarily LINQ and change tracking, start with the [EF Core guide](../ef-core/README.md).

## Run one query

Create one long-lived data source and short-lived commands:

```csharp
await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();
await using var command = dataSource.CreateCommand(
    "SELECT @left::int4 + @right::int4");

command.Parameters.Add(new BlueTuskParameter<int>(20) { ParameterName = "left" });
command.Parameters.Add(new BlueTuskParameter<int>(22) { ParameterName = "right" });

var answer = await command.ExecuteScalarAsync<int>(); // 42
```

Values are bound separately from SQL. Keep the data source for the application
lifetime so commands and logical connections share its bounded physical pool.

## Choose the API by task

| Task                                             | API or guide                                 |
| ------------------------------------------------ | -------------------------------------------- |
| One command without connection-affine state      | `dataSource.CreateCommand(...)`              |
| Several commands in one transaction              | Open a connection, then begin a transaction. |
| Several independent statements in one round trip | [Batches](batches.md)                        |
| Bulk import or export                            | [COPY](copy.md)                              |
| Large field without buffering the complete row   | [Sequential readers](sequential-readers.md)  |
| Primary/standby routing                          | [Multi-host](multi-host.md)                  |
| High-concurrency session-neutral commands        | [Pooling and multiplexing](pooling.md)       |
| Reclaim table space with PostgreSQL 19            | [Native REPACK](repack.md)                   |

The [compatibility matrix](compatibility.md) records supported and explicitly
excluded ADO.NET, Dapper, dependency-injection, schema, and routine surfaces.

## How the provider works

Build one long-lived `BlueTuskDataSource` per distinct application configuration. The data source owns physical pooling, registered codecs, and its runtime PostgreSQL catalogue. Connections created directly with `new BlueTuskConnection(...)` are unpooled convenience/compatibility paths.

### Connection-string keywords

BlueTusk recognizes `Host`, `Port`, `Database`, `Username`, `Password`,
`Passfile`, `Timeout`, `Pooling`, `Multiplexing`, `Persist Security Info`,
`Application Name`, `SSL Mode`, `Channel Binding`, `Kerberos Service Name`,
`Allow Unencrypted Password`, `Target Session Attributes`, `Load Balance Hosts`,
`Minimum Pool Size`, `Maximum Pool Size`, `Connection Idle Lifetime`,
`Connection Lifetime`, `Max Auto Prepare`, and `Auto Prepare Min Usages`.
`Timeout` (seconds, default 15) bounds both connection establishment and the
wait for an exhausted pool; see [connection pooling](pooling.md).

For 1.x compatibility, other keywords (for example Npgsql's `Command Timeout`
or a misspelt `Usernme`) are accepted and ignored. To reject them, as Npgsql
does, enable the `BlueTusk.Data.RejectUnknownConnectionStringKeywords`
AppContext switch. Creating a connection or data source then throws an
`ArgumentException` that names the keyword but not its value:

```xml
<ItemGroup>
  <RuntimeHostConfigurationOption Include="BlueTusk.Data.RejectUnknownConnectionStringKeywords" Value="true" />
</ItemGroup>
```

Authentication defaults to TLS certificate verification with SCRAM-SHA-256 and prefers
SCRAM channel binding when PostgreSQL offers it. PostgreSQL GSSAPI/Kerberos and SSPI
requests use the operating system security context with mutual authentication. Legacy PostgreSQL MD5 challenges are
supported for compatibility, but MD5 is deprecated by PostgreSQL and should not be selected
for new deployments. A server request for cleartext password authentication is accepted over
an established TLS connection. It is rejected on an unencrypted connection unless the caller
deliberately sets `Allow Unencrypted Password=true`, which is intended only for trusted
compatibility environments:

```text
SSL Mode=VerifyFull;Channel Binding=Prefer
```

```text
SSL Mode=Disable;Channel Binding=Disable;Allow Unencrypted Password=true
```

Password and SCRAM frames use protocol storage that is overwritten immediately after the
transport flushes it, and temporary MD5/password byte arrays are cleared. A .NET connection
string and its immutable password `string` cannot be zeroed by the provider, so applications
should keep their lifetime narrow and avoid logging them. See PostgreSQL's current
[password authentication](https://www.postgresql.org/docs/current/auth-password.html) and
[encryption options](https://www.postgresql.org/docs/current/encryption-options.html) for the
server-side configuration and MD5 migration guidance. The compatibility gate creates isolated
test roles and executes both authentication paths against PostgreSQL 15–19; the normal matrix
user remains configured for SCRAM-SHA-256.

The [authentication guide](authentication.md) documents password-file lookup,
password and access-token callbacks, refresh timing, credential precedence,
GSSAPI/Kerberos service principals and credentials, TLS client certificates,
and callback-based certificate selection. Optional [cloud identity
adapters](cloud-identity.md) integrate AWS RDS/Aurora, Azure Database for
PostgreSQL, and Google Cloud SQL while keeping their SDKs out of the core
provider.

Commands without parameters use PostgreSQL's simple-query protocol and receive text fields. Commands with positional `$1`, `$2`, and subsequent placeholders use Parse, Bind, Describe, Execute, and Sync and prefer binary fields. Named `@name` and `:name` placeholders are rewritten to positional placeholders by a PostgreSQL-aware lexer that skips quoted strings, quoted identifiers, dollar-quoted bodies, and comments. If PostgreSQL reports that a selected type has no binary output function, an autocommit command retries once with text fields. Commands inside explicit transactions request text fields up front so format negotiation cannot abort the transaction. Parameter values are encoded separately as typed text or binary payloads and are never interpolated into SQL. The [type mapping reference](../types/README.md) lists the formats, CLR types, and edge-case behavior implemented by the current provider.

[NativeAOT and trimming](nativeaot.md) documents the provider-core publish
gate, source-generated mapping path, measured size/startup/allocation report,
and explicit runtime-only feature boundaries.

```csharp
await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();
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

BlueTusk infers built-in PostgreSQL type OIDs from `DbType` or the CLR value. A null parameter must set `DbType`, `PostgreSqlTypeOid`, or `PostgreSqlTypeName`; this avoids relying on ambiguous server inference. `PostgreSqlTypeName` resolves schema-qualified catalogue types through the connection's runtime registry and supports scalar and array names, including quoted identifiers:

```csharp
command.Parameters.Add(new BlueTuskParameter(null)
{
    PostgreSqlTypeName = "app.order_status",
});
command.Parameters.Add(new BlueTuskParameter(null)
{
    PostgreSqlTypeName = "app.order_status[]",
});
```

The current immutable catalogue snapshot is available from either
`dataSource.TypeRegistry` or an open `connection.TypeRegistry`. After creating,
altering, or dropping a user-defined type at runtime, call
`ReloadTypes()`/`ReloadTypesAsync()` on the long-lived data source. The same
methods are available on an open directly constructed connection for its local,
unpooled catalogue.

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

Cancellation tokens and `CommandTimeout` send PostgreSQL `CancelRequest` on a separate connection. BlueTusk waits for PostgreSQL to close that one-shot cancellation channel, then drains the original connection through `ReadyForQuery` before returning, so a late cancellation cannot escape into the next command and a cancelled connection remains reusable. `Cancel()` and `CancelAsync()` provide explicit cancellation. Cancellation inside a transaction leaves PostgreSQL's transaction in the failed state and requires rollback.

Low-level clients can use [PostgreSQL pipeline mode](../pipeline-mode.md) to send multiple extended-query synchronization groups in one flush. This is a Client-layer API rather than an ADO.NET batching alias; `BlueTuskBatch` remains the provider-neutral `DbBatch` surface.

`BlueTuskDataSource` owns a bounded physical connection pool by default. Logical connections return their physical session when closed or disposed; reuse rolls back an unfinished transaction when necessary and issues `DISCARD ALL` before handing the session to another caller. See [Connection pooling](pooling.md) for sizing, lifetime, warm-up, statistics, and drain controls.

Opt-in bounded statement multiplexing shares session-neutral commands across a
fixed number of worker lanes. The [multiplexing compatibility
matrix](multiplexing-compatibility.md) defines strict fallback, session-state,
failure, PgBouncer, metrics, and performance-evidence behavior.

[Multi-host connections](multi-host.md) support ordered or randomized attempts, shared or per-host ports, and primary/standby/read-write/read-only target selection.

Connection-owned [COPY APIs](copy.md) stream raw text, CSV, or binary payloads to and from PostgreSQL while preserving exclusive use of the physical session.

Connection-owned [`LISTEN`/`NOTIFY` APIs](notifications.md) deliver PostgreSQL notifications through a bounded asynchronous stream while leaving the primary connection session available for commands.

Transactional [large-object streams](large-objects.md) support asynchronous creation, deletion, reads, writes, 64-bit seeks, and truncation.

[`BlueTuskBatch`](batches.md) implements `DbBatch`/`DbBatchCommand` with parameters, ordered multiple results, preparation, transactions, timeouts, cancellation, and data-source-owned execution.

[Diagnostics and observability](../observability.md) documents redaction-safe
connection/command activities, OpenTelemetry metrics, query tags, and opt-in
slow-command events.

[Sequential readers](sequential-readers.md) use incremental portals and backend-frame reads. Unlimited reads use the unnamed portal; positive fetch sizes use bounded named portals. Their `GetStream` and `GetTextReader` paths consume binary `bytea`, text, JSON, and JSONB directly from the active network payload. Buffered readers retain the existing random-access behavior. Raw/text/typed-binary COPY, notification subscription and waiting, and large-object streams have separate native synchronous and asynchronous paths.
