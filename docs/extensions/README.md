# Extension SDK

Extensions register types and immutable feature descriptors through `BlueTusk.Extensions.Abstractions`. `BlueTuskDataSourceBuilder.Build()` snapshots both registries into the resulting data source. Later builder changes do not mutate an existing data source, and optional packages remain independently deployable without extension-specific dependencies in BlueTusk core packages.

The API is still preview. `BlueTusk.Extensions.Citext` is the first executable compatibility slice; it does not make the broader extension SDK stable.

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

## citext preview

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
    migrationBuilder.EnsureBlueTuskCitext();
    // Create citext-backed tables after this operation.
}

protected override void Down(MigrationBuilder migrationBuilder)
{
    // Drop dependent tables before removing the extension.
    migrationBuilder.DropBlueTuskCitext();
}
```

For model-owned installation and database-first round-tripping, configure the
generic lifecycle in `OnModelCreating`:

```csharp
modelBuilder.HasBlueTuskExtension(
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
template and compatibility harness establish an executable preview contract,
but stability still requires ecosystem feedback and an explicit versioning
commitment.

## pgvector preview

`BlueTusk.Extensions.PgVector` provides the first executable pgvector slice.
Install the extension before building the data source, then register the
schema-local `vector` type:

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

`BlueTuskVector` is immutable, structurally comparable, restricted to finite
single-precision elements, and enforces pgvector's 1–16,000 dimension range.
The codec implements pgvector's native binary header and float payload as well
as its invariant text form. Runtime catalogue composition also supports
`BlueTuskVector[]`. The package's live contract verifies scalar and array binary
round trips plus Euclidean distance against the official PostgreSQL 18 pgvector
image.

The current package deliberately covers the dense `vector` data path only.
`halfvec`, `sparsevec`, vector-specific `bit` behavior, EF mappings, and LINQ
distance translations are not claimed by this preview.

## hstore preview

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

## ltree preview

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

## pg_trgm preview

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

## PostGIS preview

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
rules, topology, and algorithms to PostGIS. A rich geometry object model and EF
spatial translations are separately tracked rather than claimed here.

## TimescaleDB preview

TimescaleDB adds SQL behavior instead of a wire type, so
`BlueTusk.Extensions.TimescaleDB` is a feature-only package. It provides typed
ADO.NET operations for discovering the installed extension version, converting
an existing table to a range hypertable, and adding or removing an interval-based
retention policy:

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
so callers must opt into that behavior. The PostgreSQL 17/TimescaleDB live gate
verifies version discovery, idempotent hypertable creation, quoted identifiers,
and retention-policy creation and removal. Broader TimescaleDB query helpers and
EF translations are not part of this preview.
