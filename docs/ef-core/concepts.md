# EF Core concepts

This page explains the rules you need to use the BlueTusk EF Core provider
well. For the shared vocabulary (data source, pool,
type catalogue), read [Core concepts](../getting-started/concepts.md) first.

## How long should the data source and DbContext live?

```text
BlueTuskDataSource      singleton, app lifetime
  ├─ connection pool    physical PostgreSQL sessions
  └─ type catalogue     built-in types plus your MapEnum / MapComposite types
        ▲
        │ borrows a connection while a query or SaveChanges runs
        │
DbContext (scoped)      one per request or unit of work, never shared between threads
```

- Create **one `BlueTuskDataSource`** per connection string and keep it for the
  life of the application.
- Create **one `DbContext` per unit of work**. `AddDbContext` registers it as
  scoped, which in ASP.NET Core means one per request.
- Pass the data source to `UseBlueTusk`. In an app with dependency injection,
  `AddDataSource` (package `BlueTusk.Data.DependencyInjection`) registers the
  singleton and a readiness health check:

```csharp
builder.Services.AddDataSource(
    builder.Configuration.GetConnectionString("Shop")!,
    dataSource => dataSource.MapEnum<OrderStatus>("app.order_status"));

builder.Services.AddDbContext<ShopContext>((services, options) =>
    options.UseBlueTusk(services.GetRequiredService<BlueTuskDataSource>()));
```

