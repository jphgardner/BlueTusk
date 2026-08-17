# BlueTusk pg_durable extension

`BlueTusk.Extensions.PgDurable` is the feature-only BlueTusk integration for
Microsoft's `pg_durable` PostgreSQL extension. It targets pg_durable 0.2.5 or
later and provides parameterized helpers for starting, observing, signalling,
awaiting, and cancelling durable SQL workflows.

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

The extension contributes no PostgreSQL wire type. BlueTusk's normal catalogue
discovery and built-in codecs cover its `text`, `jsonb`, `regrole`, UUID,
timestamp, and integer values. EF migrations can use the provider's generic
extension lifecycle with `modelBuilder.HasExtension("pg_durable")`; server
preloading and role grants remain operator-owned deployment configuration.
