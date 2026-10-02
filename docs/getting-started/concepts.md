# Core concepts

These are the ideas every BlueTusk product builds on. Each product guide adds
its own concepts page; this page covers what they share.

## Data source, connection and session

A **data source** (`BlueTuskDataSource`) is the long-lived object that owns:

- the connection settings and credentials;
- the **connection pool** of physical PostgreSQL sessions; and
- the **type catalogue** loaded from the server.

A **connection** (`BlueTuskConnection`) is a short-lived logical connection.
Opening it borrows a physical session from the pool. Disposing it returns the
session.

```text
Application ──► BlueTuskDataSource (one per connection string, app lifetime)
                   └─ pool of physical sessions ──► PostgreSQL
                         ▲
        short-lived BlueTuskConnection / BlueTuskCommand borrow and return them
```

Rules of thumb:

- Create **one data source per distinct configuration** and keep it for the
  lifetime of the app. Register it as a singleton.
- Open connections late and dispose them early.
- Never create a data source per request. Each one creates its own pool.

PostgreSQL sessions carry state: temporary tables, `SET` values, prepared
statements, advisory locks, `LISTEN` registrations and open transactions.
Before a session is reused, BlueTusk rolls back any open transaction and runs
`DISCARD ALL`, so this state does not leak between callers. See
[connection pooling](../ado-net/pooling.md).

## Parameters keep SQL safe

Commands send parameter values separately from the SQL text, using the
PostgreSQL protocol. Values are never pasted into the SQL string.

```csharp
await using var command = dataSource.CreateCommand(
    "SELECT name FROM app.customers WHERE id = @id");
command.Parameters.Add(new BlueTuskParameter<long>(42) { ParameterName = "id" });
```

You can write placeholders as `@name`, `:name` or `$1`. Never build SQL by
concatenating user input.

## Types come from the server catalogue

PostgreSQL identifies every type by an object identifier (OID) in its catalogue.
When a data source starts, BlueTusk reads the catalogue and builds a type map.
This is why custom enums, composites, domains and extension types work: they are
discovered, not hard-coded.

If a value is ambiguous (for example a `null`, or a `string` that should be
`jsonb`), state the PostgreSQL type explicitly with `PostgreSqlTypeName`,
`PostgreSqlTypeOid` or `DbType`. See [PostgreSQL types](../types/README.md).

## Capabilities, not version numbers

Optional server features, such as an extension or SQL/PGQ graph queries, are
detected from the connected server's catalogue. BlueTusk does not assume a
feature exists because of a version number. If a feature is missing, the API
fails with a clear error instead of sending unsupported SQL.

## Two kinds of transaction

- An **application transaction** is work your code does through ADO.NET or EF
  Core. You can commit or roll it back.
- A **committed change transaction** is something PostgreSQL has already
  committed. Streams reads these from the write-ahead log (WAL) through
  **logical replication**. They cannot be rolled back; your code reacts to
  them.

Logical replication needs three things on the server: `wal_level = logical`, a
**publication** that lists the tables to capture, and a **replication slot**
that remembers how far a consumer has read.

## Delivery, acknowledgement and checkpoints

Streams, Sync and Live share one delivery contract:

1. PostgreSQL commits a transaction.
2. BlueTusk delivers the whole transaction, in commit order.
3. Your code (or a Sync destination) makes its effect durable.
4. Only then is the transaction **acknowledged**, and the **checkpoint**
   (the saved read position) moves forward.

If the process stops between steps 3 and 4, the same transaction is delivered
again after restart. Delivery is therefore **at least once**. Every change has
a stable identity, so a destination can recognise and ignore a repeat. BlueTusk
never claims "exactly once".

Read [delivery guarantees](../realtime-platform/contracts.md) for the exact
boundary of each product.

## Source identity

A checkpoint is only valid for the database it came from. BlueTusk records a
**source identity**: the PostgreSQL system identifier, database, replication
slot and a fingerprint of the publication. If any of these change, for example
after restoring into a new cluster, BlueTusk refuses to resume from the old
checkpoint instead of silently skipping or repeating data.

## Snapshot, then stream

A new consumer usually needs the existing rows as well as new changes. Taking a
table copy and then starting replication can miss changes made in between.
BlueTusk's **snapshot bootstrap** records a WAL position, copies a consistent
snapshot, then streams from exactly that position, with no gap and no overlap.
See [snapshot and catch-up](../streams/snapshot-bootstrap.md).

## Relay and consumer groups

One replication slot can feed many consumers through the **durable relay**: a
set of tables in PostgreSQL that store committed transactions for a retention
window. Each **consumer group** reads and acknowledges independently. See
[durable relay](../streams/durable-relay.md).

## Live results are re-queried, not copied from the change feed

Live uses a change only as a signal that a registered query might have changed.
It then re-runs that query with the subscriber's security scope and sends the
difference. Raw change data never goes straight to a browser. See
[Live guide](../live/README.md).

## Next steps

- [Install BlueTusk](install.md)
- [5-minute first app](quickstart.md)
- [Choose a product](overview.md#choose-where-to-start)
