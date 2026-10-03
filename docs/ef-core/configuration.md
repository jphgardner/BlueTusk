# EF Core configuration

This page lists every setting you can use to configure the BlueTusk EF Core
provider: the `UseBlueTusk` overloads, the provider options, the
PostgreSQL-specific model configuration methods, and the scaffold command
options. For explanations, see [Concepts](concepts.md).

## Connection string

The provider uses the same connection string as the ADO.NET provider. The
keywords you are most likely to change:

| Keyword | Default | Meaning |
| --- | --- | --- |
| `Host`, `Port`, `Database`, `Username`, `Password` | `Port=5432` | Where and how to connect. |
| `SSL Mode` | `VerifyFull` | TLS with certificate and host name checks. Use `Disable` only for a local test container. |
| `Timeout` | `15` | Seconds to wait when opening a connection. |
| `Pooling` | `true` | Applies to a data source's pool. |
| `Minimum Pool Size`, `Maximum Pool Size` | `0`, `100` | Pool limits for the data source. |

See the [ADO.NET guide](../ado-net/README.md) and
[connection pooling](../ado-net/pooling.md) for every keyword.

## UseBlueTusk overloads

Call `UseBlueTusk` on a `DbContextOptionsBuilder` (or the generic
`DbContextOptionsBuilder<TContext>`). Every overload takes an optional last
argument, `Action<BlueTuskDbContextOptionsBuilder>`, for the
[provider options](#bluetusk-options).

| Overload | Use it when |
| --- | --- |
| `UseBlueTusk(BlueTuskDataSource dataSource, ...)` | Always, in applications. Contexts share the data source's pool and its enum and composite mappings. You dispose the data source. |
| `UseBlueTusk(string? connectionString, ...)` | Tools and tests. Each context opens its own **unpooled** connection. Runtime type mappings are not available. |
| `UseBlueTusk(BlueTuskConnection connection, bool contextOwnsConnection = false, ...)` | You manage one connection yourself, for example to share it with ADO.NET code. |

```csharp
// Recommended: share the application's data source and its pool.
options.UseBlueTusk(dataSource);
```

```csharp
// A connection string: each context opens its own unpooled connection.
options.UseBlueTusk(connectionString);
```

```csharp
// An existing connection. The context disposes it only if contextOwnsConnection is true.
options.UseBlueTusk(connection, contextOwnsConnection: false);
```

## BlueTusk options

These methods are on `BlueTuskDbContextOptionsBuilder`, the object passed to
the `UseBlueTusk` callback. `UseAdminDatabase` is BlueTusk-specific; the others
are EF Core relational options with BlueTusk's defaults.

| Method | Default | Meaning |
| --- | --- | --- |
| `MaxBatchSize(int)` | `42` | Most commands sent together by one `SaveChanges` batch. `1` sends each statement on its own. **New in 1.1.0:** batching was off in 1.0.0 and 1.1.0-rc.1. A batch is also split at 65,536 characters of SQL or 32,767 parameters. |
| `MinBatchSize(int)` | `1` | Fewer commands than this are sent without batching. |
| `CommandTimeout(int?)` | `30` seconds | Seconds before a command is cancelled. |
| `ExecutionStrategy(Func<ExecutionStrategyDependencies, IExecutionStrategy>)` | No retries | Your retry strategy. See [Concepts](concepts.md#how-do-transactions-and-retries-work). |
| `MigrationsAssembly(string)` or `MigrationsAssembly(Assembly)` | The context's assembly | Where migrations live. |
| `MigrationsHistoryTable(string tableName, string? schema = null)` | `__EFMigrationsHistory`, unqualified (so usually in `public`) | Name and schema of the history table. |
| `UseQuerySplittingBehavior(QuerySplittingBehavior)` | `SingleQuery` | Load collections with one query or one query per collection. |
| `UseRelationalNulls(bool useRelationalNulls = true)` | `false` | `true` uses SQL null comparison semantics instead of C# semantics. |
| `UseParameterizedCollectionMode(ParameterTranslationMode)` | `MultipleParameters` | How a captured collection in `Contains` is sent: `IN (@ids1, @ids2, ...)` by default, or inline constants with `Constant`. `Parameter` is not supported yet (see [Troubleshooting](troubleshooting.md#queries)). |
| `UseAdminDatabase(string databaseName)` | `postgres` (`template1` when the target is `postgres`) | Existing database used to create or drop the target database. |
| `ContextOptionsBuilder` (property) | | The underlying `DbContextOptionsBuilder`. Extension packages use it. |

`TranslateParameterizedCollectionsToConstants()` and
`TranslateParameterizedCollectionsToParameters()` are marked obsolete by EF
Core 10. Use `UseParameterizedCollectionMode` instead.

A context that sets several options:

```csharp
options.UseBlueTusk(dataSource, provider => provider
    .MaxBatchSize(100)
    .CommandTimeout(60)
    .MigrationsHistoryTable("__EFMigrationsHistory", "app")
    .UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)
    .ExecutionStrategy(dependencies => new RetryOnSerializationFailure(dependencies))
    .UseAdminDatabase("maintenance"));
```

`RetryOnSerializationFailure` is the example strategy from
[Concepts](concepts.md#how-do-transactions-and-retries-work).

### Extension package options

Optional packages add methods to the same builder. Register the matching
data-source method too. Each method takes an optional `schema` argument (default
`"public"`): the schema where the extension is installed.

| Package | Provider option | Data source method |
| --- | --- | --- |
| `BlueTusk.Extensions.Citext.EntityFrameworkCore` | `UseCitext()` | `UseCitext()` |
| `BlueTusk.Extensions.PgVector.EntityFrameworkCore` | `UsePgVector()` | `UsePgVector()` |
| `BlueTusk.Extensions.PostGIS.EntityFrameworkCore` | `UsePostGis()` | `UsePostGis()` |
| `BlueTusk.Extensions.TimescaleDB.EntityFrameworkCore` | `UseTimescaleDb()` | `UseTimescaleDb()` |

See [PostgreSQL extensions](../extensions/README.md).

## Model configuration

These extension methods are in the `Microsoft.EntityFrameworkCore` namespace.
Use them in `OnModelCreating` next to the standard EF Core fluent API.
Migrations create, change and drop the objects they describe.

### Columns and keys

| Method | On | Meaning |
| --- | --- | --- |
| `UseIdentityColumn(BlueTuskIdentityGeneration generation = ByDefault)` | property | `GENERATED BY DEFAULT AS IDENTITY` or `GENERATED ALWAYS AS IDENTITY`. Integer keys get `ByDefault` without this call. |
| `UseXminConcurrencyToken()` | entity | Maps `xmin` as a concurrency token. |
| `UseSystemColumn(BlueTuskSystemColumn)`, `UseSystemColumns()` | entity | Maps `tableoid`, `xmin`, `cmin`, `xmax`, `cmax` or `ctid` as read-only shadow properties. |
| `HasColumnType(string)` (EF Core) | property | Chooses the PostgreSQL type, for example `"jsonb"`, `"cidr"` or `"app.order_status"`. |

### Indexes

| Method | Meaning |
| --- | --- |
| `UseIndexMethod(string)` | Index method: `btree`, `hash`, `gin`, `gist`, `brin`, `spgist` or an extension's method. |
| `UseOperatorClass(params string?[])` | Operator class per column, for example `"jsonb_path_ops"`. |
| `UseCollation(params string?[])` | Collation per column. |
| `HasNullSortOrder(params BlueTuskIndexNullSortOrder[])` | `NULLS FIRST` or `NULLS LAST` per column. |
| `IncludeProperties(...)` | Non-key `INCLUDE` columns (expression or property names). |
| `HasNullsDistinct(bool distinct = true)` | `NULLS DISTINCT` or `NULLS NOT DISTINCT` for unique indexes. |
| `IsConcurrent(bool concurrent = true)` | `CREATE INDEX CONCURRENTLY`. |
| `HasFillFactor(int)` | Fill factor, 10 to 100. |
| `HasStorageParameter(string name, string value)` | Any other `WITH (...)` storage parameter. |
| `HasIndexExpressions(params string?[])` | SQL expressions instead of columns. |

Entities also have `HasExpressionIndex`, `HasExclusionConstraint` and
`HasCheckConstraints` (with `IsNoInherit`, `IsNotValid` and `IsNotEnforced` on
a check constraint).

### Types, extensions and other database objects

| Method | On | Creates |
| --- | --- | --- |
| `HasEnum(name, labels, schema)` | model | An enum type. |
| `HasDomain`, `HasComposite`, `HasRange` | model | A domain, composite or range (with its multirange) type. |
| `HasExtension(name, ...)` | model | `CREATE EXTENSION`, with `UseSchema`, `HasVersion`, `DependsOnExtension` and `InstallDependencies` on the builder. |
| `HasCollation(name, ...)` | model | A collation. |
| `HasSequence<T>(...)` (EF Core) | model | A sequence. |
| `HasFunction`, `HasProcedure`, `HasRoutine` | model | Functions and procedures. |
| `HasView`, `HasMaterializedView` | model | Views and materialized views. |
| `HasAggregate`, `HasOperator`, `HasOperatorClass`, `HasOperatorFamily`, `HasCast` | model | Other schema objects. |
| `HasPublication`, `HasSubscription` | model | Logical-replication publications and subscriptions. |
| `HasTablespace`, `HasEventTrigger` | model | Tablespaces and event triggers. |
| `HasForeignDataWrapper`, `HasForeignServer`, `HasUserMapping`, `HasForeignTable` | model / entity | Foreign data objects. |
| `HasRangePartitioning`, `HasListPartitioning`, `HasHashPartitioning` | entity | A partitioned table. |
| `HasTrigger`, `HasRule` | entity | Triggers and rewrite rules. |
| `UseRowLevelSecurity`, `HasRowLevelSecurity` | entity | Row-level security and policies. |
| `InheritsFromTable` | entity | PostgreSQL table inheritance. |

Most have a matching `HasNo...` method to remove the object. The
[full reference](reference.md#migrations) shows each one with the SQL it
creates.

### Example

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.HasDefaultSchema("app");
    modelBuilder.HasEnum("order_status", ["pending", "shipped"], schema: "app");
    modelBuilder.HasSequence<long>("order_numbers", "app").StartsAt(1000);

    modelBuilder.Entity<Order>(order =>
    {
        order.Property(o => o.Id).UseIdentityColumn(BlueTuskIdentityGeneration.Always);
        order.Property(o => o.Number).HasDefaultValueSql("nextval('app.order_numbers')");
        order.Property(o => o.Customer).HasMaxLength(200);
        order.Property(o => o.Status).HasColumnType("app.order_status");
        order.Property(o => o.Total).HasPrecision(18, 2);
        order.ComplexCollection(o => o.Lines, lines => lines.ToJson());

        order.HasIndex(o => o.Tags).UseIndexMethod("gin");
        order.HasIndex(o => o.Customer).IncludeProperties(o => o.Total).HasFillFactor(90);

        order.UseXminConcurrencyToken();
    });
}
```

With these entity types:

```csharp
public enum OrderStatus
{
    [BlueTuskName("pending")] Pending,
    [BlueTuskName("shipped")] Shipped,
}

public readonly record struct OrderLine(string Sku, int Quantity);

public sealed class Order
{
    public long Id { get; set; }
    public long Number { get; set; }
    public required string Customer { get; set; }
    public OrderStatus Status { get; set; }
    public decimal Total { get; set; }
    public DateTimeOffset PlacedAt { get; set; }
    public string[] Tags { get; set; } = [];
    public BlueTuskRange<DateOnly> DeliveryWindow { get; set; }
    public List<OrderLine> Lines { get; set; } = [];
}
```

The migration for this model creates:

```sql
CREATE TYPE "app"."order_status" AS ENUM ('pending', 'shipped');
CREATE SEQUENCE "app"."order_numbers" START WITH 1000 INCREMENT BY 1 NO CYCLE;
CREATE TABLE "app"."Orders" (
    "Id" bigint NOT NULL GENERATED ALWAYS AS IDENTITY,
    "Number" bigint NOT NULL DEFAULT (nextval('app.order_numbers')),
    "Customer" character varying(200) NOT NULL,
    "Status" app.order_status NOT NULL,
    "Total" numeric(18,2) NOT NULL,
    "PlacedAt" timestamp with time zone NOT NULL,
    "Tags" text[] NOT NULL,
    "DeliveryWindow" daterange NOT NULL,
    "Lines" jsonb NOT NULL,
    CONSTRAINT "PK_Orders" PRIMARY KEY ("Id")
);
CREATE INDEX "IX_Orders_Customer" ON "app"."Orders" ("Customer") INCLUDE ("Total") WITH (fillfactor = 90);
CREATE INDEX "IX_Orders_Tags" ON "app"."Orders" USING "gin" ("Tags");
```

The application's data source must also map the enum:
`.MapEnum<OrderStatus>("app.order_status")`. See
[Concepts](concepts.md#postgresql-enums-need-three-pieces).

## Design-time setup

`dotnet ef` needs two packages in the startup project:
`BlueTusk.EntityFrameworkCore.Design` and
`Microsoft.EntityFrameworkCore.Design` (version 10.0.11). BlueTusk registers
its design-time services automatically; you do not write an
`IDesignTimeServices` class. Use `dotnet-ef` version 10.0.11.

To scaffold with `dotnet ef`, pass the provider name
`BlueTusk.EntityFrameworkCore`:

```powershell
dotnet ef dbcontext scaffold "Name=ConnectionStrings:Library" BlueTusk.EntityFrameworkCore --output-dir Scaffolded --table Authors --table Books --context ScaffoldedContext
```

`Name=ConnectionStrings:Library` reads the connection string from
configuration, so it is not copied into the generated code.

## Scaffold command options

`bluetusk scaffold` generates a `DbContext` and entity classes without a
project build. Install it with `dotnet tool install --global BlueTusk.Tool`.

```powershell
$env:BLUETUSK_CONNECTION_STRING = "Host=localhost;Port=5432;Username=postgres;Password=local-dev-only;Database=library;SSL Mode=Disable;Channel Binding=Disable"
bluetusk scaffold --schema public --context LibraryContext --namespace Library.Data --output Data
```

| Option | Default | Meaning |
| --- | --- | --- |
| `--connection <value>` | `BLUETUSK_CONNECTION_STRING` | Connection string. Required unless the environment variable is set. Removed from error messages. |
| `--schema <name>` | All schemas | Include a schema. Repeatable. |
| `--table <schema.table>` | All tables | Include a table. Repeatable. Without it, `__EFMigrationsHistory` is scaffolded too. |
| `--include-graphs`, `--include-functions`, `--include-views` | Included | Accepted for scripts; graphs, routines and views are always included. |
| `--output <directory>` | `Models` | Output folder, relative to `--project-dir`. |
| `--project-dir <directory>` | Current directory | Base project folder. |
| `--context <name>` | `BlueTuskContext` | `DbContext` class name. |
| `--namespace <name>` | `BlueTusk.Models` | Entity namespace. |
| `--context-namespace <name>` | Same as `--namespace` | `DbContext` namespace. |
| `--root-namespace <name>` | Same as `--namespace` | Project root namespace. |
| `--data-annotations` | Off | Use attributes where possible instead of fluent calls. |
| `--use-database-names` | Off | Keep database identifiers as class and property names. |
| `--no-pluralize` | Off | Do not pluralize or singularize names. |
| `--include-connection-string` | Off | Generate `OnConfiguring` with the connection string. |
| `--force` | Off | Overwrite existing files. |

Run `bluetusk scaffold --help` to print the same list. The command exits with
`0` on success, `1` if scaffolding fails, and `2` for an unknown option, a
missing value or a missing connection string. See the [tool README](../../tooling/BlueTusk.Tool/README.md) for
`bluetusk doctor`.

## Full reference

The [full EF Core reference](reference.md) documents every translated
function and operator, query construct and migration operation.
