# BlueTusk

**PostgreSQL, fully exposed to .NET.**

BlueTusk is a ground-up PostgreSQL provider ecosystem for .NET. Its long-term scope includes a native wire-protocol engine, ADO.NET, replication, Entity Framework Core, extension packages, and PostgreSQL SQL/PGQ support—without a runtime dependency on Npgsql.

> [!IMPORTANT]
> BlueTusk is an experimental pre-release provider. Version 0.0.4 can connect with TLS and SCRAM-SHA-256 and execute buffered, parameterized, transactional, and cancellable queries through ADO.NET, but it does not yet support pooling, prepared statements, batches, or production workloads. Track implemented scope in the [roadmap](docs/roadmap.md).

## Build

Prerequisites:

- .NET SDK 10.0.110 or a compatible later feature band
- Docker, only for PostgreSQL integration tests

```powershell
dotnet restore BlueTusk.slnx
dotnet build BlueTusk.slnx --no-restore
dotnet test BlueTusk.slnx --no-build
```

Integration tests are opt-in. Start one of the test databases and set `BLUETUSK_TEST_CONNECTION_STRING` before running the integration suite.

```powershell
docker compose -f eng/compose/postgres.yml up -d postgres18
$env:BLUETUSK_TEST_CONNECTION_STRING = "Host=localhost;Port=5418;Username=postgres;Password=postgres;Database=bluetusk_tests"
dotnet test tests/BlueTusk.IntegrationTests
```

## Architecture

The dependency direction is deliberately one-way:

```text
EntityFrameworkCore → Data → Client → Protocol → Transport
                              ↓          ↓
                          TypeSystem   Security
```

See [Architecture](docs/architecture/overview.md), [ADRs](docs/architecture/decisions), and [Contributing](CONTRIBUTING.md).

## Status

The current `0.0.4` implementation provides:

- the complete repository/package layout;
- shared build, formatting, analyzer, and CI configuration;
- TCP endpoint and transport abstractions;
- PostgreSQL backend-frame parsing and startup/query message writing;
- an explicit protocol connection state machine;
- catalogue-friendly type descriptors, unknown-value preservation, and an `int4` codec;
- security redaction and observability primitives;
- a fake backend message stream for conformance testing;
- a Docker-based PostgreSQL version matrix.
- TLS negotiation with safe platform certificate validation by default;
- SCRAM-SHA-256 and SCRAM-SHA-256-PLUS authentication;
- startup metadata, structured errors/notices, and backend key data;
- buffered simple-query execution with multiple results;
- extended-query execution through Parse, Bind, Describe, Execute, and Sync;
- typed binary and text parameter encoding without SQL interpolation;
- ADO.NET transactions with PostgreSQL isolation levels, commit, rollback, and rollback-on-disposal;
- cancellation tokens, command timeouts, and explicit sync/async cancellation over PostgreSQL's dedicated channel;
- initial `BlueTuskConnection`, `BlueTuskCommand`, `BlueTuskDataReader`, and `BlueTuskDataSource` APIs.

Minimal usage:

```csharp
await using var dataSource = BlueTuskDataSource.Create(connectionString);
await using var command = dataSource.CreateCommand("SELECT $1::int4 + $2::int4");
command.Parameters.Add(new BlueTuskParameter<int>(20));
command.Parameters.Add(new BlueTuskParameter<int>(22));

var answer = await command.ExecuteScalarAsync<int>();
```

## License

BlueTusk is licensed under the [MIT License](LICENSE).
