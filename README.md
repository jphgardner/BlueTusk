# BlueTusk

**PostgreSQL, fully exposed to .NET.**

[Website](https://bluetusk.io/) ·
[Quickstart](https://bluetusk.io/documentation/getting-started/quickstart) ·
[Documentation](https://bluetusk.io/documentation) ·
[GitHub](https://github.com/jphgardner/BlueTusk)

BlueTusk is a PostgreSQL-native .NET platform. It provides a direct ADO.NET
provider, EF Core integration, replication, real-time delivery, extension
packages, and PostgreSQL SQL/PGQ support without an Npgsql runtime dependency.

## Choose what you need

| Need | Start with | Add when required |
| --- | --- | --- |
| Connect a .NET application | [`BlueTusk.Data`](docs/ado-net/README.md) | Pooling, authentication, COPY, notifications, or replication |
| Use EF Core | [`BlueTusk.EntityFrameworkCore`](docs/ef-core/README.md) | PostgreSQL-native mappings, migrations, and scaffolding |
| Consume committed changes | [Streams](docs/streams/README.md) | Snapshot bootstrap, replay, and durable relay |
| Update other systems | [Sync](docs/sync/README.md) | Redis, NATS, OpenSearch, Kafka, webhooks, or object storage |
| Push authorized live data | [Live](docs/live/README.md) | ASP.NET transports and browser clients |
| Work with specialized PostgreSQL features | [Extensions](docs/extensions/README.md) | PostGIS, pgvector, TimescaleDB, and other focused packages |
| Query connected data | [Graph](docs/graph/README.md) | SQL/PGQ and Continuous Graph |

The [documentation index](docs/README.md) organizes the complete guide set by
task and audience.

## Install and run a query

BlueTusk `1.0.0` is the current stable line. `1.1.0-rc.1` is available for
prerelease evaluation. Use one exact BlueTusk version throughout an
application; see the [support matrix](VERSIONING.md) before choosing a line.

```powershell
dotnet add package BlueTusk.Data --version 1.1.0-rc.1
```

```csharp
await using var dataSource =
    new BlueTuskDataSourceBuilder(connectionString).Build();

await using var command = dataSource.CreateCommand(
    "SELECT $1::int4 + $2::int4");

command.Parameters.Add(new BlueTuskParameter<int>(20));
command.Parameters.Add(new BlueTuskParameter<int>(22));

var answer = await command.ExecuteScalarAsync<int>();
```

Create one long-lived data source for each distinct configuration. It owns the
connection pool, PostgreSQL type catalogue, and runtime codecs. Replication
uses dedicated unpooled sessions derived from the same configuration.

For a runnable local setup, follow the
[quickstart](docs/getting-started/quickstart.md). For package selection and
stable-versus-RC guidance, use the
[installation guide](docs/getting-started/install.md).

## Build the repository

Prerequisites:

- .NET SDK 10.0.111 or a compatible later feature band
- Docker for PostgreSQL integration tests

```powershell
dotnet restore BlueTusk.slnx
dotnet build BlueTusk.slnx --no-restore
dotnet test BlueTusk.slnx --no-build
```

Integration tests are opt-in:

```powershell
docker compose -f eng/compose/postgres.yml up -d postgres18
$env:BLUETUSK_TEST_CONNECTION_STRING = "Host=localhost;Port=5418;Username=postgres;Password=postgres;Database=bluetusk_tests"
dotnet test tests/BlueTusk.IntegrationTests
```

Extension-specific databases and test commands are documented in
[Testing](docs/contributing/testing.md). The
[repository layout](docs/contributing/repository-layout.md) explains solution
groups, project-registration rules, and generated output.

## Architecture

Dependencies flow toward the protocol and transport layers:

```text
EntityFrameworkCore → Data → Client → Protocol → Transport
                              ↓          ↓
                          TypeSystem   Security

Replication.PgOutput → Replication → Client
```

The real-time products build on committed PostgreSQL changes:

```text
PostgreSQL → Streams → Sync / Live / Continuous Graph
                         ↑
                    Control Plane
```

Read the [architecture overview](docs/architecture/overview.md) for ownership
and dependency rules, or the
[real-time contracts](docs/realtime-platform/contracts.md) for delivery,
checkpoint, and recovery semantics.

## Release status and evidence

The published `1.1.0-rc.1` train contains 62 NuGet packages and three npm
packages from one immutable commit. Stable `1.1.0` remains gated on the exact
candidate’s long-running, security, performance, PostgreSQL 19, and external
acceptance evidence.

- [Compatibility and versioning](VERSIONING.md)
- [1.1.0-rc.1 release record](docs/releases/1.1.0-rc.1.md)
- [Engineering evidence](https://bluetusk.io/evidence)
- [Release process](docs/release-process.md)

Package availability, a successful local build, and stable production approval
are separate claims. BlueTusk records them separately.

## Contributing and security

Read [CONTRIBUTING.md](CONTRIBUTING.md) before opening a change. Report
security issues using the private process in [SECURITY.md](SECURITY.md); do not
open a public issue for a suspected vulnerability.

BlueTusk is licensed under the [MIT License](LICENSE).
