# EF Core provider

This page helps you decide whether to use BlueTusk's Entity Framework Core
provider and where to start. The provider lets you use LINQ, change tracking,
migrations and database-first scaffolding with PostgreSQL, on top of BlueTusk's
own PostgreSQL driver. It does not use Npgsql.

## When to use it

Use the EF Core provider when:

- your application already uses EF Core, or you want LINQ queries and change
  tracking instead of hand-written SQL;
- you want EF Core migrations to own your PostgreSQL schema, including
  PostgreSQL-only objects such as enums, extensions, `GIN` indexes and
  row-level security; or
- you want to generate entity classes from an existing database.

Use the [ADO.NET provider](../ado-net/README.md) (`BlueTusk.Data`) instead when
you only need SQL commands, binary `COPY`, notifications or replication. You can
use both in one application: they share the same data source and connection
pool.

## What it supports

- **Queries**: standard LINQ, plus PostgreSQL operators and functions through
  `EF.Functions` (arrays, ranges, JSON, full-text search, window functions and
  more).
- **PostgreSQL types**: arrays and `List<T>`, ranges and multiranges, `json`
  and `jsonb` (including EF's `ToJson()`), network, geometric and bit-string
  types, and your own enums, composites and domains.
- **Saving**: change tracking, generated keys (identity columns), sequences,
  `xmin` optimistic concurrency, transactions and savepoints.
  **New in 1.1.0:** `SaveChanges` sends inserts, updates and deletes in
  batches of up to 42 statements. In 1.0.0 and 1.1.0-rc.1 each statement was
  a separate command.
- **Migrations**: tables, keys, indexes and sequences, plus PostgreSQL enums,
  domains, extensions, collations, partitioning, triggers, views, functions,
  publications and row-level security.
- **Scaffolding**: `dotnet ef dbcontext scaffold` or the `bluetusk scaffold`
  command.

## Packages

| Package | Install it when | Notes |
| --- | --- | --- |
| `BlueTusk.EntityFrameworkCore` | Always | The provider. Brings in `BlueTusk.Data`. |
| `BlueTusk.EntityFrameworkCore.Design` | You use `dotnet ef` (migrations or scaffolding) | Design-time services. |
| `Microsoft.EntityFrameworkCore.Design` | You use `dotnet ef` | Microsoft's design-time package. Not installed for you. |
| `BlueTusk.Tool` | You want the `bluetusk scaffold` command | A .NET tool, not a project package. |
| `BlueTusk.Extensions.*.EntityFrameworkCore` | You use PostGIS, pgvector, citext or TimescaleDB | See [PostgreSQL extensions](../extensions/README.md). |

```powershell
dotnet add package BlueTusk.EntityFrameworkCore
```

Keep every BlueTusk package on the same version. See
[Install BlueTusk](../getting-started/install.md) for release channels and
version pinning.

## Requirements and status

- .NET 10 (`net10.0`).
- EF Core **10.0.11**. Use version 10.0.11 for `Microsoft.EntityFrameworkCore.*`
  packages and the `dotnet-ef` tool.
- PostgreSQL 15, 16, 17 or 18. PostgreSQL 19 is preview only.

The EF Core provider is part of the Core release line. `1.0.0` is the current
stable release and `1.1.0-rc.1` is the release candidate. `1.1.0` is not
published yet. PostgreSQL 19 property-graph (SQL/PGQ) queries through EF Core
are a [preview feature](../graph/README.md).

## A taste of the code

Create one data source for the application, then give it to EF Core:

```csharp
using BlueTusk.Data;
using Microsoft.EntityFrameworkCore;

await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();

var options = new DbContextOptionsBuilder<LibraryContext>()
    .UseBlueTusk(dataSource)
    .Options;

await using var db = new LibraryContext(options);
var classics = await db.Books
    .Where(book => book.Published < 1970)
    .OrderBy(book => book.Title)
    .ToListAsync();
```

`LibraryContext` is an ordinary `DbContext`. The
[quick start](quickstart.md) builds it step by step.

## Guides

| Task | Where |
| --- | --- |
| Register the provider in an app with dependency injection | [Concepts: data source and DbContext](concepts.md#how-long-should-the-data-source-and-dbcontext-live) |
| Create and apply migrations | [Quick start](quickstart.md) and [Concepts: migrations](concepts.md#how-do-migrations-work) |
| Generate a model from an existing database | [Concepts: scaffolding](concepts.md#how-do-i-start-from-an-existing-database) and [scaffold options](configuration.md#scaffold-command-options) |
| Map PostgreSQL enums, arrays, ranges and JSON | [Concepts: type mapping](concepts.md#how-are-net-types-mapped-to-postgresql) and [model configuration](configuration.md#model-configuration) |
| Handle concurrency conflicts and retries | [Concepts: concurrency](concepts.md#how-do-i-detect-concurrent-updates) |
| Use PostGIS, pgvector, citext or TimescaleDB | [PostgreSQL extensions](../extensions/README.md) |
| Look up a PostgreSQL function, operator or migration helper | [Full EF Core reference](reference.md) |

## Next steps

1. [Quick start](quickstart.md): build a working app with migrations in about
   ten minutes.
2. [Concepts](concepts.md): lifetimes, type mapping, batching, concurrency,
   transactions and migrations.
3. [Configuration](configuration.md): every `UseBlueTusk` option, model
   configuration method and scaffold option.
4. [Troubleshooting](troubleshooting.md): common errors and how to fix them.

The [full EF Core reference](reference.md) covers every translated function,
operator and migration feature in depth. The
[specification-test record](specification-tests.md) is for provider
maintainers.
