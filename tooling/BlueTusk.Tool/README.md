# BlueTusk.Tool

`BlueTusk.Tool` is the database-first command-line tool for the BlueTusk EF Core
provider and the production preflight for BlueTusk applications. Install the
packed .NET tool, then validate a target environment without changing it:

```powershell
dotnet tool install --global BlueTusk.Tool --version 1.2.0
$env:BLUETUSK_CONNECTION_STRING = "Host=localhost;Database=app;Username=app;Password=..."
bluetusk doctor --require-tls --require-streams --extension pgcrypto
```

`bluetusk doctor` checks the live PostgreSQL version, TLS session, logical-WAL
settings, replication capacity, and required extensions. It uses a bounded
timeout, never prints the connection string, returns a non-zero exit code for a
release-blocking failure, and supports `--json` for CI, Helm preflight jobs, and
Control Plane ingestion.

Scaffold a PostgreSQL schema with:

```powershell
dotnet tool install --global BlueTusk.Tool --version 1.0.0
$env:BLUETUSK_CONNECTION_STRING = "Host=localhost;Database=app;Username=app;Password=..."
bluetusk scaffold --schema app --output Models --context AppDbContext
```

The connection string can instead be supplied with `--connection`. BlueTusk
uses it for design-time discovery but does not write it into generated C# by
default. Pass `--include-connection-string` only when that explicit convenience
outweighs the risk of committing a secret.

Repeat `--schema` and `--table` to limit discovery. The `--include-graphs`,
`--include-functions`, and `--include-views` switches are accepted for the
product-spec command shape; BlueTusk retains those PostgreSQL objects by default
so generated models do not silently lose provider semantics. Run
`bluetusk scaffold --help` for naming, namespace, overwrite, and code-style
options.
