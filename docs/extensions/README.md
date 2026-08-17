# Extension SDK

Extensions register types and immutable feature descriptors through `BlueTusk.Extensions.Abstractions`. `BlueTuskDataSourceBuilder.Build()` snapshots both registries into the resulting data source. Later builder changes do not mutate an existing data source, and optional packages remain independently deployable without extension-specific dependencies in BlueTusk core packages.

## What “supports PostgreSQL extensions” means

BlueTusk does not use a closed server-extension allowlist. Any extension
installed on the connected PostgreSQL server can be called through normal,
parameterized commands, and EF Core can own any extension's installation with
`modelBuilder.HasExtension("extension_name")`. Runtime catalogue discovery
loads extension-provided OIDs instead of assuming a compiled-in type list.

The level of convenience depends on what an extension contributes:

| Extension surface | BlueTusk behavior |
| --- | --- |
| Functions, procedures, operators, access methods, and background workers | Available through normal SQL without a BlueTusk-specific package. |
| Domains, enums, composites, arrays, ranges, and multiranges over known types | Catalogue-discovered and composed automatically. |
| A new base wire type | Preserved as `BlueTuskUnknownValue` until an extension package registers a semantic codec. |
| Provider-specific CLR values, helpers, or LINQ translation | Supplied by an optional first-party or third-party BlueTusk package. |

This makes “all extensions” an interoperability contract, not a promise that
every PostgreSQL extension has a bespoke CLR API. Server installation, binary
compatibility, preloading, privileges, and operational safety remain properties
of PostgreSQL and the extension itself.

The extension-authoring seam is compatibility-stable: the public surfaces of
`BlueTusk.Extensions.Abstractions`, `BlueTusk.Extensions.Testing`,
`BlueTusk.TypeSystem`, and the ADO.NET integration points have compiler-enforced
shipped API/nullability baselines. `BlueTusk.Extensions.Citext` supplied the
first executable compatibility slice and the packaged template exercises the
same contract. The registered first-party extension packages and their EF
integrations are included in the stable Provider 1.0.0 API freeze.

## Start an extension package

`BlueTusk.Templates` provides a complete source-and-test skeleton:

```powershell
dotnet new install BlueTusk.Templates
dotnet new bluetusk-extension `
  -n Contoso.BlueTusk.Extensions.MyType `
  --ExtensionName MyType `
  --PostgreSqlTypeName my_type
```

The generated package keeps extension-specific SQL and CLR types outside the
core provider. It includes binary/text codec tests and a live contract test
using `BlueTusk.Extensions.Testing`.

The framework-neutral compatibility verifier checks four integration
boundaries through a built data source: immutable feature retention, live
catalogue type discovery, resolved CLR identity, and resolved codec identity.
It briefly checks out a normal pooled connection; the caller continues to own
and dispose the data source. Extension authors must also add representative
value round trips, PostgreSQL behavioural tests, package-content inspection,
and any separate EF translation/migration plug-in tests.

## citext

Install `citext` in PostgreSQL, configure one long-lived data source, and use the extension-owned CLR value so runtime type inference remains unambiguous from ordinary PostgreSQL `text`:

```sql
CREATE EXTENSION IF NOT EXISTS citext;
```

```csharp
using BlueTusk.Data;
using BlueTusk.Extensions.Citext;

var builder = new BlueTuskDataSourceBuilder(connectionString).UseCitext();
await using var dataSource = builder.Build();
await using var command = dataSource.CreateCommand(
    "SELECT $1::citext = 'bluetusk'::citext, $1::citext");
command.Parameters.Add(new BlueTuskParameter<BlueTuskCitext>(new("BlueTusk")));

await using var reader = await command.ExecuteReaderAsync();
await reader.ReadAsync();
var equal = reader.GetBoolean(0); // true; comparison is performed by PostgreSQL
var value = reader.GetFieldValue<BlueTuskCitext>(1);
```

`UseCitext("extensions")` supports an extension installed into a non-default schema. Scalar and array values use PostgreSQL's text/binary send and receive functions and the same runtime catalogue used by the rest of the data source.

EF integration is deliberately a second package,
`BlueTusk.Extensions.Citext.EntityFrameworkCore`. Register the codec on the data
source and the EF mapping on the provider independently:

```csharp
var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UseCitext()
    .Build();

services.AddDbContext<AppDbContext>(options =>
    options.UseBlueTusk(dataSource, provider => provider.UseCitext()));
