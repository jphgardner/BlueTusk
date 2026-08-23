# BlueTusk pg_durable extension

`BlueTusk.Extensions.PgDurable` is a non-packable preview adapter for Microsoft's
`pg_durable` PostgreSQL extension. It is tested against exactly pg_durable 0.2.5
and provides parameterized helpers for starting, observing, signalling,
awaiting, and cancelling durable SQL workflows.

> [!WARNING]
> This project is excluded from the BlueTusk V1 product-family manifest and is
> not published as a stable package. Microsoft labels pg_durable as preview,
> describes its official container as evaluation-only, and records production
> security and resource-governance gaps. Do not use this adapter to claim a
> production-supported pg_durable deployment. Revalidate any later upstream
> version before changing this boundary.

The upstream status and limits are authoritative in the
[pg_durable 0.2.5 documentation](https://github.com/microsoft/pg_durable/tree/v0.2.5)
and [security review](https://github.com/microsoft/pg_durable/blob/v0.2.5/docs/security-review/security-review.md).

pg_durable must be installed by the PostgreSQL operator, added to
`shared_preload_libraries`, and created in the database configured by
`pg_durable.database` (the `postgres` database by default):

```sql
CREATE EXTENSION pg_durable;
SELECT df.grant_usage('app_role');
```

Register the feature before building the data source:

```csharp
using BlueTusk.Data;
using BlueTusk.Extensions.PgDurable;

await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UsePgDurable()
    .Build();

var instanceId = await dataSource.StartPgDurableAsync(
    "SELECT count(*) AS total FROM orders",
    label: "count-orders");

var status = await dataSource.AwaitPgDurableAsync(instanceId);
var resultJson = await dataSource.GetPgDurableResultAsync(instanceId);
```

The workflow is SQL by design and is executed by pg_durable. Labels, instance
IDs, cancellation reasons, signal names, signal data, database names, and
transaction modes are sent as typed parameters. Results remain JSON text so the
application can choose its own JSON model.

`GetPgDurableMetricsAsync` reads an eventually consistent snapshot of the
extension's system-wide aggregate and therefore requires an explicit
`GRANT EXECUTE ON FUNCTION df.metrics()` (or the upstream delegated-admin grant).
Aggregate counts can briefly trail per-instance status, so readiness checks that
correlate the two should use bounded polling. Ordinary application roles should
not receive that permission merely to start and observe their own workflows.

The extension contributes no PostgreSQL wire type. BlueTusk's normal catalogue
discovery and built-in codecs cover its `text`, `jsonb`, `regrole`, UUID,
timestamp, and integer values. EF migrations can use the provider's generic
extension lifecycle with `modelBuilder.HasExtension("pg_durable")`; server
preloading and role grants remain operator-owned deployment configuration.

The live acceptance test requires both `BLUETUSK_PGDURABLE_LIVE_TESTS=true`
and `BLUETUSK_TEST_CONNECTION_STRING`. CI enables that switch only for the
digest-pinned pg_durable image job, so the general PostgreSQL 15–19 provider
matrix cannot mistake an ordinary server for an extension-enabled target.
