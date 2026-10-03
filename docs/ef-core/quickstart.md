# EF Core quick start

In this quick start you build a .NET console app that uses EF Core with
PostgreSQL through BlueTusk. You define two entities, create the database with
an EF Core migration, save data and query it with LINQ. It takes about ten
minutes.

## Before you start

You need:

- the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0);
- a PostgreSQL 15, 16, 17 or 18 server you can use for testing. If you have
  Docker, step 1 starts one for you.

## 1. Start PostgreSQL

Skip this step if you already have a test server.

```powershell
docker run --name bluetusk-postgres `
  -e POSTGRES_PASSWORD=local-dev-only `
  -p 5432:5432 `
  -d postgres:18
```

You do not need to create a database. The migration in step 6 creates the
`library` database for you.

## 2. Create the app and add packages

```powershell
dotnet new console --framework net10.0 --name LibraryApp
cd LibraryApp
dotnet add package BlueTusk.EntityFrameworkCore
dotnet add package BlueTusk.EntityFrameworkCore.Design
dotnet add package Microsoft.EntityFrameworkCore.Design --version 10.0.11
dotnet add package Microsoft.Extensions.Hosting
```

| Package | Why |
| --- | --- |
| `BlueTusk.EntityFrameworkCore` | The EF Core provider. |
| `BlueTusk.EntityFrameworkCore.Design` | Lets `dotnet ef` create migrations for PostgreSQL. |
| `Microsoft.EntityFrameworkCore.Design` | Required by `dotnet ef`. Version 10.0.11 matches the provider. |
| `Microsoft.Extensions.Hosting` | Dependency injection and configuration. `dotnet ef` also uses it to find your `DbContext`. |

See [Install BlueTusk](../getting-started/install.md) to choose a release
channel for the BlueTusk packages.

## 3. Install the EF Core command-line tool

```powershell
dotnet tool install --global dotnet-ef --version 10.0.11
```

If you already have `dotnet-ef`, run
`dotnet tool update --global dotnet-ef --version 10.0.11` instead.

## 4. Set the connection string

Keep credentials out of source code. .NET reads the environment variable
`ConnectionStrings__Library` as the connection string named `Library`:

```powershell
$env:ConnectionStrings__Library = "Host=localhost;Port=5432;Username=postgres;Password=local-dev-only;Database=library;SSL Mode=Disable;Channel Binding=Disable"
```

On Linux or macOS, use `export ConnectionStrings__Library="..."` instead.

> **Warning:** `SSL Mode=Disable` is only for a local test container.
> BlueTusk's default is `SSL Mode=VerifyFull`, which requires TLS and validates
> the server certificate. Keep that default everywhere else.

## 5. Write the code

Create `Library.cs` with the entities and the `DbContext`:

```csharp
using Microsoft.EntityFrameworkCore;

public sealed class Author
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public List<Book> Books { get; } = [];
}

public sealed class Book
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public int Published { get; set; }
    public int AuthorId { get; set; }
    public Author Author { get; set; } = null!;
}

public sealed class LibraryContext(DbContextOptions<LibraryContext> options)
    : DbContext(options)
{
    public DbSet<Author> Authors => Set<Author>();
    public DbSet<Book> Books => Set<Book>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Author>().Property(author => author.Name).HasMaxLength(200);
        modelBuilder.Entity<Book>().Property(book => book.Title).HasMaxLength(300);
    }
}
```

Replace the contents of `Program.cs`:

```csharp
using BlueTusk.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Library")
    ?? throw new InvalidOperationException("Set ConnectionStrings__Library first.");

// One data source for the whole application: it owns the connection pool.
builder.Services.AddSingleton(_ => new BlueTuskDataSourceBuilder(connectionString).Build());

// One DbContext per unit of work, using the shared data source.
builder.Services.AddDbContext<LibraryContext>((services, options) =>
    options.UseBlueTusk(services.GetRequiredService<BlueTuskDataSource>()));

