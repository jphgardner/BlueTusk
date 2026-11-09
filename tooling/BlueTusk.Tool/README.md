# BlueTusk.Tool

`BlueTusk.Tool` installs the `bluetusk` command. Use it to scaffold an EF Core
model from an existing PostgreSQL database, and to check that a server is ready
for BlueTusk before you deploy.

## Install

```powershell
dotnet tool install --global BlueTusk.Tool
```

This installs the latest stable version. To pin a version, add
`--version <version>`. See [Install BlueTusk](../../docs/getting-started/install.md)
for the available versions.

## Check a server with `bluetusk doctor`

> **New in 1.1.0.** `bluetusk doctor` is not in 1.0.0 or 1.1.0-rc.1.

Validate a target environment without changing it:

```powershell
$env:BLUETUSK_CONNECTION_STRING = "Host=db.example.com;Database=app;Username=app;Password=..."
bluetusk doctor --require-tls --require-streams --extension pgcrypto
```

`bluetusk doctor` checks the PostgreSQL version, the TLS session, logical WAL
settings, replication capacity and required extensions. It never prints the
connection string or raw transport and server errors. It exits with code 1
when a check fails and code 2 for invalid command usage.

| Option | Meaning |
| --- | --- |
| `--connection <value>` | Connection string. Defaults to the `BLUETUSK_CONNECTION_STRING` environment variable. |
| `--require-tls` | Fail unless the session uses TLS. |
| `--require-streams` | Fail unless the server is ready for logical replication (Streams). |
| `--extension <name>` | Fail unless the extension is installed. Repeat for several extensions. |
| `--timeout <seconds>` | Time limit for the checks, from 1 to 120. Default: 10. |
| `--json` | Write the result as JSON. |

## Check a local package candidate

After restoring the application against the candidate feed, pass its NuGet
assets file to check the resolved dependency graph. This command reads the
files without evaluating the project or running restore.

```powershell
bluetusk doctor --local-only --package-manifest ./candidate/sha256.json `
  --project-assets ./MyApp/obj/project.assets.json --json
```

The manifest must be at the root of the extracted candidate, with its
archive paths relative to that root. Doctor verifies the SHA-256 digests of
the listed NuGet and npm archives and checks the restored BlueTusk NuGet
IDs and versions against the manifest's feed. Use a manifest that covers
every BlueTusk package in the project. Omit `--project-assets` to check
only the candidate files. Omit `--local-only` and supply a connection to
run the PostgreSQL checks in the same report.

## Scaffold an EF Core model

Generate entity classes and a `DbContext` from a PostgreSQL schema:

```powershell
$env:BLUETUSK_CONNECTION_STRING = "Host=localhost;Database=app;Username=app;Password=..."
bluetusk scaffold --schema app --output Models --context AppDbContext
```

You can pass the connection string with `--connection` instead. BlueTusk uses
it to read the schema but does not write it into the generated C# unless you
pass `--include-connection-string`. Avoid that option if the code is committed,
because it puts a secret in source control.

Repeat `--schema` and `--table` to limit what is scaffolded. Views, functions
and property graphs are kept by default; the `--include-views`,
`--include-functions` and `--include-graphs` switches are accepted but not
required. Run `bluetusk scaffold --help` for naming, namespace, overwrite and
code-style options.

See the [EF Core guide](../../docs/ef-core/README.md) for using the generated model.
