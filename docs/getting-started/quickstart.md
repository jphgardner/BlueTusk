# 5-minute first app

In this quick start you create a .NET console app, connect it to PostgreSQL and
run a parameterized query. It takes about five minutes.

## Before you start

You need:

- the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0);
- a PostgreSQL 15, 16, 17 or 18 server you can use for testing. If you have
  Docker, step 1 starts one for you.

## 1. Start PostgreSQL

Skip this step if you already have a test database.

```powershell
docker run --name bluetusk-postgres `
  -e POSTGRES_PASSWORD=local-dev-only `
  -p 5432:5432 `
  -d postgres:18 `
  -c wal_level=logical
```

`wal_level=logical` is not needed for this quick start. It lets you reuse the
same container for the [Streams guide](../streams/README.md) later.

## 2. Create the app

```powershell
dotnet new console --framework net10.0 --name BlueTuskQuickstart
cd BlueTuskQuickstart
dotnet add package BlueTusk.Data --version 1.1.0-rc.1
```

`1.1.0-rc.1` is the latest public release candidate. Use `1.0.0` if you need
the stable release. See [Install BlueTusk](install.md) to choose.

## 3. Set the connection string

Keep credentials out of source code by using an environment variable:

```powershell
$env:BLUETUSK_CONNECTION_STRING = "Host=localhost;Port=5432;Username=postgres;Password=local-dev-only;Database=postgres;SSL Mode=Disable;Channel Binding=Disable"
```

On Linux or macOS, use `export BLUETUSK_CONNECTION_STRING="..."` instead.

> **Warning:** `SSL Mode=Disable` is only for a local test container.
> BlueTusk's default is `SSL Mode=VerifyFull`, which requires TLS and validates
> the server certificate. Keep that default everywhere else.

## 4. Write the code

Replace the contents of `Program.cs`:

```csharp
using BlueTusk.Data;

var connectionString =
    Environment.GetEnvironmentVariable("BLUETUSK_CONNECTION_STRING")
    ?? throw new InvalidOperationException("Set BLUETUSK_CONNECTION_STRING first.");

// Create one data source for the lifetime of the application.
await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();

// Create a command, bind two typed parameters, and run it.
await using var command = dataSource.CreateCommand("SELECT @left::int4 + @right::int4");
command.Parameters.Add(new BlueTuskParameter<int>(20) { ParameterName = "left" });
command.Parameters.Add(new BlueTuskParameter<int>(22) { ParameterName = "right" });

var answer = await command.ExecuteScalarAsync<int>();
Console.WriteLine($"The answer is {answer}");
```

## 5. Run it

```powershell
dotnet run
```

You should see:

```text
The answer is 42
```

## What just happened

- `BlueTuskDataSourceBuilder.Build()` created a **data source**. It owns the
  configuration, the connection pool and the PostgreSQL type catalogue. Create
  one per connection string and keep it for the life of the app.
- `dataSource.CreateCommand(...)` created a command that borrows a pooled
  connection when it runs and returns it afterwards.
- The parameter values were sent separately from the SQL text. They are never
  pasted into the SQL, so this pattern is safe from SQL injection.

## If it fails

| Error | Fix |
| --- | --- |
| Connection refused or timeout | Check that PostgreSQL is running and that `Host` and `Port` are correct. |
| TLS or certificate error | For a local container only, keep `SSL Mode=Disable`. For a real server, configure TLS. |
| Password authentication failed | Check `Username` and `Password`. |

The [provider troubleshooting guide](../operations/troubleshooting.md) covers more
cases.

## Clean up

```powershell
docker rm --force bluetusk-postgres
```

## Next steps

- [Core concepts](concepts.md): the vocabulary every BlueTusk product uses.
- [ADO.NET guide](../ado-net/README.md): transactions, batches, COPY and more.
- [EF Core guide](../ef-core/README.md): LINQ and migrations.
- [Streams guide](../streams/README.md): react to committed changes.
