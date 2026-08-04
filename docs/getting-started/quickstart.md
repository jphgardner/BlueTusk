# Quickstart: run BlueTusk locally

This guide takes a new contributor from a clean checkout to a live,
parameterized PostgreSQL query. BlueTusk packages are not presented as publicly
available stable packages yet, so the supported evaluation path is a source
checkout.

## Prerequisites

- .NET SDK selected by `global.json`
- Docker Desktop or another Docker Compose-compatible runtime
- Git
- PowerShell 7 for the repository engineering scripts

Node.js is required only when building the Angular documentation website.

## Clone and build

```powershell
git clone https://github.com/jphgardner/BlueTusk.git
cd BlueTusk
dotnet restore BlueTusk.slnx
dotnet build BlueTusk.slnx --configuration Release --no-restore
```

A successful Release build has zero warnings. The solution contains product
projects, tests, samples, smoke applications and tooling; the two embedded
template projects are intentionally outside the main solution build.

## Start PostgreSQL

The repository compose file exposes PostgreSQL versions on predictable local
ports. Start PostgreSQL 18 for a general evaluation:

```powershell
docker compose -f eng/compose/postgres.yml up -d --wait postgres18
$env:BLUETUSK_TEST_CONNECTION_STRING =
  "Host=localhost;Port=5418;Username=postgres;Password=postgres;Database=bluetusk_tests;SSL Mode=Disable;Channel Binding=Disable"
```

These credentials belong only to the isolated local test container. Do not
copy them into an application deployment.

## Run the ADO.NET sample

```powershell
dotnet run `
  --project samples/BlueTusk.Samples.AdoNet/BlueTusk.Samples.AdoNet.csproj `
  --configuration Release
```

The sample exercises the provider owned by this repository. BlueTusk does not
delegate connections or command execution to Npgsql.

## Write the smallest application

Reference the Provider projects from a local application or work inside a
repository sample. Prefer a long-lived data source and short-lived logical
connections:

```csharp
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

The data source owns pooling and provider-wide type configuration. Disposing a
logical connection returns its healthy physical session to the pool. Do not
create one data source per request.

## Use dependency injection

The `BlueTusk.Data.DependencyInjection` package registers the same long-lived
data source as both `BlueTuskDataSource` and provider-neutral `DbDataSource`:

```csharp
services.AddDataSource(
    configuration.GetConnectionString("PostgreSQL")!,
    builder => builder.ConfigureDiagnostics(diagnostics),
    healthCheckName: "postgresql");
```

The optional readiness check opens a connection and executes `SELECT 1`. Read
[dependency injection](../ado-net/dependency-injection.md) before selecting
health-check frequency and timeout.

## Choose the next layer

- Use [ADO.NET](../ado-net/README.md) for direct command, COPY, notification,
  large-object or replication control.
- Use [EF Core](../ef-core/README.md) for LINQ, migrations, scaffolding and
  model-driven PostgreSQL features.
- Use [extensions](../extensions/README.md) for PostGIS, pgvector,
  TimescaleDB, citext, hstore, ltree or pg_trgm.
- Use [Streams](../streams/README.md) when the source of truth is committed WAL
  and the consumer needs acknowledgement and checkpoints.

## Run focused verification

```powershell
dotnet test tests/BlueTusk.Data.Tests/BlueTusk.Data.Tests.csproj `
  --configuration Release `
  --no-build

dotnet test tests/BlueTusk.CompatibilityTests/BlueTusk.CompatibilityTests.csproj `
  --configuration Release `
  --no-build
```

Live tests read `BLUETUSK_TEST_CONNECTION_STRING`. A missing optional service
causes its specifically scoped test to skip; that skip does not satisfy the
service’s dedicated CI gate.

## Shut down

```powershell
docker compose -f eng/compose/postgres.yml down
```

Add `--volumes` only when you intentionally want to remove the compose
environment’s test data.

## Common first-run failures

| Symptom | Meaning | Action |
| --- | --- | --- |
| SDK selection failure | `global.json` SDK is not installed | Install the selected .NET SDK |
| Connection refused on `5418` | PostgreSQL container is not healthy or port is occupied | Inspect `docker compose ps` and container logs |
| Live tests skip | Connection environment variable is absent | Set it in the same shell that launches `dotnet test` |
| TLS or channel-binding error | Local connection string inherited production security settings | Use the explicit local test connection string above |
| Package restore audit failure | A dependency advisory matched | Treat it as a release blocker; do not suppress it casually |

Continue with [core concepts](concepts.md) before designing application
lifetimes or a real-time topology.
