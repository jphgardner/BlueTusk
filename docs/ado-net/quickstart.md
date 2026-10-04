# ADO.NET quick start: a small web API

This quick start helps you build a minimal ASP.NET Core API that creates a
table, inserts rows inside a transaction and reads them back with a data
reader. The data source is registered with dependency injection, the way you
would in a real app. It takes about 10 minutes.

If you have never run BlueTusk before, do the
[5-minute first app](../getting-started/quickstart.md) first. This page builds
on it.

## 1. Check the prerequisites

You need:

- the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0);
- a PostgreSQL 15, 16, 17 or 18 server you can use for testing.

## 2. Start PostgreSQL

If the `bluetusk-postgres` container from the 5-minute app is still running,
skip this step. Otherwise start one with Docker:

```powershell
docker run --name bluetusk-postgres `
  -e POSTGRES_PASSWORD=local-dev-only `
  -p 5432:5432 `
  -d postgres:18
```

In bash, use `\` instead of the backtick to continue lines.

## 3. Create the project

```powershell
dotnet new web --framework net10.0 --name BlueTuskTodo
cd BlueTuskTodo
dotnet add package BlueTusk.Data.DependencyInjection
```

`BlueTusk.Data.DependencyInjection` brings in `BlueTusk.Data`, so one package
is enough. See [Install BlueTusk](../getting-started/install.md) to choose a
release channel or pin a version.

## 4. Write the code

Replace the contents of `Program.cs`:

```csharp
using BlueTusk.Data;
using BlueTusk.Data.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Todo")
    ?? throw new InvalidOperationException("Set ConnectionStrings__Todo first.");

// Register one BlueTuskDataSource for the whole app, plus a health check.
builder.Services.AddDataSource(connectionString);

var app = builder.Build();

// Create the table when the app starts.
var dataSource = app.Services.GetRequiredService<BlueTuskDataSource>();
await using (var create = dataSource.CreateCommand(
    """
    CREATE TABLE IF NOT EXISTS todo_items (
        id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
        title text NOT NULL CHECK (title <> ''),
        done boolean NOT NULL DEFAULT false
    )
    """))
{
    await create.ExecuteNonQueryAsync();
}

// Insert every title in one transaction: all rows are saved, or none are.
app.MapPost("/todos", async (string[] titles, BlueTuskDataSource db, CancellationToken ct) =>
{
    await using var connection = await db.OpenConnectionAsync(ct);
    await using var transaction = await connection.BeginTransactionAsync(ct);

    try
    {
        var ids = new List<long>();
        foreach (var title in titles)
        {
            await using var insert = new BlueTuskCommand(
                "INSERT INTO todo_items (title) VALUES (@title) RETURNING id",
                connection)
            {
                Transaction = transaction,
            };
            insert.Parameters.Add(new BlueTuskParameter<string>(title) { ParameterName = "title" });
            ids.Add(await insert.ExecuteScalarAsync<long>(ct));
        }

        await transaction.CommitAsync(ct);
        return Results.Ok(ids);
    }
    catch (BlueTuskException ex) when (ex.SqlState == "23514") // check_violation
    {
        // Leaving without CommitAsync rolls the transaction back.
        return Results.BadRequest(ex.Message);
    }
});

// Read the rows back with a data reader.
app.MapGet("/todos", async (BlueTuskDataSource db, CancellationToken ct) =>
{
    await using var command = db.CreateCommand(
        "SELECT id, title, done FROM todo_items ORDER BY id");
    await using var reader = await command.ExecuteReaderAsync(ct);

    var items = new List<TodoItem>();
    while (await reader.ReadAsync(ct))
    {
        items.Add(new TodoItem(reader.GetInt64(0), reader.GetString(1), reader.GetBoolean(2)));
    }

    return items;
});

// Report whether the app can reach PostgreSQL.
app.MapHealthChecks("/health");

app.Run();

record TodoItem(long Id, string Title, bool Done);
```

## 5. Run it

Set the connection string and start the app:

```powershell
$env:ConnectionStrings__Todo = "Host=localhost;Port=5432;Username=postgres;Password=local-dev-only;Database=postgres;SSL Mode=Disable;Channel Binding=Disable"
dotnet run --urls http://localhost:5050
```

In bash, use `export ConnectionStrings__Todo="..."`.

> **Warning:** `SSL Mode=Disable` is only for a local test container. Keep the
> default, `SSL Mode=VerifyFull`, for every other server.

## 6. Call the API

Open a second terminal and add two items in one transaction:

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:5050/todos `
  -ContentType application/json -Body '["Buy milk","Write docs"]'
Invoke-RestMethod http://localhost:5050/todos
```

You should see the new IDs, then the rows:

```text
1
2

id title       done
-- -----       ----
 1 Buy milk   False
 2 Write docs False
```

Now send a batch where the second title breaks the `CHECK` constraint:

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:5050/todos `
  -ContentType application/json -Body '["Walk the dog",""]'
```

The request fails with `400 Bad Request` and this message:

```text
new row for relation "todo_items" violates check constraint "todo_items_title_check"
```

Run `Invoke-RestMethod http://localhost:5050/todos` again. "Walk the dog" is
not there, because the transaction was rolled back. Finally, check the health
endpoint:

```powershell
Invoke-RestMethod http://localhost:5050/health
```

```text
Healthy
```

With bash, use `curl`:

```bash
curl -X POST http://localhost:5050/todos -H "Content-Type: application/json" -d '["Buy milk","Write docs"]'
curl http://localhost:5050/todos
curl http://localhost:5050/health
```

`curl http://localhost:5050/todos` prints JSON:
`[{"id":1,"title":"Buy milk","done":false},{"id":2,"title":"Write docs","done":false}]`.

## What just happened

- `AddDataSource` registered one `BlueTuskDataSource` as a singleton. The same
  instance is also available as `DbDataSource`. It owns the connection pool.
  It also added a health check named `bluetusk`, which runs `SELECT 1`.
- `POST /todos` opened a connection, started a transaction and enlisted each
  command in it with `Transaction = transaction`. A command on a connection
  with an open transaction must be enlisted, or BlueTusk throws.
- The `CHECK` failure arrived as a `BlueTuskException` with
  `SqlState == "23514"`. The handler returned without calling `CommitAsync`,
  so disposing the transaction rolled it back.
- `GET /todos` used `dataSource.CreateCommand(...)`, which borrows a pooled
  connection only while the command runs.

## Clean up

Stop the app with Ctrl+C, then drop the table or remove the container:

```powershell
docker exec bluetusk-postgres psql -U postgres -c "DROP TABLE todo_items"
docker rm --force bluetusk-postgres
```

## Next steps

- [Concepts](concepts.md): connections, commands, transactions and pooling.
- [Dependency injection and health checks](dependency-injection.md): probes
  and registration options.
- [Configuration](configuration.md): every connection-string keyword.
- [Batches](batches.md): send several statements in one round trip.
- [Troubleshooting](troubleshooting.md): common errors and their fixes.