```

This maps `BlueTuskCitext` and `BlueTuskCitext[]` to schema-qualified PostgreSQL
types. Normal EF equality queries remain parameterized and use PostgreSQL's
case-insensitive `citext` operator semantics. The plug-in participates in EF's
service-provider cache identity, so different installation schemas do not share
an incompatible singleton mapping.

Migrations that own installation of the PostgreSQL extension can use the
companion package's compatibility helpers. The core EF provider also has a
generic typed PostgreSQL-extension lifecycle without taking a dependency on
`citext` or any other extension-specific package:

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.EnsureCitext();
    // Create citext-backed tables after this operation.
}

protected override void Down(MigrationBuilder migrationBuilder)
{
    // Drop dependent tables before removing the extension.
    migrationBuilder.DropCitext();
}
```

For model-owned installation and database-first round-tripping, configure the
generic lifecycle in `OnModelCreating`:

```csharp
modelBuilder.HasExtension(
    "citext",
    extension => extension.UseSchema("extensions"));
```

This metadata orders installation before extension-backed schema objects and
removal after them. The `citext` CLR type, codec, query semantics, and
extension-specific service registration remain isolated in the companion
packages.

The immutable descriptor is available for integration code that must inspect configured optional behavior:

```csharp
var feature = dataSource.Features.GetRequired<BlueTuskCitextFeature>(
    BlueTuskCitextFeature.RegistryName);
```

No citext SQL, CLR type, or package reference is present in `BlueTusk.Data`,
`BlueTusk.Client`, `BlueTusk.EntityFrameworkCore`, or lower layers. The EF
mapping and migration SQL live only in the companion package. The authoring
template and compatibility harness establish the executable stable authoring
contract. Citext-specific and EF-specific convenience APIs are part of the
stable Provider 1.0.0 contract.

## pgvector

`BlueTusk.Extensions.PgVector` provides executable support for pgvector's
`vector`, `halfvec`, and `sparsevec` types. Install the extension before building
the data source, then register its schema-local types:

```sql
CREATE EXTENSION IF NOT EXISTS vector;
```

```csharp
using BlueTusk.Data;
using BlueTusk.Extensions.PgVector;

await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UsePgVector()
    .Build();

var embedding = new BlueTuskVector(1f, 2f, 3f);
await using var command = dataSource.CreateCommand(
    "SELECT $1::vector, $1 <-> '[1,2,4]'::vector");
command.Parameters.Add(new BlueTuskParameter<BlueTuskVector>(embedding));
```

`BlueTuskVector` and `BlueTuskHalfVector` are immutable, structurally comparable,
restricted to finite elements, and enforce pgvector's 1–16,000 dimension range.
`BlueTuskSparseVector` accepts zero-based CLR indices, stores up to 16,000 sorted
non-zero float elements across as many as one billion dimensions, and formats
the one-based SQL representation. Their codecs implement pgvector's native
binary layouts and invariant text forms. Runtime catalogue composition supports
arrays of all three values. The live contract verifies scalar/array binary round
trips, distance execution, and pgvector's Hamming/Jaccard operators over the
core provider's `BlueTuskBitString` against the official PostgreSQL 18 image.

The separately packaged `BlueTusk.Extensions.PgVector.EntityFrameworkCore`
integration maps scalar and array properties for all three types, preserves
dimension-qualified store types such as `vector(768)`, and translates
parameterized L2, negative-inner-product, cosine, and L1 distance calls to
`<->`, `<#>`, `<=>`, and `<+>`:

```csharp
var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseBlueTusk(dataSource, provider => provider.UsePgVector())
    .Options;

var nearest = await context.Items
    .OrderBy(item => EF.Functions.L2Distance(item.Embedding, probe))
    .Take(10)
    .ToListAsync();
```

The EF package also translates `BlueTuskBitString` Hamming and Jaccard distances
to `<~>` and `<%>`. Its live gate verifies EF writes, materialisation, array
round trips, dimension constraints, and parameterized distance execution for
the complete pgvector type family.

## hstore

`BlueTusk.Extensions.HStore` maps PostgreSQL's key/value type to an immutable
`BlueTuskHStore` value. Install and register it before building the data source:

```sql
CREATE EXTENSION IF NOT EXISTS hstore;
```

```csharp
using BlueTusk.Data;
using BlueTusk.Extensions.HStore;

var attributes = new BlueTuskHStore(
    new("owner", "BlueTusk"),
    new("reviewed", null));
var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UseHStore()
    .Build();
```

