# BlueTusk.Extensions.TimescaleDB

Stable TimescaleDB support for BlueTusk. TimescaleDB adds SQL behavior rather
than an application wire type, so this package contributes an immutable feature
plus parameterized ADO.NET operations for version discovery, converting an
existing table to a range hypertable, approximate row counts, retention and
Hypercore columnstore policies, and continuous-aggregate refresh policies.

```csharp
using BlueTusk.Data;
using BlueTusk.Extensions.TimescaleDB;
using BlueTusk.TypeSystem;

await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UseTimescaleDb()
    .Build();

var hypertable = await dataSource.CreateHypertableAsync(
    "public.metrics",
    "recorded_at");
var jobId = await dataSource.AddRetentionPolicyAsync(
    "public.metrics",
    new BlueTuskInterval(months: 0, days: 30, microseconds: 0));

var estimate = await dataSource.GetApproximateRowCountAsync("public.metrics");
await dataSource.AddColumnstorePolicyAsync(
    "public.metrics",
    new BlueTuskInterval(months: 0, days: 7, microseconds: 0));
```

PostgreSQL must have `CREATE EXTENSION timescaledb` applied. Relation and column
names are typed `regclass`/`name` parameters rather than SQL interpolation, and
extension functions are safely schema-qualified. `migrateData` defaults to
`false` because converting a populated table can take locks and requires an
explicit caller decision.

Continuous aggregates can be refreshed over a typed `DateTimeOffset` window
with `RefreshContinuousAggregateAsync`; add/remove policy helpers use finite
`BlueTuskInterval` values. The columnstore helpers target the current Hypercore
`add_columnstore_policy`/`remove_columnstore_policy` API rather than the legacy
compression-policy names. Typed EF queries and migration helpers are separately
packaged in `BlueTusk.Extensions.TimescaleDB.EntityFrameworkCore`.

This package and the BlueTusk extension SDK use the stable 1.0.0
Provider-family contract.