using var host = builder.Build();

// Write: one SaveChangesAsync call inserts the author and both books.
await using (var scope = host.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LibraryContext>();

    var author = new Author { Name = "Ursula K. Le Guin" };
    author.Books.Add(new Book { Title = "A Wizard of Earthsea", Published = 1968 });
    author.Books.Add(new Book { Title = "The Left Hand of Darkness", Published = 1969 });
    db.Authors.Add(author);

    await db.SaveChangesAsync();
    Console.WriteLine($"Saved author {author.Id} with {author.Books.Count} books.");
}

// Read: a LINQ query that PostgreSQL runs as one SELECT with a join.
await using (var scope = host.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LibraryContext>();

    var books = await db.Books
        .AsNoTracking()
        .Where(book => book.Published < 1970)
        .OrderBy(book => book.Published)
        .Select(book => new { book.Title, book.Published, Author = book.Author.Name })
        .ToListAsync();

    foreach (var book in books)
    {
        Console.WriteLine($"{book.Published}: {book.Title} by {book.Author}");
    }
}
```

## 6. Create the database with a migration

Create a migration from your model, then apply it:

```powershell
dotnet ef migrations add InitialCreate
dotnet ef database update
```

`migrations add` writes C# files to a `Migrations` folder. `database update`
creates the `library` database if it does not exist, creates the `Authors` and
`Books` tables, and records the migration in `__EFMigrationsHistory`. The `Id`
columns become `GENERATED BY DEFAULT AS IDENTITY` columns.

To review the SQL before applying it, run `dotnet ef migrations script`.

## 7. Run it

```powershell
dotnet run
```

The host logs every SQL command at the `Information` level. Among the log lines
you should see:

```text
      INSERT INTO "Books" ("AuthorId", "Published", "Title")
      VALUES (@p1, @p2, @p3)
      RETURNING "Id";
      INSERT INTO "Books" ("AuthorId", "Published", "Title")
      VALUES (@p4, @p5, @p6)
      RETURNING "Id";
Saved author 1 with 2 books.
...
1968: A Wizard of Earthsea by Ursula K. Le Guin
1969: The Left Hand of Darkness by Ursula K. Le Guin
```

Each run adds the author and books again, so later runs print more rows.

## What just happened

- The **data source** (`BlueTuskDataSource`) is a singleton that owns the
  connection pool. Every `LibraryContext` borrows a connection from it.
- `AddDbContext` registers `LibraryContext` as **scoped**: each scope gets its
  own context, used for one unit of work.
- `SaveChangesAsync` inserted the author first (the books need its generated
  `Id`), then sent both book inserts in **one batched command**. Batching is
  new in 1.1.0; see [Concepts](concepts.md#how-does-savechanges-batch-commands).
- The LINQ query was translated to a single PostgreSQL `SELECT` with an
  `INNER JOIN`.

## If it fails

| Error | Fix |
| --- | --- |
| `Set ConnectionStrings__Library first.` | Set the environment variable in the same terminal (step 4). `dotnet ef` needs it too. |
| `Could not load file or assembly 'BlueTusk.EntityFrameworkCore.Design'` | Add the `BlueTusk.EntityFrameworkCore.Design` package (step 2). |
| `The Entity Framework tools version '...' is older than that of the runtime '10.0.11'` | Update `dotnet-ef` to 10.0.11 (step 3). |
| Connection refused or timeout | Check that PostgreSQL is running and that `Host` and `Port` are correct. |

See [Troubleshooting](troubleshooting.md) for more.

## Clean up

Drop the database and remove the container:

```powershell
dotnet ef database drop --force
docker rm --force bluetusk-postgres
```

## Next steps

- [Concepts](concepts.md): lifetimes, type mapping, batching, concurrency and
  migrations.
- [Configuration](configuration.md): every `UseBlueTusk` option and model
  configuration method.
- [Full EF Core reference](reference.md): PostgreSQL functions, operators and
  migration features.
