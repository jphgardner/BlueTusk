# ADO.NET configuration

This page lists every setting you can give `BlueTusk.Data`: connection-string
keywords, `BlueTuskDataSourceBuilder` options, per-command properties and the
dependency-injection registration. Names and defaults come from the 1.1.0
source.

## Where settings go

| Setting kind | Where you set it |
| --- | --- |
| Server, credentials, TLS, pooling, routing | Connection string (this page's keyword tables) |
| Callbacks, certificates, type mappings, diagnostics, multiplexing tuning | `BlueTuskDataSourceBuilder` |
| Command timeout, protocol, streaming | Properties on each `BlueTuskCommand` or `BlueTuskBatch` |
| Host registration | `services.AddDataSource(...)` |

A typical app keeps the connection string in configuration and the rest in
code:

```json
{
  "ConnectionStrings": {
    "Orders": "Host=db.example.com;Database=orders;Username=orders_app;Maximum Pool Size=50;Application Name=orders-api"
  }
}
```

```csharp
builder.Services.AddDataSource(
    builder.Configuration.GetConnectionString("Orders")!,
    dataSourceBuilder => dataSourceBuilder
        .UsePasswordProvider(async (request, cancellationToken) =>
            await secrets.GetPasswordAsync(request.Username, cancellationToken))
        .ConfigureDiagnostics(new BlueTuskDiagnosticsOptions
        {
            SlowCommandThreshold = TimeSpan.FromMilliseconds(500),
        }));
```

`AddDataSource` is in `BlueTusk.Data.DependencyInjection`, and
`BlueTuskDiagnosticsOptions` is in `BlueTusk.Diagnostics`.

## How the connection string is parsed

- Keywords are case-insensitive. Write them as shown, with spaces
  (`SSL Mode`, not `SslMode`).
- There are **no aliases**. `Server`, `User ID`, `UID`, `Pwd` and `SslMode`
  are not recognized.
- **Unknown keywords are ignored without an error.** A misspelled keyword
  silently falls back to the default. For example, `SslMode=Disable` leaves
  `SSL Mode` at `VerifyFull`. Check spelling against the tables below.
- Enum values are case-insensitive, and `-`, `_` and spaces are ignored, so
  `read-write`, `ReadWrite` and `read_write` are the same. An invalid value
  throws `ArgumentException` (for example
  `'verify-ca' is not a valid value for SSL Mode.`) when the data source is
  created.
- Durations are whole seconds.
- `BlueTuskConnectionStringBuilder` exposes every keyword as a typed property
  (shown in the **Property** column).

```csharp
var settings = new BlueTuskConnectionStringBuilder
{
    Host = "db.example.com",
    Database = "orders",
    Username = "orders_app",
    MaximumPoolSize = 50,
    ApplicationName = "orders-api",
};

await using var dataSource = new BlueTuskDataSourceBuilder(settings.ConnectionString)
    .UsePasswordProvider(request => Environment.GetEnvironmentVariable("ORDERS_DB_PASSWORD")!)
    .Build();
```

## Connection

| Keyword | Property | Type | Default | Meaning |
| --- | --- | --- | --- | --- |
| `Host` | `Host` | string | `localhost` | Server host name or IP address. A comma-separated list enables [multi-host](multi-host.md) routing. |
| `Port` | `Port`, `Ports` | int or list | `5432` | Server port. Give one port for all hosts, or one per host in the same order. |
| `Database` | `Database` | string | none (required) | Database to connect to. Opening fails if it is empty. |
| `Username` | `Username` | string | none (required) | PostgreSQL role. Opening fails if it is empty. |
| `Application Name` | `ApplicationName` | string | `BlueTusk` | Sent as `application_name`; visible in `pg_stat_activity` and server logs. |
| `Timeout` | `Timeout` | seconds (> 0) | `15` | Time allowed to resolve and connect the network socket to one host. It does not limit TLS, authentication or waiting for a pooled connection. |

## Security and TLS

| Keyword | Property | Type | Default | Meaning |
| --- | --- | --- | --- | --- |
| `SSL Mode` | `SslMode` | `Disable`, `Prefer`, `Require`, `VerifyFull` | `VerifyFull` | `VerifyFull` and `Require` both require TLS and validate the server certificate and host name with the operating system's trust store. `Prefer` uses TLS when the server offers it (and still validates the certificate), otherwise connects without TLS. `Disable` never uses TLS. |
| `Channel Binding` | `ChannelBinding` | `Disable`, `Prefer`, `Require` | `Prefer` | SCRAM-SHA-256-PLUS channel binding to the TLS session. `Require` fails if the server does not offer it, and cannot be combined with `SSL Mode=Disable`. |
| `Allow Unencrypted Password` | `AllowUnencryptedPassword` | bool | `false` | Allows the server's cleartext password method without TLS. Use only in a trusted compatibility environment. |
| `Persist Security Info` | `PersistSecurityInfo` | bool | `false` | When `false`, `Password` and `Passfile` are removed from the public `ConnectionString` of a data source, and of a connection after it opens. |

There is no setting that encrypts without validating the certificate. For a
private certificate authority, install the CA in the trust store or use
`UseRemoteCertificateValidationCallback` (see
[builder options](#data-source-builder-options)).

## Authentication

| Keyword | Property | Type | Default | Meaning |
| --- | --- | --- | --- | --- |
| `Password` | `Password` | string | not set | Password for password, MD5 or SCRAM authentication. Prefer a password callback or password file. |
| `Passfile` | `Passfile` | path | not set | PostgreSQL password file. When not set, BlueTusk checks `PGPASSFILE`, then `%APPDATA%\postgresql\pgpass.conf` (Windows) or `~/.pgpass` (Linux, macOS). An empty value turns password-file lookup off. |
| `Kerberos Service Name` | `KerberosServiceName` | string | `postgres` | Service name for GSSAPI/Kerberos. Must match the server's `krb_srvname`. Cannot contain `/` or `@`. |

When the server asks for a password, BlueTusk uses the first source that
exists: access-token callback, password callback, `Password`, then the
password file. See [Authentication](authentication.md).

## Pooling

| Keyword | Property | Type | Default | Meaning |
| --- | --- | --- | --- | --- |
| `Pooling` | `Pooling` | bool | `true` | Gives the data source a pool of physical connections. |
| `Minimum Pool Size` | `MinimumPoolSize` | int (>= 0) | `0` | Connections opened by `WarmUpAsync()` and kept open. Per host. |
| `Maximum Pool Size` | `MaximumPoolSize` | int (> 0) | `100` | Hard limit on physical connections. Per host. Must be at least `Minimum Pool Size`. |
| `Connection Idle Lifetime` | `ConnectionIdleLifetime` | seconds (>= 0) | `300` | An idle connection older than this is closed instead of reused. `0` turns idle expiry off. |
| `Connection Lifetime` | `ConnectionLifetime` | seconds (>= 0) | `3600` | A connection older than this is closed when it is next checked out or returned. `0` turns it off. |

See [Connection pooling](pooling.md).

## Commands and preparation

| Keyword | Property | Type | Default | Meaning |
| --- | --- | --- | --- | --- |
| `Max Auto Prepare` | `MaxAutoPrepare` | int (>= 0) | `0` (off) | Maximum automatically prepared statements per physical connection. The least recently used is dropped first. |
| `Auto Prepare Min Usages` | `AutoPrepareMinUsages` | int (> 0) | `5` | Executions on one connection before a statement is prepared automatically. |

There is no `Command Timeout` keyword. Set `CommandTimeout` on the command
(see [per-command settings](#per-command-settings)). Automatic preparation is
explained in [Concepts](concepts.md#preparation).

## Multi-host routing

| Keyword | Property | Type | Default | Meaning |
| --- | --- | --- | --- | --- |
| `Target Session Attributes` | `TargetSessionAttributes` | `any`, `primary`, `standby`, `prefer-primary`, `prefer-standby`, `read-write`, `read-only` | `any` | Which server role is acceptable. |
| `Load Balance Hosts` | `LoadBalanceHosts` | `disable`, `random` | `disable` | `random` shuffles the host order for each new physical connection. |

See [Multi-host connections](multi-host.md).

## Multiplexing

| Keyword | Property | Type | Default | Meaning |
| --- | --- | --- | --- | --- |
| `Multiplexing` | `Multiplexing` | bool | `false` | Runs session-neutral data-source commands over shared connections. Requires `Pooling=true`. Tune it with `EnableMultiplexing(...)`. |

## Diagnostics

No connection-string keyword controls diagnostics. Set `Application Name` so
you can find your sessions on the server, and use `ConfigureDiagnostics` for
slow-command events. Traces and metrics are published under the name
`BlueTusk.Diagnostics`. See [Diagnostics and observability](../observability.md).

## Data source builder options

`BlueTuskDataSourceBuilder` collects settings that cannot live in a
connection string. Call the methods, then `Build()` once.

| Member | What it does |
| --- | --- |
| `MapEnum<TEnum>(typeName, labels)` | Maps a CLR enum to a PostgreSQL enum. Without `labels`, the member names must match the PostgreSQL labels exactly. |
| `MapComposite<T>(typeName)` | Maps a CLR type to a composite type. Members match fields by snake_case name. |
| `Types` | The type registry, for source-generated codecs (`Address.RegisterCodec(builder.Types)`) and custom codecs. |
| `UsePlugin(plugin)`, `Features` | Registers an extension package, such as PostGIS or pgvector. See [Extensions](../extensions/README.md). |
| `UsePasswordProvider(...)` | Sync or async callback that returns a password for each new physical connection. |
| `UseAccessTokenProvider(...)` | Sync or async callback that returns an access token. Cannot be combined with a password callback. |
| `RequireTlsForAccessTokens()` | Calls the token callback only after TLS is established. The [cloud identity](cloud-identity.md) packages turn this on. |
| `UseGssCredential(credential)` | Explicit Kerberos/SSPI credential instead of the process identity. |
| `UseClientCertificate(certificate)` | Adds a TLS client certificate. You own the certificate and must keep it valid. |
| `UseClientCertificateSelectionCallback(callback)` | Chooses which client certificate to present. |
| `UseRemoteCertificateValidationCallback(callback)` | Replaces server certificate validation. Your callback becomes the security check. |
| `ConfigureDiagnostics(options)` | Sets `BlueTuskDiagnosticsOptions.SlowCommandThreshold` (default `null`, off). |
| `EnableMultiplexing(configure)` | Turns on multiplexing and sets the options below. |

### Multiplexing options

Set these inside `EnableMultiplexing(options => ...)`:

| Property | Default | Meaning |
| --- | --- | --- |
| `WorkerCount` | `0` (automatic) | Concurrent multiplexing workers, each holding one pooled connection. Automatic is half of `Maximum Pool Size`, between 1 and 4. Cannot exceed `Maximum Pool Size`. |
| `QueueCapacity` | `1024` | Commands that can wait for a worker. |
| `MaxPipelineCommands` | `64` | Commands written to PostgreSQL in one network flush. Cannot exceed `MaxCommandsPerLease`. |
| `MaxCommandsPerLease` | `65536` | Commands a worker runs before it returns its connection to the pool. |
| `ShutdownTimeout` | 30 seconds | How long disposal waits for queued commands to finish. |

```csharp
await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .EnableMultiplexing(options =>
    {
        options.WorkerCount = 4;
        options.QueueCapacity = 2_048;
    })
    .Build();
```

## Per-command settings

| Member | Default | Meaning |
| --- | --- | --- |
| `BlueTuskCommand.CommandTimeout` | `30` seconds | `0` means no limit. On expiry BlueTusk cancels the statement on the server and throws `TimeoutException`. |
| `BlueTuskBatch.Timeout` | `30` seconds | The same for a whole batch. |
| `BlueTuskCommand.ExecutionMode` | `Auto` | `Simple` or `Extended` forces a protocol. See [Concepts](concepts.md#how-a-command-is-sent). |
| `BlueTuskCommand.MultiplexingMode` | `Auto` | `Require` fails if the command cannot be multiplexed; `Disable` always uses a dedicated connection. |
| `BlueTuskCommand.SequentialFetchSize` | `0` | Rows per fetch for `CommandBehavior.SequentialAccess`. `0` streams the whole result. See [Sequential readers](sequential-readers.md). |

## Dependency-injection registration

`BlueTusk.Data.DependencyInjection` adds one method:

```csharp
IServiceCollection AddDataSource(
    this IServiceCollection services,
    string connectionString,
    Action<BlueTuskDataSourceBuilder>? configure = null,
    string healthCheckName = "bluetusk")
```

| Parameter | Meaning |
| --- | --- |
| `connectionString` | Required. Must not be empty. |
| `configure` | Optional. Receives the builder before `Build()` runs, the first time the data source is resolved. |
| `healthCheckName` | Name of the readiness health check. It is tagged `bluetusk` and `ready`. |

It registers:

- `BlueTuskDataSource` as a singleton;
- `DbDataSource`, resolving to the same instance;
- `BlueTuskDataSourceHealthCheck`, which opens a connection and runs `SELECT 1`.

Registration uses `TryAdd`, so only the first `AddDataSource` call in a
container creates a data source. For a second database, build a second
`BlueTuskDataSource` yourself and wrap it in your own service type. See
[Dependency injection](dependency-injection.md).

## Full reference

- [PostgreSQL types](../types/README.md) and the
  [complete type reference](../types/reference.md)
- [Diagnostics and observability](../observability.md)
- [Security](../security.md)
