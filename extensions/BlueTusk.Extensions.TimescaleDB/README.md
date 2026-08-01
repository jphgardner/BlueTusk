# BlueTusk.Extensions.TimescaleDB

Preview TimescaleDB support for BlueTusk. TimescaleDB adds SQL behavior rather
than an application wire type, so this package contributes an immutable feature
plus parameterized ADO.NET operations for version discovery, converting an
existing table to a range hypertable, and adding or removing retention policies.

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
```

PostgreSQL must have `CREATE EXTENSION timescaledb` applied. Relation and column
names are typed `regclass`/`name` parameters rather than SQL interpolation, and
extension functions are safely schema-qualified. `migrateData` defaults to
`false` because converting a populated table can take locks and requires an
explicit caller decision.

This package and the BlueTusk extension SDK are experimental `0.3.0-preview.1`
APIs, not stable or production-ready contracts.
