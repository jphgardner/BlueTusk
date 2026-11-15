# Run your first query

Create a .NET console application and send two bound parameters to
PostgreSQL. The application prints `42`.

## 1. Prepare a database

You’ll need the .NET 10 SDK and a PostgreSQL 15–18 database you can use for
development. If you have Docker, create a disposable PostgreSQL 18 instance
bound only to your machine:

```sh
docker run -d --name bluetusk-quickstart -p 127.0.0.1:5432:5432 -e POSTGRES_USER=quickstart -e POSTGRES_PASSWORD=local-example-only -e POSTGRES_DB=quickstart postgres:18
docker exec bluetusk-quickstart pg_isready -U quickstart -d quickstart
```

Repeat the readiness check until it reports `accepting connections`.
Use different credentials and certificate verification when connecting to a
shared PostgreSQL server.

## 2. Create the application

```sh
dotnet new console --framework net10.0 --name BlueTuskQuickstart
cd BlueTuskQuickstart
dotnet add package BlueTusk.Data --version 1.2.0
```

## 3. Set the connection string

For the disposable Docker instance, run one of these commands in the shell
you will use to start the application.

**Bash or Zsh**

```sh
export BLUETUSK_CONNECTION_STRING='Host=127.0.0.1;Port=5432;Database=quickstart;Username=quickstart;Password=local-example-only;SSL Mode=Disable;Channel Binding=Disable'
```

**PowerShell**

```powershell
$env:BLUETUSK_CONNECTION_STRING = 'Host=127.0.0.1;Port=5432;Database=quickstart;Username=quickstart;Password=local-example-only;SSL Mode=Disable;Channel Binding=Disable'
```

TLS is disabled for this local example. Use the
[authentication guide](../ado-net/authentication.md) for a shared server.

## 4. Replace Program.cs

```csharp
using BlueTusk.Data;

var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_CONNECTION_STRING")
    ?? throw new InvalidOperationException("Set BLUETUSK_CONNECTION_STRING first.");

await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();
await using var command = dataSource.CreateCommand("SELECT $1::int4 + $2::int4");

command.Parameters.Add(new BlueTuskParameter<int>(20));
command.Parameters.Add(new BlueTuskParameter<int>(22));

Console.WriteLine(await command.ExecuteScalarAsync<int>());
```

Parameters travel separately from the command text. Use the same mechanism
for values that come from HTTP requests or other untrusted sources.

## 5. Run and verify

```sh
dotnet run
```

Expected output:

```text
42
```

If the connection fails, confirm the server is listening on the configured
port and check the database, role, password, and TLS settings. See
[troubleshooting](../operations/troubleshooting.md) for common error messages.

If you started the disposable database, stop and remove that container after
the exercise with `docker rm -f bluetusk-quickstart`.

## Build your next feature

Create one long-lived data source per connection configuration in a hosted
application. Open short-lived logical connections as work arrives. See
[dependency injection](../ado-net/dependency-injection.md) to register the
data source, [ADO.NET](../ado-net/README.md) for commands and transactions,
or [EF Core](../ef-core/README.md) to start with a DbContext.
