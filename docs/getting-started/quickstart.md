# Quickstart: run the first query

This guide creates a .NET console application, connects it to PostgreSQL, and
runs one parameterized query. It uses the published `1.1.0-rc.1` package; use
`1.0.0` instead if you require the stable channel.

## Prerequisites

- .NET 10 SDK
- PostgreSQL 15, 16, 17, or 18
- a database and credentials you may use for this test

See [Install BlueTusk](install.md) for the complete compatibility and package
selection guidance.

## 1. Create the application

```powershell
dotnet new console --framework net10.0 --name BlueTuskQuickstart
Set-Location BlueTuskQuickstart
dotnet add package BlueTusk.Data --version 1.1.0-rc.1
```

Keep all BlueTusk dependencies on the same exact version. Do not mix stable
and release-candidate packages.

## 2. Set the connection string

Use an environment variable so credentials do not enter source control:

```powershell
$env:BLUETUSK_CONNECTION_STRING =
  "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=bluetusk;SSL Mode=Disable;Channel Binding=Disable"
```

That example disables TLS only for an isolated local PostgreSQL instance. Use
TLS and appropriately scoped credentials outside local development.

## 3. Replace `Program.cs`

```csharp
using BlueTusk.Data;

var connectionString =
    Environment.GetEnvironmentVariable("BLUETUSK_CONNECTION_STRING")
    ?? throw new InvalidOperationException(
        "Set BLUETUSK_CONNECTION_STRING before running the application.");

await using var dataSource =
    new BlueTuskDataSourceBuilder(connectionString).Build();

await using var connection = await dataSource.OpenConnectionAsync();
await using var command = connection.CreateCommand();

command.CommandText = "SELECT @left::int4 + @right::int4";
command.Parameters.Add(new BlueTuskParameter<int>("left", 20));
command.Parameters.Add(new BlueTuskParameter<int>("right", 22));

var answer = await command.ExecuteScalarAsync<int>();
Console.WriteLine(answer);
```

The parameters travel through PostgreSQL protocol binding; their values are
not interpolated into SQL.

## 4. Run it

```powershell
dotnet run
```

The application should print:

```text
42
```

## Understand the ownership model

`BlueTuskDataSource` owns configuration, PostgreSQL type metadata, and the
physical connection pool. Create one long-lived data source for each distinct
connection configuration. Open and dispose short-lived logical connections as
work arrives; healthy physical sessions return to the pool.

Do not create a data source per request.

## Choose the next guide

- [ADO.NET provider](../ado-net/README.md): commands, transactions, batches,
  COPY, notifications, large objects, and replication.
- [Dependency injection](../ado-net/dependency-injection.md): register the data
  source and a readiness check in a hosted application.
- [EF Core](../ef-core/README.md): use LINQ, migrations, scaffolding, and
  PostgreSQL-native mappings.
- [Extensions](../extensions/README.md): add PostGIS, pgvector, TimescaleDB,
  and other focused packages.
- [Streams](../streams/README.md): consume committed PostgreSQL changes with
  acknowledgement and checkpoints.
- [Production checklist](../operations/production-checklist.md): prepare a
  secure, bounded, observable, and recoverable deployment.

## Build the repository instead

The package quickstart above is the normal application path. Contributors can
build and run the repository sample directly:

```powershell
git clone https://github.com/jphgardner/BlueTusk.git
Set-Location BlueTusk
dotnet restore BlueTusk.slnx
dotnet build BlueTusk.slnx --configuration Release --no-restore

docker compose -f eng/compose/postgres.yml up -d --wait postgres18
$env:BLUETUSK_CONNECTION_STRING =
  "Host=localhost;Port=5418;Username=postgres;Password=postgres;Database=bluetusk_tests;SSL Mode=Disable;Channel Binding=Disable"

dotnet run `
  --project samples/BlueTusk.Samples.AdoNet/BlueTusk.Samples.AdoNet.csproj `
  --configuration Release
```

The repository credentials are restricted to the disposable local test
database. Stop it with:

```powershell
docker compose -f eng/compose/postgres.yml down
```

Add `--volumes` only when you intentionally want to remove its test data.

If the first run fails, use the [troubleshooting guide](../operations/troubleshooting.md)
and include the BlueTusk version, PostgreSQL version, and smallest reproducer
when reporting a defect.