`UseBlueTusk` also accepts a connection string or an open `BlueTuskConnection`
(see [Configuration](configuration.md#usebluetusk-overloads)), but avoid them
in applications. With a connection string, each context creates its own
**unpooled** connection, and enum or composite mappings registered on a data
source are not available.

See [Dependency injection](../ado-net/dependency-injection.md) for `AddDataSource`.

## How are .NET types mapped to PostgreSQL?

When you do not choose a column type, BlueTusk uses these defaults:

| .NET type | PostgreSQL type |
| --- | --- |
| `bool` | `boolean` |
| `short`, `int`, `long` | `smallint`, `integer`, `bigint` |
| `float`, `double` | `real`, `double precision` |
| `decimal` | `numeric` (`numeric(p,s)` with `HasPrecision`) |
| `string` | `text` (`character varying(n)` with `HasMaxLength`) |
| `Guid` | `uuid` |
| `byte[]` | `bytea` |
| `DateTimeOffset` | `timestamp with time zone` |
| `DateTime` | `timestamp without time zone` |
| `DateOnly`, `TimeOnly` | `date`, `time without time zone` |
| `TimeSpan` | `interval` |
| `T[]`, `List<T>` of a supported `T` | PostgreSQL array, for example `text[]` |
| `BlueTuskRange<int>` (and `long`, `DateOnly`, `DateTime`, `DateTimeOffset`, `BlueTuskNumeric`) | `int4range` (and the matching range type) |
| Owned or complex type with `ToJson()` | `jsonb` |
| CLR `enum` | its underlying integer type |

Rules worth knowing:

- **Use `DateTimeOffset` for points in time.** `DateTime` maps to
  `timestamp without time zone` and reads back with
  `DateTimeKind.Unspecified`. This differs from Npgsql, which maps `DateTime`
  to `timestamptz`. `DateTimeOffset` values read back in UTC.
- **Choose another type with `HasColumnType`**, for example `"jsonb"` for a
  `string` or `"cidr"` for a `BlueTuskNetworkAddress`.
- **Complex collections of structs work.** EF Core 10 rejects a collection of
  value-type complex objects; BlueTusk accepts it when the collection is mapped
  to JSON with `ToJson()`.

### PostgreSQL enums need three pieces

A CLR enum is stored as an integer unless you map it to a PostgreSQL enum.
To use a PostgreSQL enum:

1. Declare it in the model so migrations create it:
   `modelBuilder.HasEnum("order_status", ["pending", "shipped"], schema: "app");`
2. Point the property at it:
   `order.Property(o => o.Status).HasColumnType("app.order_status");`
3. Register the CLR type on the data source:
   `.MapEnum<OrderStatus>("app.order_status")`.

BlueTusk sends each enum member's **CLR name** as the label unless you
override it. Match lower-case labels with `[BlueTuskName("pending")]` (from
`BlueTusk.TypeSystem`), `[EnumMember(Value = "pending")]`, or the `labels`
dictionary of `MapEnum`. Composites follow the same pattern (`HasComposite`
and `MapComposite`). Domains need only `HasDomain` and `HasColumnType`. See
[PostgreSQL types](../types/README.md) and the
[full reference](reference.md#postgresql-type-mappings).

## How does SaveChanges batch commands?

**New in 1.1.0.** `SaveChanges` groups inserts, updates and deletes into
batches. One batch carries up to **42 commands** by default. In 1.0.0 and
1.1.0-rc.1 every statement was sent as a separate command.

```csharp
db.Orders.AddRange(newOrders);
await db.SaveChangesAsync(cancellationToken);
```

- EF Core still orders commands by their dependencies: a parent row is
  inserted before the rows that need its generated key.
- A batch is split before it exceeds 65,536 characters of SQL or 32,767
  parameters, whatever `MaxBatchSize` says.
- Generated keys, computed columns and concurrency checks still reach the
  right entities.
- Logging and `DbCommandInterceptor` see **one command per batch**. When a
  command interceptor is registered, BlueTusk keeps the older per-row result
  shape so the interceptor's reader stays compatible.
- If a statement fails, `DbUpdateException.Entries` lists every entry in the
  failing batch, not only the one that caused the error.

To go back to one command per statement, set
`provider => provider.MaxBatchSize(1)`. See
[Configuration](configuration.md#bluetusk-options) and the
[batching reference](reference.md#savechanges-batching).

## How do I detect concurrent updates?

Use PostgreSQL's `xmin` system column as a concurrency token. PostgreSQL
changes it on every update, so you do not need an extra column:

```csharp
order.UseXminConcurrencyToken();
```

This adds a shadow property named `xmin` (`BlueTuskSystemColumns.Xmin`).
Migrations never create it, because PostgreSQL owns it. When a row changed
since you read it, `SaveChanges` throws `DbUpdateConcurrencyException`:

```csharp
var order = await db.Orders.SingleAsync(o => o.Id == orderId);
order.Status = OrderStatus.Shipped;

try
{
    await db.SaveChangesAsync();
}
catch (DbUpdateConcurrencyException conflict)
{
    // Someone else changed the row since it was read. Here the database wins:
    // reload the current values, then decide whether to apply the change again.
    foreach (var entry in conflict.Entries)
    {
        await entry.ReloadAsync();
    }
}
```

Standard EF Core concurrency tokens (`IsConcurrencyToken()` or
`[ConcurrencyCheck]` on your own column) also work.

## How do transactions and retries work?

Each `SaveChanges` call runs in a transaction, so either all of its batches
commit or none do. To group several operations, start a transaction yourself:

```csharp
await using (var transaction = await db.Database.BeginTransactionAsync())
{
    db.Orders.Add(new Order { Customer = "Fabrikam" });
    await db.SaveChangesAsync();

    await db.Orders
        .Where(o => o.Customer == "Fabrikam")
        .ExecuteUpdateAsync(set => set.SetProperty(o => o.Total, 0m));

    await transaction.CommitAsync();
}
```

Inside your transaction, EF Core creates a savepoint before each
`SaveChanges`. If the save fails, EF Core rolls back to the savepoint, so
earlier work in the transaction is kept and you can still commit.

**Retries are off by default.** BlueTusk does not ship a retrying execution
strategy. To retry on PostgreSQL errors you consider transient, write one and
choose the SQLSTATE codes yourself:

```csharp
public sealed class RetryOnSerializationFailure(ExecutionStrategyDependencies dependencies)
    : ExecutionStrategy(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(2))
{
    // 40001 = serialization_failure, 40P01 = deadlock_detected
    protected override bool ShouldRetryOn(Exception exception)
        => exception is BlueTuskException { SqlState: "40001" or "40P01" };
}
```

Register it with `ExecutionStrategy(...)` (see
[Configuration](configuration.md#bluetusk-options)). With a retrying strategy,
run explicit transactions through the strategy so the whole unit can be
retried:

```csharp
var strategy = db.Database.CreateExecutionStrategy();
await strategy.ExecuteAsync(async () =>
{
    await using var transaction = await db.Database.BeginTransactionAsync();
    db.Orders.Add(new Order { Customer = "Northwind" });
    await db.SaveChangesAsync();
    await transaction.CommitAsync();
});
```

Only retry work that is safe to repeat. If the connection fails during
`COMMIT`, BlueTusk cannot tell whether the commit succeeded.

## How do migrations work?

BlueTusk uses the normal EF Core migrations workflow (`dotnet ef migrations
add`, `database update`, `migrations script`). It adds:

- **PostgreSQL objects in the model.** Enums, domains, composites, ranges,
  extensions, collations, sequences, partitions, triggers, views, functions,
  row-level security policies and publications become migration operations.
  See [model configuration](configuration.md#model-configuration).
- **Identity keys.** Integer keys generated on add become
  `GENERATED BY DEFAULT AS IDENTITY`. Use `UseIdentityColumn(...)` to choose
  `ALWAYS`.
- **A history table** named `__EFMigrationsHistory` (change it with
  `MigrationsHistoryTable`).
- **A migration lock.** Before applying migrations, BlueTusk runs
  `LOCK TABLE "__EFMigrationsHistory" IN ACCESS EXCLUSIVE MODE`. The lock ends
  with the migration transaction, so two processes cannot apply migrations at
  the same time.
- **Database creation.** `database update` creates the target database if it
  is missing. It connects to the `postgres` database to do so (or `template1`
  when the target is `postgres`). Change it with `UseAdminDatabase`.
- **Server version checks.** Features that need a newer PostgreSQL, such as
  virtual generated columns (PostgreSQL 18), are wrapped in a check that stops
  the migration with a clear error on an older server.

For production, generate a reviewed script with
`dotnet ef migrations script --idempotent` and apply it in one deployment step,
using a role allowed to change the schema. Do not let every application
replica migrate at startup.

> **Note:** A data source that maps a PostgreSQL enum or composite with
> `MapEnum` or `MapComposite` needs that type to exist when it first connects.
> Create the type (run the migration) before the application's data source
> opens. See [Troubleshooting](troubleshooting.md#enums-and-types).

## How do I start from an existing database?

Scaffolding (database-first) reads the PostgreSQL catalogue and generates a
`DbContext` and entity classes. You have two options:

- `dotnet ef dbcontext scaffold "<connection string>" BlueTusk.EntityFrameworkCore`,
  in a project that references `BlueTusk.EntityFrameworkCore.Design` and
  `Microsoft.EntityFrameworkCore.Design`.
- `bluetusk scaffold`, from the `BlueTusk.Tool` .NET tool. It does not need a
  project to build first.

Both keep PostgreSQL details such as identity mode, index method, operator
class and collation as fluent calls in the generated `OnModelCreating`.
`bluetusk scaffold` leaves your connection string out of the generated code
unless you pass `--include-connection-string`. `dotnet ef dbcontext scaffold`
writes it into `OnConfiguring` unless you pass `--no-onconfiguring` or use
`Name=ConnectionStrings:<name>`.
See [scaffold command options](configuration.md#scaffold-command-options).

## Next steps

- [Configuration](configuration.md): every option and model method.
- [Troubleshooting](troubleshooting.md): common errors.
- [Full EF Core reference](reference.md): translated functions and operators.
