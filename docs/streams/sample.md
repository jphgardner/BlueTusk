# Run the snapshot-then-stream sample

This page shows you how to run `BlueTusk.Samples.Streams`, a hosted worker in
this repository that copies an existing table and then prints every committed
change to it.

The sample uses `AddBlueTuskStreams().AddHostedConsumer<T>()` with a
`PostgreSqlConsistentSnapshotSource`, the path described in
[snapshot and catch-up](snapshot-bootstrap.md) and
[hosting](hosting-observability.md). You need the .NET 10 SDK, a clone of this
repository, and a PostgreSQL 15 to 18 test server with `wal_level = logical`
(the `bluetusk-postgres` container from the
[5-minute first app](../getting-started/quickstart.md#1-start-postgresql)
works).

## 1. Create the table and publication

In `psql`:

```sql
CREATE SCHEMA IF NOT EXISTS app;
CREATE TABLE app.orders (
    id bigint PRIMARY KEY,
    description text NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
INSERT INTO app.orders (id, description) VALUES (1, 'existing row 1'), (2, 'existing row 2');
```

The sample's column list is fixed to this shape: `id bigint`, `description
text`, `updated_at timestamptz`.

Create the publication, but not the slot. The snapshot source must create the
slot itself to get a matching snapshot. With the
[`bluetusk-streams` tool](cli.md):

```powershell
$env:BLUETUSK_STREAMS_SOURCE = "Host=localhost;Port=5432;Username=postgres;Password=local-dev-only;Database=postgres;SSL Mode=Disable;Channel Binding=Disable"
bluetusk-streams provision --direct-only --skip-slot `
  --publication app_changes --slot app_sample_stream --table app.orders
```

Or in SQL: `CREATE PUBLICATION app_changes FOR TABLE app.orders;`.

> **Warning:** This uses the `postgres` superuser and `SSL Mode=Disable`, which
> is only acceptable for a local test container. Elsewhere use a login with
> `REPLICATION` and `SELECT` on the table, and keep the default
> `SSL Mode=VerifyFull`.

## 2. Run the sample

From the repository root, in the same terminal:

```powershell
$env:BlueTusk__Streams__Slot = "app_sample_stream"
$env:BlueTusk__Streams__Publications__0 = "app_changes"
dotnet run --project samples/BlueTusk.Samples.Streams
```

On Linux or macOS, use `export` for each variable. The worker copies the
existing rows, then waits for changes:

```text
SNAPSHOT RESET c498c113-d6cf-48a3-8033-2e65dfa38fbe Initial consistent snapshot.
SNAPSHOT START c498c113-d6cf-48a3-8033-2e65dfa38fbe tables=1
SNAPSHOT BATCH app.orders sequence=0 rows=2
SNAPSHOT COMPLETE c498c113-d6cf-48a3-8033-2e65dfa38fbe rows=2
```

## 3. Make a change

In a second terminal, insert a row:

```powershell
docker exec bluetusk-postgres psql -U postgres -c "INSERT INTO app.orders (id, description) VALUES (3, 'new row');"
```

The sample prints each change's full `ChangeId`, then a summary line:

```text
CHANGE ChangeId { Source = ChangeSourceIdentity { ... }, CommitEndPosition = 0/57BDCA0, TransactionId = 1491, Ordinal = 0 } InsertChange
TRANSACTION xid=1491 commit=0/57BDCA0 changes=1
```

## Settings

| Setting | Default | Meaning |
| --- | --- | --- |
| `BLUETUSK_STREAMS_SOURCE` | (required) | Source connection string. |
| `BlueTusk__Streams__Slot` | (required) | Slot the sample creates. |
| `BlueTusk__Streams__Publications__0` | (required) | Publication to read. |
| `BlueTusk__Streams__Sample__Schema` | `app` | Schema of the table to copy. |
| `BlueTusk__Streams__Sample__Table` | `orders` | Table to copy. |

Without the three required settings the sample prints a message and exits with
code 2.

## What happens when you restart it

The sample sets `ExistingSlotMode = RestartSnapshot`. On the next start it
checks that the slot is inactive, logical, uses `pgoutput` and belongs to the
configured database, drops it, and copies the table again under a new snapshot
epoch. It does not resume from a checkpoint. A real destination must discard or
replace the rows of the earlier epoch in `ResetSnapshotAsync`.

## Before you copy this into an application

The sample is deliberately small. A production worker should also:

- **Confirm positions to PostgreSQL.** The sample passes no delivery observer,
  so PostgreSQL is never told how far it has read and the slot keeps all WAL
  while the sample runs. Add the observer from
  [hosting and observability](hosting-observability.md#confirm-positions-to-postgresql),
  or a checkpoint store as in [snapshot and catch-up](snapshot-bootstrap.md#3-copy-then-stream-then-resume).
- **Apply changes durably and idempotently** before acknowledging each
  transaction.
- **Map rows to types** with a [typed mapping](typed-mappings.md) instead of a
  hand-written column list.

## Clean up

Stop the sample with Ctrl+C, then drop the slot so it stops holding WAL:

```sql
SELECT pg_drop_replication_slot('app_sample_stream');
DROP PUBLICATION app_changes;
DROP TABLE app.orders;
```
