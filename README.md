# BlueTusk

**PostgreSQL, fully exposed to .NET.**

BlueTusk is a ground-up PostgreSQL provider ecosystem for .NET. Its long-term scope includes a native wire-protocol engine, ADO.NET, replication, Entity Framework Core, extension packages, and PostgreSQL SQL/PGQ support—without a runtime dependency on Npgsql.

> [!IMPORTANT]
> BlueTusk is in its foundation phase. The repository builds and contains tested transport/protocol/type-system primitives, but it is not yet a usable database provider. Track implemented scope in the [roadmap](docs/roadmap.md).

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

The current `0.0.1` foundation provides:

- the complete repository/package layout;
- shared build, formatting, analyzer, and CI configuration;
- TCP endpoint and transport abstractions;
- PostgreSQL backend-frame parsing and startup/query message writing;
- an explicit protocol connection state machine;
- catalogue-friendly type descriptors, unknown-value preservation, and an `int4` codec;
- security redaction and observability primitives;
- a fake backend message stream for conformance testing;
- a Docker-based PostgreSQL version matrix.

## License

BlueTusk is licensed under the [MIT License](LICENSE).

