# Validate and provision PostgreSQL with `bluetusk-streams`

This guide shows you how to use the `bluetusk-streams` command to check that a
PostgreSQL database is ready for Streams, and to create the publication, slot
and relay storage a worker needs.

## Install the tool

```powershell
dotnet tool install --global BlueTusk.Streams.Tool
```

See [Install BlueTusk](../getting-started/install.md) to pin a version. Run
`bluetusk-streams --help` or `bluetusk-streams <command> --help` for usage.

## Set the connections

The tool reads connection strings from environment variables, which keeps them
out of your shell history:

```powershell
$env:BLUETUSK_STREAMS_SOURCE = "Host=source;Database=app;Username=streams;Password=..."
$env:BLUETUSK_STREAMS_CONTROL = "Host=control;Database=streams;Username=streams;Password=..."
```

On Linux or macOS, use `export NAME="..."`. You can pass `--connection` and
`--control-connection` instead, for example in automation. Error messages never
include either connection string.

`BLUETUSK_STREAMS_SOURCE` is the database you capture changes from. The login
needs the `REPLICATION` attribute to create a slot, and must own the tables (and
have `CREATE` on the database) to create a publication. `BLUETUSK_STREAMS_CONTROL` is the separate database that
holds the [durable relay](durable-relay.md); it is needed only for relay setups.

## Check a database

```powershell
bluetusk-streams validate --publication orders_publication --slot orders_quickstart --direct-only
```

```text
OK BTS001 PostgreSQL 18 is supported.
OK BTS002 wal_level is logical.
OK BTS003 Publication 'orders_publication' exists.
OK BTS004 The configured publications expose 1 distinct table(s).
OK BTS005 Logical slot 'orders_quickstart' is compatible and inactive.
OK BTS006 Direct slot-per-group mode was selected explicitly.
OK BTS008 Publication fingerprint: 17984220fe0a62f6750ce374434707168d85ed8051431cc6c094d78a07d5b6e5
```

Each line is `OK`, `WARNING` or `ERROR`, a stable code and a message:

| Code | Checks | Fix when it fails |
| --- | --- | --- |
| `BTS001` | The server is PostgreSQL 15 to 19. | Upgrade the server. |
| `BTS002` | `wal_level` is `logical`. | Set `wal_level = logical` and restart PostgreSQL. |
| `BTS003` | Each `--publication` exists. | Run `provision` with `--table`, or `CREATE PUBLICATION`. |
| `BTS004` | The publications include at least one table. | Add tables to the publication. |
| `BTS005` | The slot exists, uses `pgoutput` and belongs to this database. Reports whether it is active. | Run `provision`, or drop and recreate a slot that uses another plug-in or database. |
| `BTS006` | Relay storage is in a separate database (or sharing was allowed). A `WARNING` means no control connection was given. | Pass `--control-connection`, or `--direct-only` for direct consumers. |
| `BTS007` | No publication includes the relay's own schema. | Remove the control schema from the publication. |
| `BTS008` | Always `OK`. Prints the canonical publication fingerprint. | You can use it as the publication fingerprint of your `ChangeSourceIdentity`. |

The exit code is `0` when there are no errors, `1` when any check fails or the
command fails, and `2` for invalid arguments.

## Provision for the durable relay

```powershell
bluetusk-streams provision --publication app_changes --slot app_streams `
  --table app.orders --table app.order_items
```

This command:

1. creates the publication `FOR TABLE` the listed tables if it does not exist
   (an existing publication is left unchanged);
2. creates a `pgoutput` logical slot if it does not exist;
3. creates or upgrades the relay schema in the control database
   (`--control-schema`, default `bluetusk_streams`); and
4. runs the same checks as `validate`.

It is safe to run again. Output lines start with `CREATED`, `UNCHANGED` or
`READY`, followed by the check report.

## Provision for a direct consumer

Add `--direct-only` to skip relay storage. No control connection is needed:

```powershell
bluetusk-streams provision --direct-only `
  --publication orders_publication --slot orders_quickstart --table app.orders
```

A [snapshot consumer](snapshot-bootstrap.md) must create its own slot, so also
add `--skip-slot`:

```powershell
bluetusk-streams provision --direct-only --skip-slot `
  --publication app_changes --slot app_sample_stream --table app.orders
```

```text
CREATED publication app_changes
SKIPPED relay storage (explicit --direct-only mode)
OK BTS001 PostgreSQL 18 is supported.
OK BTS002 wal_level is logical.
OK BTS003 Publication 'app_changes' exists.
OK BTS004 The configured publications expose 1 distinct table(s).
WARNING BTS005 Logical slot 'app_sample_stream' is absent and slot provisioning was skipped.
OK BTS006 Direct slot-per-group mode was selected explicitly.
OK BTS008 Publication fingerprint: 1569e830e20ebdd6f3313d62c6b4d59e1f44e82bf5dbd1b30441c4d2bfd07bf9
```

> **Warning:** A slot created by `provision` keeps WAL from that moment, even if
> no worker reads it. Drop slots you do not use. See
> [troubleshooting](troubleshooting.md#wal-keeps-growing-on-the-source-server).

## Keep the relay in the source database (not recommended)

By default the tool refuses a control connection that points at the source
database:

```text
BlueTusk Streams provision failed: The relay control data source resolves to the source database. Use a separate database or pass --allow-shared-control explicitly.
```

If you must share one database, pass `--allow-shared-control` and list tables
with `--table`. `--all-tables` is then rejected, and `BTS007` fails if a
publication includes the relay schema, because the relay would capture its own
writes.

## All options

| Option | Commands | Meaning |
| --- | --- | --- |
| `--connection <value>` | both | Source connection string. Default: `BLUETUSK_STREAMS_SOURCE`. |
| `--publication <name>` | both | Publication name. Required; repeat for several. |
| `--slot <name>` | both | Logical replication slot name. Required. |
| `--skip-slot` | both | Do not create the slot; report it as a warning if absent. |
| `--control-connection <value>` | both | Relay control connection string. Default: `BLUETUSK_STREAMS_CONTROL`. Required by `provision` unless `--direct-only`. |
| `--control-schema <name>` | both | Relay schema. Default: `bluetusk_streams`. |
| `--direct-only` | both | No relay storage: one slot per consumer. |
| `--allow-shared-control` | both | Allow the source and control connections to use the same database. |
| `--table <schema.table>` | `provision` | Table for a new publication. Repeatable. |
| `--all-tables` | `provision` | Create the publication `FOR ALL TABLES`. Not allowed with shared control. |
| `--help`, `-h` | both | Show usage. |

## Related pages

- [Quick start](quickstart.md)
- [Troubleshooting](troubleshooting.md)
