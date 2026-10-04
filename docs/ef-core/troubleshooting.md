# EF Core troubleshooting

This page helps you fix common errors with the BlueTusk EF Core provider. Find
the message or symptom you see, then apply the fix. For connection, TLS and
authentication errors, see the
[provider troubleshooting guide](../operations/troubleshooting.md).

## Setup

### "No database provider has been configured for this DbContext"

**Cause:** `UseBlueTusk` was never called for this context, or the context's
constructor does not pass `DbContextOptions<TContext>` to the base class.

**Fix:** Register the context with
`AddDbContext<TContext>((services, options) => options.UseBlueTusk(...))` and
give it a constructor such as
`MyContext(DbContextOptions<MyContext> options) : DbContext(options)`.

### "A connection string is required."

**Cause:** `UseBlueTusk` received a null or empty connection string, usually
because a configuration value or environment variable is not set.

**Fix:** Check the configuration key. For example, `GetConnectionString("Library")`
reads `ConnectionStrings:Library`, which the environment variable
`ConnectionStrings__Library` can supply.

### PostgreSQL runs out of connections, or every request opens a new one

**Cause:** The context uses `UseBlueTusk(connectionString)`, which opens an
unpooled connection for each context, or the app builds a new data source per
request.

**Fix:** Build one `BlueTuskDataSource` as a singleton and pass it to
`UseBlueTusk`. See
[Concepts](concepts.md#how-long-should-the-data-source-and-dbcontext-live).

## dotnet ef and package versions

### "Your startup project '...' doesn't reference Microsoft.EntityFrameworkCore.Design."

**Fix:** `dotnet add package Microsoft.EntityFrameworkCore.Design --version 10.0.11`.
`BlueTusk.EntityFrameworkCore.Design` does not add it for you.

### "Could not load file or assembly 'BlueTusk.EntityFrameworkCore.Design'"

**Cause:** `dotnet ef` found the BlueTusk provider but not its design-time
package.

**Fix:** `dotnet add package BlueTusk.EntityFrameworkCore.Design` in the
startup project, using the same version as `BlueTusk.EntityFrameworkCore`.

### "The Entity Framework tools version '...' is older than that of the runtime '10.0.11'."

**Fix:** `dotnet tool update --global dotnet-ef --version 10.0.11` (or
`dotnet tool update dotnet-ef --version 10.0.11` for a local tool).

### "NU1605: Detected package downgrade: Microsoft.EntityFrameworkCore.Relational from 10.0.11 to ..."

**Cause:** Your project references an EF Core package older than 10.0.11.
The provider needs EF Core 10.0.11 or later in the 10.0 line.

**Fix:** Reference version 10.0.11 for every `Microsoft.EntityFrameworkCore.*`
package. EF Core 9 and earlier are not supported.

### "Unable to retrieve project metadata. Ensure it's an SDK-style project."

**Cause:** The project was never restored, so `obj/project.assets.json` is
missing.

**Fix:** Run `dotnet build` once, then run the `dotnet ef` command again.

## Queries

### "The LINQ expression '...' could not be translated."

**Cause:** The query calls a .NET method that EF Core and BlueTusk cannot
turn into SQL, such as your own helper method.

**Fix:** Rewrite the condition with translatable members, or use a PostgreSQL
function from `EF.Functions` (see the [full reference](reference.md)). If the
rest of the work must run in .NET, filter in SQL first, then call
`AsEnumerable()` and finish in memory.

### "BlueTusk PostgreSQL database functions can only be used in translated EF Core queries."

**Cause:** An `EF.Functions` method from BlueTusk ran as ordinary .NET code,
outside a query, or in a part of the query EF Core evaluates on the client.

**Fix:** Use these methods only inside LINQ queries that go to the database.

## Enums and types

### "'Pending' is not a catalogue label for PostgreSQL enum app.order_status."

**Cause:** BlueTusk sends each enum member's CLR name as its label, but the
PostgreSQL enum uses different labels (for example lower-case).

**Fix:** Give each member its PostgreSQL label with `[BlueTuskName("pending")]`
or `[EnumMember(Value = "pending")]`, or pass a `labels` dictionary to
`MapEnum`. See
[Concepts](concepts.md#postgresql-enums-need-three-pieces).

### "invalid input value for enum app.order_status" in a query with an enum constant

**Cause:** A LINQ query compares with an enum constant, such as
`o.Status == OrderStatus.Shipped`, and the enum's PostgreSQL labels are set
only in the `labels` dictionary of `MapEnum`. BlueTusk writes a constant into
the SQL with the label from `[BlueTuskName]`, then `[EnumMember]`, then the CLR
member name. It cannot see the `labels` dictionary, so it sends `'Shipped'`.

**Fix:** Put the labels on the enum members with `[BlueTuskName("shipped")]`
or `[EnumMember(Value = "shipped")]`. Or compare with a variable, which is sent
as a parameter and uses the data source's labels:

```csharp
var status = OrderStatus.Shipped;
var orders = await db.Orders
    .Where(o => o.Status == status)
    .ToListAsync();
```

### "PostgreSQL type OID ... requires a registered codec or string/byte payload."

**Cause:** A property is mapped to a PostgreSQL enum or composite with
`HasColumnType`, but the data source has no `MapEnum` or `MapComposite` for it.

**Fix:** Add the mapping to the data source builder, and pass that data
source to `UseBlueTusk`.

### "PostgreSQL type app.order_status is not present in the loaded type catalogue."

**Cause:** The data source loaded its type catalogue before the type existed,
and nothing reloaded it. `Migrate` and `MigrateAsync` reload the catalogue of
the data source they run on, but only in their own process. So this happens
when the type was created while the application was already running: by a SQL
script, by `dotnet ef database update` or another process's migration, or by
`ExecuteSqlRaw`. The message comes from an `InvalidOperationException`, often
inside a `DbUpdateException`.

**Fix:** Call `await dataSource.ReloadTypesAsync()` on the application's data
source after the type is created, or restart the application. Also check that
the schema and name in `MapEnum` or `MapComposite` match the database.

### Values read back as `DateTimeKind.Unspecified`

**Cause:** `DateTime` maps to `timestamp without time zone`, which stores no
time zone.

**Fix:** Use `DateTimeOffset` (`timestamp with time zone`) for points in
time. See [Concepts](concepts.md#how-are-net-types-mapped-to-postgresql).

## Saving

### DbUpdateConcurrencyException: "The database operation was expected to affect 1 row(s), but actually affected 0 row(s)"

**Cause:** Another transaction changed or deleted the row after you read it,
and the entity has a concurrency token such as `UseXminConcurrencyToken()`.

**Fix:** Catch the exception, reload or merge the entries in
`conflict.Entries`, and save again. See
[Concepts](concepts.md#how-do-i-detect-concurrent-updates).

### DbUpdateException: "An error occurred while saving the entity changes."

**Cause:** PostgreSQL rejected a statement. The inner exception is a
`BlueTuskException`.

**Fix:** Read `((BlueTuskException)exception.InnerException).SqlState`. For
example, `23505` is a unique violation and `23503` a foreign key violation.
Clear or fix the failed entries before you save again.

### SaveChanges behaves differently after upgrading to 1.1.0

**Cause:** **New in 1.1.0**, `SaveChanges` sends up to 42 statements in one
command. In 1.0.0 and 1.1.0-rc.1 each statement was its own command. You may
notice that:

- logs show several `INSERT`, `UPDATE` or `DELETE` statements in one
  "Executed DbCommand" entry;
- a `DbCommandInterceptor` is called once per batch, not once per entity;
- `DbUpdateException.Entries` lists every entry in the failing batch.

**Fix:** Update code that counted commands or expected one entry per error.
To restore the old behavior while you investigate, set
`provider => provider.MaxBatchSize(1)`.

### "The configured execution strategy '...' does not support user-initiated transactions."

**Cause:** A retrying execution strategy is configured, and the code calls
`BeginTransaction` directly.

**Fix:** Run the whole transaction inside
`db.Database.CreateExecutionStrategy().ExecuteAsync(...)`. See
[Concepts](concepts.md#how-do-transactions-and-retries-work).

## Migrations

### "BlueTusk virtual generated columns require PostgreSQL 18 or later."

**Cause:** The migration uses a feature that the connected server does not
have, such as virtual generated columns (PostgreSQL 18) or `NOT ENFORCED`
check constraints (PostgreSQL 18). Similar messages name other features and
versions. BlueTusk checks the server version and stops the migration with
SQLSTATE `0A000` before it runs the statement.

**Fix:** Upgrade the server, or change the model (for example use
`stored: true` for a generated column) and add a new migration.

### `database update` waits after "Acquiring an exclusive lock for migration application."

**Cause:** Another process is applying migrations and holds a lock on
`__EFMigrationsHistory`, or an idle open transaction is holding a lock on it.

**Fix:** Let the other process finish. Check `pg_stat_activity` for sessions
that are `idle in transaction`. Apply migrations from one deployment step, not
from every replica.

### `database update` or `database drop` cannot connect to create or drop the database

**Cause:** To create or drop the target database, BlueTusk connects to the
`postgres` database (or `template1` when the target is `postgres`). Your role
may not be allowed to connect there, or may lack `CREATEDB`.

**Fix:** Create the database yourself, or point BlueTusk at a database you
can use with `UseAdminDatabase("maintenance")`.

## Still stuck?

- [Configuration](configuration.md) lists every option and its default.
- The [full EF Core reference](reference.md) covers each translated function
  and migration operation.
