# BlueTusk

**PostgreSQL, fully exposed to .NET.**

BlueTusk is a ground-up PostgreSQL provider ecosystem for .NET. Its long-term scope includes a native wire-protocol engine, ADO.NET, replication, Entity Framework Core, extension packages, and PostgreSQL SQL/PGQ support—without a runtime dependency on Npgsql.

> [!IMPORTANT]
> BlueTusk is an experimental pre-release provider. Version 0.0.2 can connect with TLS and SCRAM-SHA-256 and execute buffered simple queries through ADO.NET, but it does not yet support parameters, transactions, pooling, cancellation, or production workloads. Track implemented scope in the [roadmap](docs/roadmap.md).

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

The current `0.0.2` implementation provides:

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
- initial `BlueTuskConnection`, `BlueTuskCommand`, `BlueTuskDataReader`, and `BlueTuskDataSource` APIs.

Minimal usage:

```csharp
await using var connection = new BlueTuskConnection(connectionString);
await connection.OpenAsync();

await using var command = new BlueTuskCommand("SELECT 42::int4, 'hello'::text", connection);
await using var reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
{
    Console.WriteLine($"{reader.GetInt32(0)} — {reader.GetString(1)}");
}
```

## License

BlueTusk is licensed under the [MIT License](LICENSE).
