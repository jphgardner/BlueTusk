# ADO.NET provider

Use `BlueTusk.Data` to run SQL against PostgreSQL from .NET with the standard
ADO.NET types: connections, commands, parameters, data readers and
transactions. It also gives you PostgreSQL features that generic ADO.NET does
not have, such as COPY, `LISTEN`/`NOTIFY`, large objects and multi-host
routing.

BlueTusk speaks the PostgreSQL wire protocol directly. It does not wrap or
depend on Npgsql.

## When to use it

Use `BlueTusk.Data` when you want:

- hand-written SQL, Dapper, or provider-neutral `DbDataSource` code;
- control over transactions, batches and round trips;
- bulk import and export with COPY;
- PostgreSQL notifications, large objects or streaming of large values.

Choose something else when:

- your app is mostly LINQ queries and change tracking: use the
  [EF Core provider](../ef-core/README.md), which is built on this one;
- you want to react to committed changes: use [Streams](../streams/README.md).

## Packages

| Package | Install it when |
| --- | --- |
| `BlueTusk.Data` | Always. The ADO.NET provider. |
| `BlueTusk.Data.DependencyInjection` | You use `Microsoft.Extensions.DependencyInjection`. Adds `AddDataSource` and a health check. |
| `BlueTusk.SourceGeneration` | You map composite types and want reflection-free code, for example for [NativeAOT](nativeaot.md). |
| `BlueTusk.Identity.Aws`, `.Azure`, `.GoogleCloud` | You sign in with a cloud identity instead of a password. See [Cloud identity](cloud-identity.md). |
| `BlueTusk.Extensions.*` | You use PostGIS, pgvector, TimescaleDB and other [extensions](../extensions/README.md). |

```powershell
dotnet add package BlueTusk.Data
```

See [Install BlueTusk](../getting-started/install.md) for release channels and
version pinning.

## Status

The provider is part of the Core release line. `1.0.0` is the current stable
release and `1.1.0-rc.1` is the current release candidate; `1.1.0` is not
released yet. It supports .NET 10 and PostgreSQL 15, 16, 17 and 18.
PostgreSQL 19 is preview only. See [Compatibility](compatibility.md) for the
ADO.NET features that are deliberately not supported.

## A first taste

```csharp
using BlueTusk.Data;

// One data source per connection string, kept for the life of the app.
await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();

await using var command = dataSource.CreateCommand(
    "SELECT title FROM todo_items WHERE id = @id");
command.Parameters.Add(new BlueTuskParameter<long>(1) { ParameterName = "id" });

var title = await command.ExecuteScalarAsync<string>();
```

The data source owns the connection pool and the type catalogue. Parameter
values are sent separately from the SQL, never pasted into it. The
[5-minute first app](../getting-started/quickstart.md) walks through this
end to end.

## Guides

| I want to | Read |
| --- | --- |
| Register the data source in an ASP.NET Core or worker app | [Dependency injection and health checks](dependency-injection.md) |
| Sign in with passwords, password files, Kerberos or client certificates | [Authentication](authentication.md) |
| Sign in with AWS, Azure or Google Cloud identity | [Cloud identity](cloud-identity.md) |
| Send several statements in one round trip | [Batches](batches.md) |
| Import or export many rows fast | [COPY](copy.md) |
| Read a large value without loading the whole row | [Sequential readers](sequential-readers.md) |
| Size and monitor the connection pool | [Connection pooling](pooling.md) |
| Share connections across many small commands | [Multiplexing compatibility](multiplexing-compatibility.md) |
| Connect to a primary and standbys | [Multi-host connections](multi-host.md) |
| Receive PostgreSQL notifications | [Notifications](notifications.md) |
| Store files as PostgreSQL large objects | [Large objects](large-objects.md) |
| Read database metadata at run time | [Schema discovery](schema-discovery.md) |
| Publish a trimmed or NativeAOT app | [NativeAOT and trimming](nativeaot.md) |
| Reclaim table space on PostgreSQL 19 | [Native REPACK](repack.md) (new in 1.1.0, preview) |
| Check which ADO.NET features are supported, or move from Npgsql | [Compatibility](compatibility.md) |
| Map PostgreSQL types to .NET types | [PostgreSQL types](../types/README.md) |
| Trace and measure database calls | [Diagnostics and observability](../observability.md) |

## Next steps

1. [Quick start](quickstart.md): build a small web API with a table, a
   transaction and dependency injection.
2. [Concepts](concepts.md): data sources, connections, commands, preparation,
   transactions and pooling.
3. [Guides](#guides): task pages for specific features.
4. [Configuration](configuration.md): every connection-string keyword and
   builder option.
5. [Troubleshooting](troubleshooting.md): common errors and their fixes.
