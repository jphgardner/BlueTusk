# Use BlueTusk with Entity Framework Core

Use this guide when an application already uses EF Core, or when you want LINQ,
change tracking, and migrations on top of BlueTusk's PostgreSQL connection
pool. If you only need SQL commands, start with the
[ADO.NET guide](../ado-net/README.md).

## What you will build

A normal ASP.NET Core application with:

- one application-owned `BlueTuskDataSource`;
- one scoped `DbContext` per unit of work;
- LINQ queries and `SaveChangesAsync`; and
- migrations run as a controlled deployment step.

## 1. Install the provider

Keep all BlueTusk packages on the same exact version:

```powershell
dotnet add package BlueTusk.EntityFrameworkCore
dotnet add package BlueTusk.EntityFrameworkCore.Design
dotnet add package Microsoft.EntityFrameworkCore.Design
```

See [installation](../getting-started/install.md) for stable and preview version
selection.

## 2. Create the model and context

```csharp
using Microsoft.EntityFrameworkCore;

public sealed class Order
{
    public long Id { get; set; }
    public string Customer { get; set; } = "";
    public decimal Total { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class OrdersContext(DbContextOptions<OrdersContext> options)
    : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(order =>
        {
            order.ToTable("orders", "app");
            order.HasKey(x => x.Id);
            order.Property(x => x.Customer).HasMaxLength(200);
            order.Property(x => x.Total).HasPrecision(18, 2);
        });
    }
}
```

## 3. Register it once

The data source is a singleton because it owns the physical connection pool.
The context remains scoped:

```csharp
using BlueTusk.Data;
using Microsoft.EntityFrameworkCore;

builder.Services.AddSingleton(_ =>
    new BlueTuskDataSourceBuilder(
        builder.Configuration.GetConnectionString("PostgreSQL")!)
        .Build());

builder.Services.AddDbContext<OrdersContext>((services, options) =>
    options.UseBlueTusk(services.GetRequiredService<BlueTuskDataSource>()));
```

Do not create a new data source for every request. Doing so creates new pools
instead of reusing healthy PostgreSQL sessions.

## 4. Read and write data

```csharp
app.MapGet("/orders/{id:long}", async (long id, OrdersContext db) =>
    await db.Orders.AsNoTracking().SingleOrDefaultAsync(order => order.Id == id)
        is { } order
        ? Results.Ok(order)
        : Results.NotFound());

app.MapPost("/orders", async (Order order, OrdersContext db) =>
{
    db.Orders.Add(order);
    await db.SaveChangesAsync();
    return Results.Created($"/orders/{order.Id}", order);
});
```

Use `AsNoTracking` for read-only results. Keep a context inside one request or
unit of work; it is not thread-safe.

## 5. Create and apply migrations

```powershell
dotnet ef migrations add InitialCreate
dotnet ef migrations script --idempotent --output artifacts/database.sql
```

Review the SQL and apply it through a deployment job using a migration role.
Do not let every application replica race to migrate the database at startup.

## Verify the setup

Run the repository's executable example when developing BlueTusk itself:

```powershell
$env:BLUETUSK_CONNECTION_STRING = "Host=localhost;Database=app;Username=app;Password=local-only;SSL Mode=Disable;Channel Binding=Disable"
dotnet run --project samples/BlueTusk.Samples.EntityFrameworkCore
```

The TLS-disabled connection is for an isolated local database only.

## Production defaults

- Supply the connection string from the deployment secret store.
- Enable TLS certificate and hostname validation.
- Set explicit command timeouts and a measured maximum pool size.
- Use a least-privilege application role and a separate migration role.
- Log query duration and failure metadata, not parameter values.

## Go deeper only when needed

The [EF Core reference](reference.md) covers PostgreSQL mappings, translated
operators and functions, arrays, migrations, scaffolding, extension packages,
and SQL/PGQ. The [specification-test record](specification-tests.md) is evidence
for provider maintainers rather than required application reading.
