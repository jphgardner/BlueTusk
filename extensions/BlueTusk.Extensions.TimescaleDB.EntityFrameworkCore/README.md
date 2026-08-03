# BlueTusk.Extensions.TimescaleDB.EntityFrameworkCore

Preview TimescaleDB query and migration integration for BlueTusk Entity
Framework Core. Register the feature on the long-lived data source and the
query translators on the EF provider:

```csharp
using BlueTusk.Data;
using BlueTusk.Extensions.TimescaleDB;
using Microsoft.EntityFrameworkCore;

await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UseTimescaleDb()
    .Build();

var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseBlueTusk(dataSource, provider => provider.UseTimescaleDb())
    .Options;
```

`EF.Functions.TimeBucket` translates interval buckets for `DateTimeOffset`,
`DateTime`, and `DateOnly`, including offset/origin forms and the TimescaleDB
timezone form for `DateTimeOffset`. Integer, bigint, and smallint buckets and
offsets are also available. All functions are schema-qualified with the schema
passed to `UseTimescaleDb`.

The package translates TimescaleDB's `first`, `last`, and `histogram`
hyperfunctions as typed group aggregates. The normal LINQ sequence operators
carry through to aggregate SQL, including ordering, `Distinct()`, and `Where()`
as PostgreSQL `ORDER BY`, `DISTINCT`, and `FILTER`:

```csharp
var width = new BlueTuskInterval(months: 0, days: 0, microseconds: 3_600_000_000);
var buckets = await context.Metrics
    .GroupBy(metric => EF.Functions.TimeBucket(width, metric.RecordedAt))
    .Select(group => new
    {
        group.Key,
        First = EF.Functions.TimescaleFirst(
            group.Select(metric => ValueTuple.Create(metric.Value, metric.RecordedAt))),
        Last = EF.Functions.TimescaleLast(
            group.Select(metric => ValueTuple.Create(metric.Value, metric.RecordedAt))),
        Histogram = EF.Functions.TimescaleHistogram(
            group.Select(metric => metric.Value), 0, 100, 10),
    })
    .ToListAsync();
```

Migrations can call `EnsureTimescaleDb`,
`ConvertToHypertable`, and `DropTimescaleDb`. Identifiers and
relation literals are quoted centrally; hypertable conversion is idempotent and
does not migrate existing rows unless explicitly requested.

The live PostgreSQL 17/TimescaleDB 2.29 gate covers scalar and aggregate query
execution, compiled queries, current Hypercore columnstore policies, approximate
row counts, and continuous-aggregate refresh and policy lifecycle. These APIs
remain experimental `0.3.0-preview.1` contracts.