The value preserves PostgreSQL's distinction between a null value and the text
`"NULL"`, uses ordinal key identity, and compares independently of pair order.
Its parser and formatter implement hstore quoting and backslash escaping. The
native codec validates pair counts, length prefixes, UTF-8, duplicate keys, and
trailing bytes. Runtime catalogue composition supplies `BlueTuskHStore[]` with
no array-specific registration. The live gate covers scalar and array binary
round trips plus key lookup, existence, and null-definition semantics.

## ltree

`BlueTusk.Extensions.LTree` covers all three public ltree data types: label
paths, hierarchical patterns, and position-independent text patterns.

```sql
CREATE EXTENSION IF NOT EXISTS ltree;
```

```csharp
using BlueTusk.Data;
using BlueTusk.Extensions.LTree;

var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UseLTree()
    .Build();
var path = new BlueTuskLTree("Top.Countries.Europe.Russia");
var query = new BlueTuskLQuery("Top.*{,2}.Europe.Russ@*");
```

`BlueTuskLTree`, `BlueTuskLQuery`, and `BlueTuskLTxtQuery` keep separate CLR
identities so parameter inference cannot confuse their PostgreSQL operators.
Their binary codecs implement PostgreSQL's version byte followed by canonical
UTF-8 text. PostgreSQL remains the grammar authority because valid ltree label
characters depend on the database locale. The live gate resolves all three
catalogue types and exercises arrays, hierarchical matching,
position-independent matching, and path-level functions.

## pg_trgm

pg_trgm contributes functions, operators, and index operator classes but no
wire type, so `BlueTusk.Extensions.PgTrgm` is intentionally a feature-only
plugin. Its typed ADO.NET helper executes the three similarity families and
threshold operators together:

```sql
CREATE EXTENSION IF NOT EXISTS pg_trgm;
```

```csharp
using BlueTusk.Data;
using BlueTusk.Extensions.PgTrgm;

var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UsePgTrgm()
    .Build();
var comparison = await dataSource.ComparePgTrgmAsync(
    "BlueTusk",
    "blue tusk");
```

`BlueTuskPgTrgmComparison` returns similarity, word similarity, strict-word
similarity, all three threshold decisions, and the query's trigram set from one
round trip. Both input strings are typed parameters. Functions and operators
are schema-qualified, including quoted custom schemas. The live gate moves the
extension into a spaced identifier, executes the same behavior, and restores
it to `public`.

## pg_durable

`pg_durable` contributes a background worker and a SQL workflow DSL over
PostgreSQL built-in types. `BlueTusk.Extensions.PgDurable` is a non-packable
preview adapter: catalogue discovery already covers its `text`, `jsonb`,
`regrole`, UUID, timestamp, and integer values. It is excluded from the V1
Provider manifest and stable package/API counts.

