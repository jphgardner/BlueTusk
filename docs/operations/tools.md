# Use the BlueTusk command-line tools

Install a tool version from the same package source as its BlueTusk
dependencies. Run the command’s `--help` before using an option that may
differ between versions. Supply connection strings through your secret
environment instead of literal command-line arguments.

## Inspect a PostgreSQL connection

The `bluetusk doctor` command is available in builds of `BlueTusk.Tool`
that include the database preflight. It checks the live server, TLS,
logical replication settings, and required extensions without changing
the database. Set `BLUETUSK_CONNECTION_STRING`, then run:

```sh
bluetusk doctor --require-tls --require-streams --extension pgcrypto
```

Only require Streams and extensions your deployment uses. Use `--json`
for machine-readable output and treat a nonzero exit code as a failed
check. Run as the role the application will use.

## Scaffold an EF Core model

Install `BlueTusk.Tool` and configure `BLUETUSK_CONNECTION_STRING`. Limit
catalogue discovery to the application schema:

```sh
bluetusk scaffold --schema app --output Models --context AppDbContext
```

Review the generated model and PostgreSQL-specific annotations before
building the application. Use a read-only discovery identity when possible.
BlueTusk omits the connection string from generated source by default.
For Microsoft’s `dotnet ef` workflow, see
[database-first scaffolding](../ef-core/reference.md#database-first-scaffolding).

## Choose a specialist tool

| Command | Use it to |
| --- | --- |
| `bluetusk-streams` | [Inspect and provision a change stream](../streams/cli.md). |
| `bluetusk-schema` | [Capture PostgreSQL catalogue state and detect drift](../schema/README.md). |
| `bluetusk-sql` | [Validate a typed SQL catalog against a schema](../sql/README.md). |

Start a read-only operation with restricted credentials and an explicit
schema. For a provisioning or migration command, review the proposed scope
and use a separate deployment identity.