[Microsoft labels pg_durable as preview](https://github.com/microsoft/pg_durable/tree/v0.2.5)
and its official container as evaluation-only. Its
[published security review](https://github.com/microsoft/pg_durable/blob/v0.2.5/docs/security-review/security-review.md)
records unresolved production resource-governance and default-exposure gaps.
The checked-in adapter and live gate prove BlueTusk compatibility with exactly
0.2.5; they do not approve the upstream extension or its container for
production use.

For evaluation, the PostgreSQL operator must install pg_durable 0.2.5, add it to
`shared_preload_libraries`, create it in the database selected by
`pg_durable.database` (the `postgres` database by default), and grant the
application role explicitly:

```sql
CREATE EXTENSION pg_durable;
SELECT df.grant_usage('app_role');
```

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

The helpers cover installed-version discovery, start, status, result, await,
cancel, external signals, and restricted system-wide aggregate metrics. The
metrics helper requires an explicit `df.metrics()` EXECUTE grant; the ordinary
application-role grant intentionally omits it. Workflow SQL remains
extension-owned; all non-workflow inputs are typed parameters, result JSON is
preserved as text, and unknown future status values degrade to `Unknown`. The
dedicated official pg_durable 0.2.5/PostgreSQL 17 evaluation-image gate verifies
a complete parameterized workflow lifecycle. Promotion requires fresh upstream
production guidance, a supported deployment artifact, closed security blockers,
explicit resource limits, and a new compatibility review.

## PostGIS

`BlueTusk.Extensions.PostGIS` provides distinct geometry and geography
identities without introducing a geometry-model dependency into BlueTusk core:

```sql
CREATE EXTENSION IF NOT EXISTS postgis;
```

```csharp
using BlueTusk.Data;
using BlueTusk.Extensions.PostGIS;

var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UsePostGis()
    .Build();
var point = BlueTuskGeometry.FromText(
    "SRID=4326;POINT(-0.1276 51.5072)");
```

`BlueTuskGeometry` and `BlueTuskGeography` accept server-parseable WKT/EWKT or
immutable EWKB. The codecs select text for textual values and native binary for
EWKB; when an array contains text, existing binary elements fall back to
hexadecimal EWKB accepted by PostGIS. Binary results can be sent back without
conversion. The live PostgreSQL 18/PostGIS 3.6 gate verifies catalogue
resolution, geometry and geography parameters, arrays, SRIDs, geography
distance, spatial predicates, and text-to-binary conversion.

This transport slice deliberately leaves geometry parsing, coordinate-system
rules, topology, and algorithms to PostGIS. Rich geometry and EF support stays
separately packaged in `BlueTusk.Extensions.PostGIS.EntityFrameworkCore`, which
uses NetTopologySuite without adding spatial dependencies to BlueTusk core:

```csharp
using NetTopologySuite.Geometries;

var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UsePostGis()
    .Build();

services.AddDbContext<AppDbContext>(options =>
    options.UseBlueTusk(dataSource, provider => provider.UsePostGis()));

modelBuilder.Entity<Place>()
    .Property(place => place.Location)
    .HasColumnType("geometry(Point,4326)");
```

The EF package maps the complete NetTopologySuite geometry hierarchy, exact
geometry/geography typmods, and arrays through BlueTusk's EWKB codecs. It
translates typed predicates, distance and index-aware proximity, set operations,
buffers, geometry members, transforms, validity repair, GeoJSON output, and the
PostGIS bounding-box operator. Geography uses only its documented distance,
intersection, covers/covered-by, area, length, and centroid surface; unsupported
geometry-only calls produce focused translation errors. Conversion helpers
preserve SRID and XY/Z/M ordinates, mutable geometries receive structural EF
snapshots, and migration helpers own optional extension create/drop SQL. The
PostgreSQL 18/PostGIS 3.6 live gate covers round trips, arrays, parameters,
projections, spatial filters, and compiled queries.

## TimescaleDB

TimescaleDB adds SQL behavior instead of a wire type, so
`BlueTusk.Extensions.TimescaleDB` is a feature-only package. It provides typed
ADO.NET operations for discovering the installed extension version, converting
an existing table to a range hypertable, obtaining approximate row counts, and
managing retention, Hypercore columnstore, and continuous-aggregate policies:

```sql
CREATE EXTENSION IF NOT EXISTS timescaledb;
```

```csharp
using BlueTusk.Data;
using BlueTusk.Extensions.TimescaleDB;
using BlueTusk.TypeSystem;

await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UseTimescaleDb()
    .Build();

var result = await dataSource.CreateHypertableAsync(
    "public.metrics",
    "recorded_at");

var jobId = await dataSource.AddRetentionPolicyAsync(
    "public.metrics",
    new BlueTuskInterval(months: 0, days: 30, microseconds: 0));

await dataSource.RemoveRetentionPolicyAsync("public.metrics");
```

Relations and time-column names are passed through PostgreSQL `regclass` and
`name` parameters, while the configured extension schema is safely delimited.
`migrateData` defaults to `false`: converting a populated table can take locks,
so callers must opt into that behavior. Continuous-aggregate refresh windows use
typed `DateTimeOffset` values and all policy intervals must be finite. The
columnstore helpers use TimescaleDB's current Hypercore APIs, not the legacy
compression-policy names.

`BlueTusk.Extensions.TimescaleDB.EntityFrameworkCore` adds separately registered,
schema-qualified query translations and migration helpers:

```csharp
services.AddDbContext<AppDbContext>(options =>
    options.UseBlueTusk(dataSource, provider => provider.UseTimescaleDb()));

var hourly = context.Metrics
    .GroupBy(metric => EF.Functions.TimeBucket(width, metric.RecordedAt))
    .Select(group => new
    {
        group.Key,
        First = EF.Functions.TimescaleFirst(
            group.Select(metric => ValueTuple.Create(metric.Value, metric.RecordedAt))),
        Histogram = EF.Functions.TimescaleHistogram(
            group.Select(metric => metric.Value), 0, 100, 10),
    });
```

Temporal buckets support timestamp-with-time-zone, timestamp, and date values,
including offset/origin forms and timezone-aware timestamp-with-time-zone
bucketing; smallint, integer, and bigint buckets are also typed. `first`, `last`,
and `histogram` preserve LINQ ordering, distinctness, and filters in aggregate
SQL. The PostgreSQL 17/TimescaleDB 2.29 gate verifies queries, compiled queries,
hypertable creation, approximate counts, and all documented policy/refresh
lifecycles.
