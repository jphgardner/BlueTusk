# Entity Framework Core

`BlueTusk.EntityFrameworkCore` is the EF Core provider over the BlueTusk ADO.NET driver. The current implementation supports provider registration, relational queries, change tracking and PostgreSQL CRUD, explicit transactions and savepoints, store-generated values, optimistic concurrency, and PostgreSQL-native type mappings.

## Configure a context

```csharp
services.AddSingleton(_ =>
    new BlueTuskDataSourceBuilder(connectionString).Build());
services.AddDbContext<AppDbContext>((serviceProvider, options) =>
    options.UseBlueTusk(serviceProvider.GetRequiredService<BlueTuskDataSource>()));
```

The long-lived data source is the recommended application entry point: EF-created logical connections share its physical pool, configured codecs, and runtime type catalogue, while the dependency-injection container owns the data source lifetime. `UseBlueTusk` also accepts a connection string or an existing `BlueTuskConnection` for compatibility and dedicated-lifetime scenarios; directly constructed connections are unpooled.

Configure runtime user-defined types before registering the data source:

```csharp
var builder = new BlueTuskDataSourceBuilder(connectionString);
builder.MapEnum<OrderStatus>("app.order_status");
builder.MapComposite<Address>("app.address");
var dataSource = builder.Build();

services.AddDbContext<AppDbContext>(options =>
    options.UseBlueTusk(dataSource));
```

Optional extensions keep their ADO.NET and EF registrations separate. For
example, `citext` uses `BlueTusk.Extensions.Citext` for the data-source codec and
`BlueTusk.Extensions.Citext.EntityFrameworkCore` for EF scalar/array mappings
and migration helpers:

```csharp
var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UseCitext()
    .Build();

services.AddDbContext<AppDbContext>(options =>
    options.UseBlueTusk(dataSource, provider => provider.UseCitext()));
```

## PostgreSQL type mappings

In addition to the standard .NET relational types, the 0.3 query work maps BlueTusk's wire-native PostgreSQL scalar values. This includes `inet`/`cidr`, `macaddr`/`macaddr8`, all built-in geometric values, `bit`/`varbit`, arbitrary-precision `numeric`, `money`, `pg_lsn`, `tid`, `timetz`, native intervals, `jsonpath`, `tsvector`/`tsquery`, object identifiers, transaction values, and system-catalogue values. `string` can be explicitly mapped to `json`, `jsonb`, or `xml`.

Ambiguous types default to the general-purpose mapping: `BlueTuskNetworkAddress` uses `inet`, `BlueTuskBitString` uses `bit varying`, and `BlueTuskTransactionSnapshot` uses `pg_snapshot`. Select the alternative with normal EF configuration:

```csharp
modelBuilder.Entity<NetworkRule>(entity =>
{
    entity.Property(rule => rule.Network).HasColumnType("cidr");
    entity.Property(rule => rule.Mask).HasColumnType("bit(128)");
    entity.Property(rule => rule.Document).HasColumnType("jsonb");
});
```

Provider mappings carry the exact PostgreSQL OID into every parameter, including null parameters, so store-type intent is preserved on the wire.

CLR arrays of supported wire-native elements map to PostgreSQL arrays. Both one- and multidimensional CLR arrays preserve shape, null reference-type elements, and exact store intent such as `cidr[]`, `bit(128)[]`, and `jsonb[]`. The mapping uses structural snapshots, so mutating an array element in place is detected by EF change tracking. `byte[]` remains the scalar `bytea` mapping; use `byte[][]` for `bytea[]`.

The six built-in range families map to `BlueTuskRange<T>`, and the corresponding multiranges map to `BlueTuskMultirange<T>`:

| Subtype | Range | Multirange |
|---|---|---|
| `int` | `int4range` | `int4multirange` |
| `long` | `int8range` | `int8multirange` |
| `BlueTuskNumeric` | `numrange` | `nummultirange` |
| `DateTime` | `tsrange` | `tsmultirange` |
| `DateTimeOffset` | `tstzrange` | `tstzmultirange` |
| `DateOnly` | `daterange` | `datemultirange` |

Range and multirange arrays are supported as well.

Schema-qualified store types map runtime-registered enums and composites, catalogue-discovered domains, and lossless `BlueTuskRecord` values. Their arrays are supported with the same exact runtime type identity. Configure primitive-collection element store types explicitly so EF can build the element mapping used by query parameters and change tracking:

```csharp
modelBuilder.Entity<Order>(entity =>
{
    entity.Property(order => order.Status)
        .HasColumnType("app.order_status");
    entity.PrimitiveCollection(order => order.StatusHistory)
        .HasColumnType("app.order_status[]")
        .ElementType(element => element.HasStoreType("app.order_status"));
    entity.Property(order => order.ShippingAddress)
        .HasColumnType("app.address");
});
```

## PostgreSQL operator predicates

The first PostgreSQL-specific query slice exposes typed, translation-only
extensions on `EF.Functions`. Captured values remain normal EF parameters and
retain the store type inferred from the mapped column. No operator API accepts
an SQL fragment or concatenates application values into generated SQL.

```csharp
var requiredTags = new[] { 2, 3 };
var activeWindow = new BlueTuskRange<int>(100, 200);
var jsonFilter = """{"kind":"provider"}""";
var candidateIds = new[] { 7, 11, 42 };
var cursorId = 7;
var cursorName = "BlueTusk";

var documents = await context.Documents
    .Where(document =>
        EF.Functions.ILike(document.Name, "blue%")
        && EF.Functions.ArrayContains(document.Tags, requiredTags)
        && EF.Functions.RangeContains(document.ValidIds, activeWindow)
        && EF.Functions.JsonContains(document.Metadata, jsonFilter)
        && EF.Functions.EqualAny(document.Id, candidateIds)
        && EF.Functions.RowGreaterThan(
            ValueTuple.Create(document.Id, document.Name),
            ValueTuple.Create(cursorId, cursorName)))
    .ToListAsync(cancellationToken);
```

The preview covers:

- text `ILIKE`, case-sensitive `~`, and case-insensitive `~*`;
- array containment (`@>`, `<@`) and overlap (`&&`);
- range containment, element containment, overlap, strict left/right, and
  adjacency, plus multirange containment and overlap;
- JSONB containment and key tests, and JSONPath `@?`/`@@`;
- `inet`/`cidr` containment and overlap; and
- `tsvector @@ tsquery` full-text matching;
- typed `=`, `<>`, `<`, `<=`, `>`, and `>=` comparisons with PostgreSQL
  `ANY(array)` and `ALL(array)`, plus `LIKE`/`ILIKE` quantifiers; and
- two-or-more-element row comparisons using `ValueTuple.Create(...)` and all
  six PostgreSQL B-tree comparison operators.

The quantified methods are named after their SQL shape: `EqualAny`,
`NotEqualAll`, `LessThanAny`, and the corresponding comparison/quantifier
combinations. Array arguments retain one PostgreSQL array parameter instead of
being interpolated or expanded into SQL literals. Row methods are `RowEqual`,
`RowNotEqual`, `RowLessThan`, `RowLessThanOrEqual`, `RowGreaterThan`, and
`RowGreaterThanOrEqual`. Both row constructors must have the same arity;
BlueTusk rejects a mismatch during translation with a focused diagnostic.

These methods deliberately throw if evaluated as ordinary CLR methods. A query
must translate completely, and SQL null behavior follows the underlying
PostgreSQL operator rather than pretending to be an in-memory implementation.
Operator behavior is defined by PostgreSQL's
[row and array comparison](https://www.postgresql.org/docs/current/functions-comparisons.html),
[pattern](https://www.postgresql.org/docs/current/functions-matching.html),
[array](https://www.postgresql.org/docs/current/functions-array.html),
[range/multirange](https://www.postgresql.org/docs/current/functions-range.html),
[JSON](https://www.postgresql.org/docs/current/functions-json.html),
[network](https://www.postgresql.org/docs/current/functions-net.html), and
[text-search](https://www.postgresql.org/docs/current/functions-textsearch.html)
documentation. SQL-generation tests cover every exposed operator family, and
live acceptance executes typed parameters against PostgreSQL 15–19.

## PostgreSQL scalar functions

Scalar `EF.Functions` translations compose inside filters and projections, and
can be nested with the operator predicates above. For example, full-text query
construction and ranking remain entirely server-side:

```csharp
var search = "PostgreSQL provider";

var matches = await context.Documents
    .Where(document => EF.Functions.FullTextMatches(
        EF.Functions.ToTextSearchVector(document.Body),
        EF.Functions.PlainToTextSearchQuery(search)))
    .Select(document => new
    {
        document.Id,
        Rank = EF.Functions.TextSearchRank(
            EF.Functions.ToTextSearchVector(document.Body),
            EF.Functions.PlainToTextSearchQuery(search)),
        MetadataType = EF.Functions.JsonTypeOf(document.Metadata),
        ValidFrom = EF.Functions.RangeLower(document.ValidIds),
    })
    .OrderByDescending(result => result.Rank)
    .ToListAsync(cancellationToken);
```

The initial scalar surface includes array length/lower/upper/cardinality;
range and multirange bounds, inclusivity, infinity, and empty checks; JSONB
type, array length, and first JSONPath result; regular-expression replace and
count; network host/family/mask/network/broadcast; and full-text vector/query
construction, lexeme/node counts, and rank. PostgreSQL's default text-search
configuration applies to the current no-configuration overloads.

The date/time surface includes `date_part`, `date_trunc`, `date_bin`, and
two-argument `age`; date, time, timestamp, timestamp-with-time-zone, and
interval constructors; and all three interval-justification functions.
Timestamp-with-time-zone construction and truncation expose an explicit time-
zone argument so results do not silently depend on the session setting.
`date_bin` accepts a `TimeSpan` stride, which cannot represent months and
therefore matches PostgreSQL's stride restriction. Calendar-sensitive interval
results use `BlueTuskInterval`, preserving independent months, days, and
microseconds instead of flattening them into a `TimeSpan`:

```csharp
var buckets = context.Events.Select(item => new
{
    Bin = EF.Functions.DateBin(
        TimeSpan.FromMinutes(15),
        item.RecordedAt,
        origin),
    Day = EF.Functions.DateTrunc(
        "day",
        item.RecordedAtWithTimeZone,
        "Europe/London"),
});
```

The geometric surface covers PostgreSQL's complete documented function table:
area, center, diagonal, diameter, height, open/closed path tests, length, point
count, path open/close conversion, radius, slope, and width. Overloads retain
the exact `BlueTuskBox`, `BlueTuskPath`, `BlueTuskCircle`,
`BlueTuskLineSegment`, `BlueTuskPolygon`, and `BlueTuskPoint` mappings. Path
area is nullable because PostgreSQL returns `NULL` for an open path. Generated
SQL and live typed-parameter/result tests run across PostgreSQL 15–19. Other
mathematical, formatting, string, binary, JSON, and full-text scalar overloads
remain planned and are not implied by this preview. The function definitions
follow PostgreSQL's [date/time](https://www.postgresql.org/docs/current/functions-datetime.html)
and [geometric](https://www.postgresql.org/docs/current/functions-geometry.html)
documentation.

## PostgreSQL aggregate functions

The initial aggregate surface translates grouping enumerables without losing
EF's aggregate metadata:

```csharp
var summaries = await context.Events
    .GroupBy(item => item.Category)
    .Select(group => new
    {
        group.Key,
        Values = EF.Functions.ArrayAggregate(
            group.OrderBy(item => item.Position).Select(item => item.Value)),
        Labels = EF.Functions.StringAggregate(
            group.OrderBy(item => item.Position).Select(item => item.Label),
            ", "),
        AllValid = EF.Functions.BooleanAnd(group.Select(item => item.IsValid)),
        Covered = EF.Functions.RangeAggregate(
            group.Where(item => item.IsIncluded).Select(item => item.ValidRange)),
    })
    .ToListAsync(cancellationToken);
```

`ArrayAggregate`, `StringAggregate`, `BooleanAnd`, `BooleanOr`,
`RangeAggregate`, and `RangeIntersectAggregate` map to PostgreSQL
`array_agg`, `string_agg`, `bool_and`, `bool_or`, `range_agg`, and
`range_intersect_agg`. Ordering stays inside the aggregate call, `Distinct()`
becomes aggregate `DISTINCT`, and a grouping `Where(...)` becomes native
`FILTER (WHERE ...)`. Delimiters and filter values remain normal parameters.
The APIs return nullable results because PostgreSQL returns `NULL` when an
aggregate has no selected input rows.

`JsonAggregate`, `JsonbAggregate`, and `XmlAggregate` retain `json`, `jsonb`,
and `xml` result mappings. `IntegerBitAnd`/`Or`/`Xor` and their `BigInt`
counterparts expose PostgreSQL's width-preserving bitwise aggregates.
`StandardDeviationPopulation`, `StandardDeviationSample`,
`VariancePopulation`, and `VarianceSample` have `double` and `decimal`
overloads so floating-point and PostgreSQL `numeric` calculations materialize
without changing result families. These aggregates keep the same in-call
ordering, `DISTINCT`, and `FILTER` support as the initial surface:

```csharp
var summaries = context.Events
    .GroupBy(item => item.Category)
    .Select(group => new
    {
        Payloads = EF.Functions.JsonbAggregate(
            group.OrderBy(item => item.Position).Select(item => item.Payload)),
        PopulationVariance = EF.Functions.VariancePopulation(
            group.Where(item => item.IsIncluded).Select(item => item.Measurement)),
    });
```

Generated SQL and typed live tests cover both result mappings and numeric
families across PostgreSQL 15–19. JSON object aggregates, paired regression and
covariance aggregates, ordered-set/hypothetical-set aggregates, and remaining
aggregate families are still planned; BlueTusk does not currently emulate their
multi-input or `WITHIN GROUP` syntax with client code.

## Array expansion and lateral queries

Mapped PostgreSQL array properties can be queried as ordinary primitive
collections. BlueTusk translates collection filters, projections, `Any`, and
correlated `SelectMany` through `unnest(...) WITH ORDINALITY`. Correlated inner
and outer collection selectors use PostgreSQL `JOIN LATERAL` and
`LEFT JOIN LATERAL`; no SQL Server `APPLY` syntax leaks into generated SQL:

```csharp
var minimum = 10;

var expanded = await context.Documents
    .SelectMany(
        document => document.Scores.Where(score => score >= minimum),
        (document, score) => new { document.Id, Score = score })
    .OrderBy(result => result.Id)
    .ThenBy(result => result.Score)
    .ToListAsync(cancellationToken);
```

The array value and filter inputs keep their relational type mappings and
normal parameterization. Explicit output-column names survive EF alias
uniquification, ordinality preserves PostgreSQL array order, and nullable array
elements materialize without being collapsed. For an outer expansion over a
non-nullable value-type array, project the element to its nullable form before
`DefaultIfEmpty()` so the absent row remains distinguishable from the CLR
default value. This preview covers mapped array columns only.

Series are also available as typed, composable query roots. Use
`Database.GenerateSeries` for a standalone series; `int`, `long`, and `decimal`
map to PostgreSQL `integer`, `bigint`, and `numeric`, while `DateTime` and
`DateTimeOffset` map to `timestamp` and `timestamp with time zone`. Numeric
steps default to one; temporal roots require a `TimeSpan` interval:

```csharp
var values = await context.Database
    .GenerateSeries(2, 10, 2)
    .Where(value => value >= minimum)
    .OrderBy(value => value)
    .ToListAsync(cancellationToken);
```

Use `EF.Functions.GenerateSeries` inside a translated query when a bound must
refer to an outer row. BlueTusk represents the function as a query root before
EF navigation expansion and emits a parameterized PostgreSQL lateral join:

```csharp
var expanded = await context.Documents
    .SelectMany(
        document => EF.Functions.GenerateSeries(1, document.PageCount),
        (document, page) => new { document.Id, Page = page })
    .ToListAsync(cancellationToken);
```

The numeric two-argument and explicit-step forms and the explicit-step temporal
forms participate in compiled queries. `Database.GenerateSeries` rejects a zero
step before execution; PostgreSQL retains its native empty-series and direction
semantics. The translation-only `EF.Functions` form must not be called outside
an EF query. The PostgreSQL 16+ timezone-name overload is not exposed yet so
the same API executes across the PostgreSQL 15–19 support matrix.

BlueTusk also exposes four single-column JSONB roots:
`JsonArrayElements`, `JsonArrayElementsText`, `JsonObjectKeys`, and
`JsonPathQuery`. JSON-valued results remain mapped as `jsonb`; text elements
materialize as nullable strings so a JSON `null` is not replaced with an empty
value. JSON and JSONPath parameters receive exact store-type mappings, while
mapped properties should be configured as `jsonb`:

```csharp
modelBuilder.Entity<Document>()
    .Property(document => document.Payload)
    .HasColumnType("jsonb");

var elements = await context.Documents
    .SelectMany(
        document => EF.Functions.JsonArrayElementsText(document.Payload),
        (document, element) => new { document.Id, Element = element })
    .ToListAsync(cancellationToken);
```

`JsonEach` and `JsonEachText` expand JSON objects to typed
`KeyValuePair<string, string>` and `KeyValuePair<string, string?>` rows. The
JSONB form preserves each value as JSON text with a `jsonb` result mapping; the
text form uses nullable values so JSON `null` materializes as `null`:

```csharp
var properties = await context.Documents
    .SelectMany(
        document => EF.Functions.JsonEachText(document.Payload),
        (document, property) => new
        {
            document.Id,
            property.Key,
            property.Value,
        })
    .ToListAsync(cancellationToken);
```

These roots emit `WITH ORDINALITY` internally so duplicate values and JSON-null
elements have stable row identity and source order. Correlated roots use lateral
joins, and captured JSON/JSONPath values remain parameters.

`JsonToRecordset<T>` expands a JSONB array of objects into an application row
shape. `T` must be registered as a flat keyless entity. Its configured column
names become the JSON field names and its relational store types become the
PostgreSQL column-definition list required by `jsonb_to_recordset`:

```csharp
modelBuilder.Entity<PayloadRow>(row =>
{
    row.HasNoKey();
    row.Property(item => item.Id)
        .HasColumnName("id")
        .HasColumnType("integer");
    row.Property(item => item.Label)
        .HasColumnName("label")
        .HasColumnType("text");
});

var payloadRows = await context.Documents
    .SelectMany(
        document => EF.Functions.JsonToRecordset<PayloadRow>(document.Payload),
        (document, row) => new { document.Id, row.Label })
    .ToListAsync(cancellationToken);
```

BlueTusk quotes every model-derived output name and emits each mapped store type;
the JSON input remains a `jsonb` parameter or mapped column. PostgreSQL converts
JSON fields according to those declared types and returns `NULL` for missing
fields. Configure nullable CLR properties wherever the payload may contain JSON
`null` or omit a field. Recordset rows are keyless and untracked, and callers
should use an explicit `OrderBy` when result order matters. Inheritance,
navigations, and complex properties are rejected with a focused diagnostic so
the generated record contract remains flat and explicit. Correlated and compiled
queries are covered across PostgreSQL 15–19; no query-time column name, store
type, or SQL fragment is accepted.

The initial multi-argument `unnest` API pairs an `integer[]` with a nullable
`text[]` and returns `KeyValuePair<int?, string?>` rows. Both outputs are
nullable because PostgreSQL pads the shorter input with `NULL`:

```csharp
var pairs = await context.Documents
    .SelectMany(
        document => EF.Functions.Unnest(document.Scores, document.Labels),
        (document, pair) => new
        {
            document.Id,
            Score = pair.Key,
            Label = pair.Value,
        })
    .ToListAsync(cancellationToken);
```

Column-correlated inputs use a lateral join; captured arrays retain their exact
array mappings and work in compiled queries. Additional element-type and arity
combinations remain planned.

Application-defined table functions use EF Core's model metadata instead of a
runtime string-based SQL API. Define a context method with `FromExpression`,
map its row as a keyless entity, and register the method's PostgreSQL name and
schema with `HasDbFunction`:

```csharp
public IQueryable<SearchResult> SearchDocuments(int minimumRank)
    => FromExpression(() => SearchDocuments(minimumRank));

protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.Entity<SearchResult>().HasNoKey();
    modelBuilder
        .HasDbFunction(typeof(AppDbContext).GetMethod(
            nameof(SearchDocuments), [typeof(int)])!)
        .HasName("search_documents")
        .HasSchema("application");
}
```

The function name and schema are fixed model metadata and are identifier-quoted;
function arguments remain normal EF parameters. The returned keyless row can be
filtered, ordered, projected, and used in compiled queries. A call whose
argument refers to an outer row becomes a PostgreSQL `JOIN LATERAL`, so model-
registered functions compose in correlated `SelectMany` queries as well. SQL-
generation and live PostgreSQL 15–19 tests cover schema qualification,
parameterization, typed materialization, correlation, and compiled execution.
BlueTusk does not expose an API that accepts an application-provided function
name or SQL fragment at query time.

## Migrations

`Database.GenerateCreateScript()` and `IRelationalDatabaseCreator.CreateTables()` generate PostgreSQL DDL for ordinary relational models. The supported create-schema surface includes tables, primary and foreign keys, indexes, defaults, length and precision facets, and `GENERATED BY DEFAULT AS IDENTITY` integer keys. This path is covered both by SQL-shape tests and by executing the generated commands against PostgreSQL.

Runtime migrations support the PostgreSQL `__EFMigrationsHistory` repository, transaction-scoped migration locking, up/down application, and idempotent scripts. The initial DDL surface covers tables, columns, keys and constraints, indexes, sequences, defaults, comments, schema moves, and alter/rename/drop operations. Acceptance tests apply an idempotent script twice, re-enter `Database.MigrateAsync()`, move back to an earlier migration, and finally revert to the empty database.

### Advanced PostgreSQL indexes

Advanced index metadata composes with EF's standard `IsUnique`, `IsDescending`,
`HasFilter`, and `HasDatabaseName` configuration:

```csharp
modelBuilder.Entity<Document>()
    .HasIndex(document => new { document.Title, document.CreatedAt })
    .HasDatabaseName("ix_documents_title_created")
    .IsUnique()
    .IsDescending(false, true)
    .HasFilter("\"title\" IS NOT NULL")
    .UseBlueTuskIndexMethod("btree")
    .UseBlueTuskOperatorClass("text_pattern_ops", null)
    .UseBlueTuskCollation("C", null)
    .HasBlueTuskNullSortOrder(
        BlueTuskIndexNullSortOrder.NullsFirst,
        BlueTuskIndexNullSortOrder.NullsLast)
    .IncludeProperties(document => new
    {
        document.SearchVector,
        document.Summary,
    })
    .HasBlueTuskFillFactor(80)
    .HasBlueTuskNullsDistinct(false)
    .IsBlueTuskConcurrent();
```

`UseBlueTuskIndexMethod` accepts built-in B-tree, hash, GiST, SP-GiST, GIN,
and BRIN methods as well as extension-provided access methods. Operator classes
and collations are configured per leading key and may be schema-qualified.
Included properties are resolved through EF's table/column mapping, while
storage-parameter names and values are restricted to safe PostgreSQL tokens.
`NULLS NOT DISTINCT` requires PostgreSQL 15 or newer, which is BlueTusk's
current minimum supported server.

Trusted expression indexes can replace selected mapped keys with
`HasBlueTuskIndexExpressions`; an empty entry retains the mapped column. These
expressions become migration DDL verbatim and must be fixed application model
metadata, never request data or user input. Partial indexes continue to use
EF's `HasFilter` API.

Concurrent create and drop commands are emitted with `CONCURRENTLY` and marked
as transaction-suppressed EF migration commands. PostgreSQL does not allow
those commands inside a transaction. The current idempotent-script wrapper is
therefore not a supported deployment path for migrations containing concurrent
indexes; run normal EF migrations or use non-concurrent index DDL until the
version-aware/idempotent DDL milestone is complete.

### PostgreSQL exclusion constraints

Exclusion constraints use provider-owned entity metadata because EF has no
relational constraint abstraction for PostgreSQL's `EXCLUDE` form. Column
elements use typed property selectors and resolve through EF's table mapping:

```csharp
modelBuilder.Entity<Reservation>()
    .HasBlueTuskExclusionConstraint(
        "reservations_no_overlap",
        constraint => constraint
            .UseIndexMethod("gist")
            .HasProperty(reservation => reservation.During, "&&")
            .IncludeProperties(nameof(Reservation.Note))
            .HasStorageParameter("fillfactor", "80")
            .HasFilter("active")
            .IsDeferrable());
```

Each element can instead be a fixed trusted SQL expression and can configure a
schema-qualified operator, collation, operator class and operator-class
parameters, descending order, and explicit null ordering. Constraints also
support included mapped columns, validated index storage parameters, an index
tablespace, a trusted partial predicate, and immediate or initially deferred
deferrability. Operator tokens and storage settings are validated separately
from identifier-quoted names. Expression and predicate SQL are deliberate
model-time escape hatches and must never contain request data or other untrusted
input.

Migration diffing adds constraints after their tables and drops them before
dependent relational changes. Equal definitions can be renamed without an
index rebuild; all other definition changes produce an explicit destructive
drop/add pair. Drops use `RESTRICT`. PostgreSQL does not support exclusion
constraints on partitioned roots, so BlueTusk reports a model diagnostic and
requires constraints to be configured on concrete leaf partitions.

Database-first discovery joins `pg_constraint` to the backing index and retains
the access method, ordered canonical index expressions, exact operators,
included columns, storage settings, tablespace, partial predicate, and
deferrability. Canonical expressions that cannot safely be mapped back to one
EF property are retained as trusted preformatted model metadata. The complete
enforcement, discovery, generated-C#, rename, and drop lifecycle is exercised
against PostgreSQL 15–19.

### Declarative table partitioning

Partition trees are part of the EF model rather than a collection of unrelated
tables. RANGE, LIST, and HASH roots, default partitions, and recursive
subpartitions use the same metadata in create scripts, migration diffs,
snapshots, and reverse-engineered models:

```csharp
modelBuilder.Entity<Event>()
    .HasBlueTuskRangePartitioning(item => item.OccurredOn)
    .HasRangePartition(
        "events_2025",
        BlueTuskPartitionValue.Literal(new DateOnly(2025, 1, 1)),
        BlueTuskPartitionValue.Literal(new DateOnly(2026, 1, 1)))
    .HasRangePartition(
        "events_2026",
        BlueTuskPartitionValue.Literal(new DateOnly(2026, 1, 1)),
        BlueTuskPartitionValue.Literal(new DateOnly(2027, 1, 1)))
    .HasDefaultPartition("events_default")
    .HasSubpartitioning(
        "events_2026",
        BlueTuskPartitionStrategy.Hash,
        [BlueTuskPartitionKeyDefinition.Column(nameof(Event.TenantId))],
        child => child
            .HasHashPartition("events_2026_0", modulus: 2, remainder: 0)
            .HasHashPartition("events_2026_1", modulus: 2, remainder: 1));
```

The property-expression helpers resolve EF property names to their mapped
column names. Explicit keys may be columns or fixed trusted SQL expressions,
with optional schema-qualified collation and operator-class identifiers. LIST
partitioning accepts one key, matching PostgreSQL's restriction; RANGE and HASH
may use multiple keys. Typed bound values cover strings, Booleans, integral and
decimal numbers, dates, timestamps with time zone, UUIDs, `NULL`, `MINVALUE`,
and `MAXVALUE`. `BlueTuskPartitionValue.FromSql` and
`BlueTuskPartitionBound.FromSql` are deliberate escape hatches for fixed model
metadata and must never receive request data or other user input.

Migration diffing creates new partition trees and emits add, drop, rename, and
schema-move operations for children. Changing a bound replaces that partition
with a destructive drop/create pair. PostgreSQL cannot convert an existing
table to a different partition strategy or key in place, so BlueTusk reports a
diagnostic requiring an explicit data-preserving replacement migration instead
of silently rebuilding the table.

Existing compatible tables can be attached and partitions can be detached from
manual migrations:

```csharp
migrationBuilder.AttachBlueTuskPartition(
    "events",
    "events_2027",
    BlueTuskPartitionBound.Range(
        BlueTuskPartitionValue.Literal(new DateOnly(2027, 1, 1)),
        BlueTuskPartitionValue.Literal(new DateOnly(2028, 1, 1))),
    parentSchema: "application",
    partitionSchema: "application");

migrationBuilder.DetachBlueTuskPartition(
    "events",
    "events_2027",
    BlueTuskPartitionDetachMode.Concurrently,
    parentSchema: "application",
    partitionSchema: "application");
```

Concurrent detach is emitted as a transaction-suppressed migration command.
PostgreSQL does not permit it when the partitioned table has a default
partition, and interrupted concurrent detaches may require the `Finalize` mode.
Attaching a new partition may also require validating or constraining existing
rows in its table and the current default partition; plan those data steps in
the migration before the attach operation.

### Row-level security

Row-level security enablement, owner enforcement, and policies are retained as
table-owned EF metadata:

```csharp
modelBuilder.Entity<Document>()
    .UseBlueTuskRowLevelSecurity(enabled: true, forced: true)
    .HasPolicy(
        "tenant_select",
        BlueTuskRowSecurityPolicyCommand.Select,
        usingSql: "tenant_id = current_setting('application.tenant_id')::integer",
        roles: [BlueTuskRowSecurityRoleDefinition.Named("application_user")])
    .HasPolicy(
        "tenant_insert",
        BlueTuskRowSecurityPolicyCommand.Insert,
        withCheckSql: "tenant_id = current_setting('application.tenant_id')::integer",
        roles: [BlueTuskRowSecurityRoleDefinition.Named("application_user")]);
```

Policies support PostgreSQL's permissive and restrictive behavior, `ALL`,
`SELECT`, `INSERT`, `UPDATE`, and `DELETE` command scopes, named roles, and the
`PUBLIC`, `CURRENT_ROLE`, `CURRENT_USER`, and `SESSION_USER` targets. BlueTusk
rejects `USING` on `INSERT` policies and `WITH CHECK` on `SELECT` or `DELETE`
policies because PostgreSQL does not accept those combinations. Policy
expressions are emitted verbatim: they must be fixed application-model SQL and
must never contain request data or other user input.

Migration diffs create, alter, drop, and rename policies. Role and predicate
changes use PostgreSQL's in-place `ALTER POLICY`. A change PostgreSQL cannot
alter—such as command scope, permissive versus restrictive behavior, or
removing an existing expression—is represented as an explicit drop/create replacement.
Removing the model metadata drops its policies and emits `DISABLE ROW LEVEL
SECURITY` and `NO FORCE ROW LEVEL SECURITY` when needed. Table renames preserve
the attached policies without trying to recreate them. Generated migration C#
and snapshots retain the same typed definition.

Enabling RLS without an applicable permissive policy produces PostgreSQL's
default-deny behavior. Superusers and roles with `BYPASSRLS` still bypass
policies; table owners normally bypass them unless `forced: true` is configured.
RLS supplements normal `GRANT` privileges rather than replacing them, so roles
also need schema and table privileges. Application migrations should create or
manage those roles and grants separately from the policy metadata.

### Direct table inheritance

PostgreSQL table inheritance is modelled separately from EF's CLR inheritance
mapping and from declarative partitioning. A child may name one or more direct
parents; parent order is retained because PostgreSQL records it in
`pg_inherits.inhseqno` and uses it when arranging inherited columns:

```csharp
modelBuilder.Entity<BaseEvent>()
    .ToTable("base_events", "application");
modelBuilder.Entity<AuditRecord>()
    .ToTable("audit_records", "application");

modelBuilder.Entity<EventMessage>()
    .ToTable("event_messages", "application")
    .InheritsFromBlueTuskTable<BaseEvent>()
    .InheritsFromBlueTuskTable<AuditRecord>();
```

Configure each parent entity and its final table mapping before using the typed
helper. `InheritsFromBlueTuskTable("base_events", "application")` is available
when the parent is external to the EF model. The child must contain compatible
columns, nullability, and inheritable check constraints for every parent;
PostgreSQL validates those structural rules when the migration is applied.
Primary keys, unique constraints, and foreign keys are not inherited.

Migration diffing emits `NO INHERIT` before a relationship or dependent table
is removed and `INHERIT` after both tables exist. Parent and child table/schema
renames preserve an unchanged relationship without detaching it. Reordering
multiple parents performs an explicit detach/reattach so the catalogue order is
deterministic. Manual migrations can use `AddBlueTuskTableInheritance` and
`RemoveBlueTuskTableInheritance`; removal leaves both tables and their columns
in place. Normal PostgreSQL queries against a parent include descendant rows,
while `ONLY parent_table` restricts a query to the parent's own rows.

### PostgreSQL collations

Provider-owned collation schema objects participate in migration diffing,
snapshots, generated migration C#, dependency ordering, and database-first
scaffolding:

```csharp
modelBuilder.HasBlueTuskCollation(
    "case_insensitive",
    collation => collation
        .UseProvider(BlueTuskCollationProvider.Icu)
        .UseLocale("und-u-ks-level2")
        .IsDeterministic(false),
    schema: "application");
```

The builder supports one provider locale or separate libc `LC_COLLATE` and
`LC_CTYPE` values. Nondeterministic comparison and custom rules require ICU.
ICU `HasRules` is capability-guarded for PostgreSQL 16 and later; the
`Builtin` provider is guarded for PostgreSQL 17 and later. Accepted locales and
their exact behavior depend on the server's operating-system or ICU build, so
applications should test the comparisons and ordering they rely on.

Collations are created after their schema and before provider-owned types,
routines, tables, indexes, and views. Automatic creation omits `IF NOT EXISTS`
so an unmanaged collision cannot be mistaken for the configured definition.
An otherwise identical name or schema change uses `ALTER COLLATION`, preserving
PostgreSQL dependency identities. Provider, locale, determinism, rules, and
recorded-version changes cannot be altered safely in place; BlueTusk rejects
them and requires an explicit rebuild of every dependent object followed by a
drop/create migration. Automatic drops are destructive and use `RESTRICT`.

Manual migrations can copy an existing collation or control collision/drop
semantics:

```csharp
migrationBuilder.CreateBlueTuskCollationFrom(
    "application_default",
    "C",
    schema: "application",
    sourceSchema: "pg_catalog");

migrationBuilder.DropBlueTuskCollation(
    "application_default",
    schema: "application");
```

Provider version drift needs special care. Rebuild every affected index and
other stored object first, then use
`RefreshBlueTuskCollationVersion`; PostgreSQL's refresh only updates the
catalogue version and does not verify or rebuild dependants. `HasVersion` is the
low-level creation option primarily used when preserving state through upgrade
or scaffolding, not an automatic upgrade mechanism.

Reverse engineering reads `pg_collation` through a version-adaptive projection,
retaining provider, locale categories, determinism, ICU rules, and the recorded
version while excluding system and extension-owned collations. PostgreSQL does
not retain whether a definition was originally copied with `FROM`, so
database-first scaffolding emits its explicit discovered properties.

### PostgreSQL extensions

Provider-owned extension installations participate in migration diffing,
snapshots, generated migration C#, dependency ordering, and database-first
scaffolding:

```csharp
modelBuilder.HasBlueTuskExtension(
    "hstore",
    extension => extension
        .UseSchema("application_types")
        .HasVersion("1.8"));

modelBuilder.HasBlueTuskExtension("postgis");
modelBuilder.HasBlueTuskExtension(
    "postgis_topology",
    extension => extension
        .DependsOnExtension("postgis")
        .InstallDependencies());
```

An extension is created after its target schema but before provider-owned
types, routines, tables, indexes, and views, so those objects may consume its
types, functions, operators, or access methods. Declared provider-owned
extension dependencies are installed first and removed last. PostgreSQL
extension names are database-global rather than schema-qualified; `UseSchema`
selects the installation schema and a later change emits `ALTER EXTENSION SET
SCHEMA`. PostgreSQL accepts that move only for a relocatable extension.

`HasVersion` pins initial installation and emits `ALTER EXTENSION ... UPDATE TO`
when changed. PostgreSQL must provide a valid update path; BlueTusk does not
promise downgrades. Removing the version requests PostgreSQL's next available
update without pinning a target. A schema move and version update are emitted as
separate statements, update first. If dependent model metadata contains textual
schema-qualified extension object names, stage the extension move and those
metadata changes in explicit migrations.

Automatic creates deliberately omit `IF NOT EXISTS`, because that clause does
not prove that an existing installation has the requested schema, version, or
configuration. Automatic drops are destructive and explicitly use `RESTRICT`;
dependent provider-owned objects are removed first, while unmanaged dependants
make PostgreSQL reject the drop. Manual migrations can use
`CreateBlueTuskExtension`, `AlterBlueTuskExtension`, and
`DropBlueTuskExtension`, including explicit `IF NOT EXISTS` or `CASCADE`
options when the application owns that risk. `InstallDependencies` adds
`CASCADE` only during creation so PostgreSQL may recursively install missing
required extensions.

Reverse engineering reads the installed schema, exact version, and recorded
extension-to-extension dependencies from `pg_extension` and `pg_depend`.
Whether the original install used `CASCADE` is not stored by PostgreSQL, so
scaffolding does not regenerate `InstallDependencies`. Owners, grants, and
extension membership changes are also outside this metadata and require
explicit migrations. Extension installation executes server-side package
scripts with the installing role's privileges; install only reviewed extension
packages and follow each extension's trusted/superuser and `search_path`
guidance.

### PostgreSQL enum, domain, composite, range, and multirange types

Provider-owned enum, domain, standalone composite, range, and paired multirange
schema objects use typed model metadata and participate in migration diffing,
snapshots, generated migration C#, and database-first scaffolding:

```csharp
modelBuilder.HasBlueTuskEnum(
    "mood",
    ["sad", "ok", "happy"],
    schema: "application");

modelBuilder.HasBlueTuskDomain(
    "positive_integer",
    "integer",
    domain => domain
        .HasDefaultSql("1")
        .IsRequired()
        .HasCheckConstraint(
            "value_positive",
            "VALUE > 0",
            isValidated: false),
    schema: "application");

modelBuilder.HasBlueTuskComposite(
    "address",
    composite => composite
        .HasAttribute("street", "text")
        .HasAttribute("postal_code", "application.positive_integer"),
    schema: "application");

modelBuilder.HasBlueTuskRange(
    "measurement_range",
    "float8",
    range => range
        .UseSubtypeOperatorClass("float8_ops", "pg_catalog")
        .HasSubtypeDifferenceFunction("float8mi", "pg_catalog")
        .HasMultirangeType("measurement_multirange"),
    schema: "application",
    subtypeSchema: "pg_catalog");
```

Type names, enum labels, constraint names, and attribute names are quoted as
identifiers or literals. Store types, collations, `DefaultSql`, and domain check
expressions are trusted model-time SQL; never populate them from request data or
other untrusted input. Schema-qualified provider-owned store types are used to
order dependent creates and reverse-order drops. Drops use PostgreSQL's default
`RESTRICT` behavior and are marked destructive rather than silently adding
`CASCADE`.

The supported in-place alteration surface follows PostgreSQL's DDL limits:

- enum labels may be added at a specific position or renamed; removal and
  reordering require an explicit data-preserving replacement migration;
- domain defaults, nullability, and named check constraints may be added,
  dropped, renamed, replaced, or validated, while base type and collation
  changes require explicit replacement;
- composite attributes may be renamed, dropped, have their type/collation
  altered, or be appended; reordering existing attributes or inserting before
  them requires explicit replacement.

Enum `ADD VALUE` commands are transaction-suppressed so a new label can be used
by following migration commands. Automatic type/schema renames require an
otherwise unchanged definition; split a rename from a simultaneous body change
into separate migrations. Manual migrations can use the typed
`CreateBlueTusk*Type`, `AlterBlueTusk*Type`, `DropBlueTusk*Type`, and
`RenameBlueTuskUserDefinedType` helpers when a replacement or staged rollout is
needed.

Custom ranges retain structured, schema-qualified references to their subtype,
B-tree operator class, optional collation, optional canonical function,
optional subtype-difference function, and PostgreSQL-created multirange type.
When `HasMultirangeType` is omitted, BlueTusk uses PostgreSQL's naming rule:
the first `range` substring becomes `multirange`, or `_multirange` is appended.
Creates are ordered before domains, composites, routines, and tables that use
either the range or multirange name. Drops are destructive, explicitly use
`RESTRICT`, and rely on PostgreSQL to remove the paired multirange.

PostgreSQL treats the multirange as a separate type for `ALTER TYPE`; it does
not follow a range rename or schema move. BlueTusk therefore moves and renames
the multirange first and the range second. Changes to subtype, operator class,
collation, canonical function, or subtype-difference function cannot be made in
place and produce replacement guidance.

`HasCanonicalFunction` references a function that already exists when the
range is created. A canonical function whose argument or result is the new
range requires PostgreSQL's shell-type workflow: create the shell type, create
the function, and then complete the range definition. BlueTusk does not
synthesize that cycle from provider-owned routine metadata; use a staged manual
migration for it. Function SQL is trusted deployment input, while every type,
operator-class, collation, and function name in the range API is quoted as an
identifier.

### PostgreSQL functions and procedures

Provider-owned routines are separate from EF's `HasDbFunction` query mapping.
The routine schema API models PostgreSQL overload identity and generates actual
`CREATE FUNCTION`/`CREATE PROCEDURE` migrations:

```csharp
modelBuilder.HasBlueTuskFunction(
    "calculate_total",
    "numeric",
    "SELECT amount * (1 + tax_rate)",
    function => function
        .HasParameter("numeric", "amount")
        .HasParameter("numeric", "tax_rate", defaultSql: "0.2")
        .HasVolatility(BlueTuskFunctionVolatility.Immutable)
        .IsStrict()
        .HasParallelSafety(BlueTuskFunctionParallelSafety.Safe)
        .HasCost(1),
    schema: "application");

modelBuilder.HasBlueTuskProcedure(
    "record_call",
    "BEGIN INSERT INTO application.call_log(message) VALUES (message); END",
    procedure => procedure
        .UseLanguage("plpgsql")
        .HasParameter("text", "message"),
    schema: "application");
```

The typed builders cover ordered `IN`/`OUT`/`INOUT`/`VARIADIC` parameters,
defaults, scalar or `SETOF` function results, implementation language,
volatility, strict/null-input behavior, invoker/definer security, leakproof and
parallel classifications, planner cost/rows, and routine-local configuration.
Store types, parameter defaults, configuration values, and bodies are trusted
model-time SQL. Bodies are safely dollar-quoted, but their contents are not
sanitized; never derive them from request data.

Overloads are keyed by kind, schema, name, and PostgreSQL input argument types.
Initial migrations use `CREATE` so an unmanaged collision fails. Body, default,
language, and compatible attribute changes use `CREATE OR REPLACE`, preserving
the routine's ownership and privileges. PostgreSQL cannot replace a routine
while changing its kind, input signature, parameter name/mode/output shape,
function return type, or `WINDOW` status; BlueTusk diagnoses same-signature
changes and treats a different signature as destructive create/drop. Use the
signature-qualified `RenameBlueTuskRoutine` helper for dependency-preserving
name or schema changes.

User-defined types are created before routines and dropped after them. Quoted
string bodies are created before relational objects so tables may reference a
function in defaults or generated expressions. SQL-standard bodies discovered
through `prosqlbody` retain PostgreSQL's tracked dependencies and are instead
created after, and dropped before, relational objects.

`SECURITY DEFINER` routines require a carefully restricted `search_path`; use
`HasConfiguration("search_path", "application, pg_temp")` only with reviewed,
trusted SQL. Routine execute grants are not managed by this metadata and should
be applied with explicit `GRANT`/`REVOKE` migrations.

### PostgreSQL views and materialised views

Provider-owned views are schema objects rather than EF query mappings. Ordinary
and materialised definitions retain their trusted defining query, explicit
output names, and view-on-view dependencies in migrations, snapshots, generated
migration C#, and database-first scaffolding:

```csharp
modelBuilder.HasBlueTuskView(
    "active_orders",
    "SELECT id, tenant_id, total FROM application.orders WHERE total >= 0",
    view => view
        .HasColumns("id", "tenant_id", "total")
        .IsSecurityBarrier()
        .IsSecurityInvoker()
        .HasCheckOption(BlueTuskViewCheckOption.Cascaded),
    schema: "application");

modelBuilder.HasBlueTuskMaterializedView(
    "order_totals",
    "SELECT tenant_id, sum(total)::numeric AS total " +
        "FROM application.orders GROUP BY tenant_id",
    view => view
        .HasColumns("tenant_id", "total")
        .UseAccessMethod("heap")
        .HasStorageParameter("fillfactor", "80")
        .IsPopulated(),
    schema: "application");

modelBuilder.HasBlueTuskView(
    "large_order_totals",
    "SELECT tenant_id, total FROM application.order_totals WHERE total >= 1000",
    view => view
        .HasColumns("tenant_id", "total")
        .DependsOnView("order_totals", "application"),
    schema: "application");
```

`QuerySql` and storage-parameter values are trusted model-time SQL and must not
contain request data or other untrusted input. Names are quoted centrally.
Ordinary builders also support PostgreSQL's recursive form; recursive views
require explicit output names and cannot use `CHECK OPTION`.

Ordinary query and option changes use `CREATE OR REPLACE VIEW`. BlueTusk rejects
an explicit output-list change that renames, removes, or reorders existing
columns; PostgreSQL also validates that existing output types remain unchanged
and permits only new columns appended at the end. Replacements explicitly reset
removed `security_barrier`, `security_invoker`, and `check_option` settings.
Name and schema-only changes use `ALTER VIEW`/`ALTER MATERIALIZED VIEW`, while
drops retain PostgreSQL's default `RESTRICT` behavior rather than silently
adding `CASCADE`.

PostgreSQL cannot replace a materialised view's defining query in place. A query
or output-list change is therefore marked destructive and emits a dependency-
ordered drop/create. Provider-owned views that declare a transitive dependency
on the replaced materialised view are dropped first and reconstructed after it.
Declare model-authored view dependencies with `DependsOnView`; reverse
engineering derives the same edges from PostgreSQL's catalogues. Access method,
tablespace, storage-parameter, and populated/unpopulated changes use supported
`ALTER MATERIALIZED VIEW` and `REFRESH MATERIALIZED VIEW` forms without replacing
the definition.

Manual refreshes use typed migration operations:

```csharp
migrationBuilder.RefreshBlueTuskMaterializedView(
    "order_totals",
    schema: "application",
    concurrently: true);
```

PostgreSQL requires a populated materialised view and at least one all-row,
column-only unique index for `CONCURRENTLY`; it rejects `CONCURRENTLY WITH NO
DATA` and allows only one refresh of a materialised view at a time. Create the
required index separately with a normal migration index operation. A no-data
refresh is marked destructive because it discards the stored contents and leaves
the relation unscannable.

The schema metadata deliberately does not manage owners, privileges, or
application-specific grants. Apply those through explicit migrations. Defining
queries are dependency-tracked by PostgreSQL, and `security_invoker` changes
whose privileges and row-level-security policies apply to underlying relations;
review both the query and grants as security-sensitive schema.

PostgreSQL 19 property graphs have typed model metadata, migration diffing and operation scaffolding, central identifier quoting, live `CREATE`/`ALTER`/`DROP PROPERTY GRAPH` coverage, and an execution-time SQL/PGQ capability guard. The optional citext EF package also provides explicit `EnsureBlueTuskCitext` and `DropBlueTuskCitext` migration operations. Other PostgreSQL-specific schema features remain in progress. See [the executable roadmap](../roadmap.md) for the exact status.

## PostgreSQL 19 property-graph queries

`PropertyGraph` creates a typed SQL/PGQ query from graph metadata configured in
the EF model. The preview translates linear directed paths to `GRAPH_TABLE`,
keeps captured predicate values parameterized, and returns a composable
`IQueryable`. It supports outer relational filters, joins, grouping, ordering,
pagination, DTO projections, and tracked entity materialization:

```csharp
var friends = await context.PropertyGraph("social", "application")
    .Match(pattern => pattern
        .Vertex<Person>("source", person => person.Id == personId)
        .Outgoing<Friendship>("edge")
        .Vertex<Person>("target"))
    .Select<FriendResult>(projection => projection
        .Property<Person, int>(
            "target", person => person.Id, result => result.PersonId)
        .Property<Person, string>(
            "target", person => person.Name, result => result.Name))
    .OrderBy(result => result.Name)
    .ToListAsync(cancellationToken);
```

The exact supported expression subset and raw-SQL-only remainder are documented
in the [SQL/PGQ guide](../graph/README.md).

## Database-first scaffolding

The design-time provider integrates with EF Core reverse engineering. It discovers ordinary tables and views, columns and PostgreSQL store types, defaults and generated values, primary and unique keys, foreign keys, exclusion constraints, indexes, comments, standalone sequences, provider-owned collations, installed extensions, declarative partition trees, direct table-inheritance parents, row-level security policies, provider-owned enums, domains, standalone composite, range, and multirange types, functions, procedures, and PostgreSQL 19 property graphs. Column-based indexes retain their access method, operator classes, collations, sort/null ordering, included columns, null-distinctness, storage parameters, and predicate; generated contexts use the BlueTusk fluent index APIs for those annotations. Exclusion constraints retain their access method, ordered canonical elements and exact operators, included columns, storage settings, tablespace, predicate, and deferrability without duplicating their backing indexes. Collation discovery retains the provider, locale categories, determinism, ICU rules, and recorded version while excluding system and extension-owned objects. Installed-extension discovery retains the exact version, installation schema, and extension dependency edges while excluding extensions installed into system schemas. Partition discovery retains PostgreSQL's exact catalogue key and bound expressions, including empty partitioned tables and recursive subpartitions. Child partitions are represented inside the root's fluent metadata instead of being scaffolded as unrelated EF entities. Direct inheritance discovery retains ordered multiple parents while excluding declarative-partition catalogue edges. RLS discovery retains enable/force flags, permissive/restrictive behavior, command scopes, roles, and catalogue-rendered `USING`/`WITH CHECK` expressions. User-defined-type discovery retains enum order, domain base/default/nullability/collation/check state, ordered composite attributes, and range subtype/operator-class/collation/function/multirange identities while excluding table row types, system schemas, and extension-owned types. Routine discovery retains overload identity, arguments/defaults, results, window status, tracked-body dependency phase, and the server's canonical `pg_get_functiondef` DDL; aggregates, system routines, and extension-owned routines are excluded. View discovery retains the stable, non-pretty `pg_get_viewdef` query, ordered output names, security/check options, materialisation kind, access method, storage parameters, tablespace, population state, and view-on-view dependency edges while excluding system and extension-owned relations. Graph metadata includes vertex and edge tables, keys, labels, properties, and source/destination column mappings. Sequence metadata is read directly from PostgreSQL's catalogues, avoiding the relation-opening behavior of `pg_sequences` when another session is concurrently changing schema. Schema and table filters are supported, and caller-owned open connections remain open.

```bash
dotnet ef dbcontext scaffold \
  "Host=localhost;Database=app;Username=app;Password=..." \
  BlueTusk.EntityFrameworkCore \
  --context AppDbContext \
  --output-dir Models \
  --schema public
```

Generated contexts configure `UseBlueTusk`. Reverse-engineered exclusion constraints, collations, installed extensions, graphs, partition trees, table-inheritance relationships, RLS policies, enums, domains, standalone composites, ranges and paired multiranges, functions, procedures, ordinary views, and materialised views are retained through provider model annotations and participate in later migration diffs. Expression-index creation is supported from model metadata, but standalone expression indexes are not scaffolded yet because EF requires a mapped-property key; canonical expression elements owned by exclusion constraints are retained. PostgreSQL-complete discovery—including standalone expression indexes, privileges, aggregates, and other executable schema objects—remains a separate roadmap item.

## Validation

The PostgreSQL 15–19 view gate verifies security/check enforcement, dependency
ordering, normal and concurrent materialised refresh, constrained replacement,
auxiliary alteration, rename, canonical catalogue discovery, and generated
fluent C#.

The PostgreSQL 15–19 extension gate verifies installation, dependency and schema
ordering, exact version/schema catalogue round-tripping, generated fluent C#,
relocation, and default-`RESTRICT` removal.

The PostgreSQL 15–19 collation gate verifies ICU comparison behavior,
collation-first ordering, safe rename/schema moves, exact cross-version
catalogue discovery, generated fluent C#, default-`RESTRICT` removal,
PostgreSQL 16+ ICU rules, and PostgreSQL 17+ built-in-provider guards.

The PostgreSQL 15–19 custom-range gate verifies executable range and multirange
values, dependency-ordered creation, pair-aware rename/schema moves,
default-`RESTRICT` removal, exact `pg_range` discovery, and generated fluent C#.

The PostgreSQL 15–19 exclusion-constraint gate verifies live overlap rejection,
partial-predicate behavior, included columns and storage settings, exact
`pg_constraint`/index discovery, generated fluent C#, constraint rename, and
default-`RESTRICT` removal.

The provider gate runs against PostgreSQL and covers service lifetimes, core and wire-native scalar mappings, generated values and concurrency, CRUD and transactions, common LINQ and compiled queries, raw SQL composition and parameters, tracking modes and identity resolution, split-query includes and relationship fix-up, bulk update/delete, schema creation, migrations and idempotent scripts, advanced index creation/deletion, declarative partition lifecycles, direct table inheritance, row-level security enforcement, catalogue round-tripping, and database-first C# generation. Advanced index acceptance runs on PostgreSQL 15–19 and verifies expression/partial keys, access methods, operator classes, collations, sort/null ordering, included columns, null-distinctness, storage parameters, and transaction-suppressed concurrent operations. Partition acceptance on the same server matrix verifies RANGE/LIST/HASH DDL, recursive row routing, default partitions, typed bounds, destructive-change diagnostics, exact catalogue discovery, generated fluent C#, and attach/detach operations. Table-inheritance acceptance verifies ordered multiple parents, inherited versus `ONLY` scans, add/remove lifecycle SQL, rename-aware diffs, `pg_inherits` discovery, and generated fluent C# across PostgreSQL 15–19. RLS acceptance verifies non-owner tenant filtering, successful and rejected `WITH CHECK` inserts, active enable/force state, policy lifecycle SQL, catalogue discovery, and generated fluent C# on PostgreSQL 15–19. User-defined-type acceptance on the same matrix verifies dependency-ordered enum/domain/composite creation, runtime enforcement, transaction-suppressed enum additions, supported alterations and renames, destructive diagnostics, exact catalogue discovery, and generated fluent C#. Routine acceptance across PostgreSQL 15–19 verifies overloaded functions, default arguments, optimizer/null/parallel attributes, PL/pgSQL procedures, UDT and relational dependency phases, signature-qualified lifecycle operations, canonical catalogue discovery, and generated fluent C#. The native type gate round-trips network, geometric, bit-string, LSN, arbitrary-numeric, temporal, full-text, JSON/JSONB/XML, JSON-path, array, range, multirange, enum, domain, typed composite, and lossless record values through EF. The PostgreSQL-specific query gate executes parameterized operator predicates, the documented scalar-function subset, typed array/string/boolean/range aggregates, lateral array expansion, typed series and JSONB roots, integer/text multi-array expansion, and model-registered user-defined table functions across PostgreSQL 15–19. Aggregate ordering, `DISTINCT`, and `FILTER`, plus single/multi-array `unnest` filtering, ordinality, nullable elements, null padding, inner/outer lateral composition, standalone/correlated/compiled `generate_series`, JSONB element/key/path/pair/recordset expansion, and schema-qualified typed table-function materialization are covered in generated SQL and live execution; remaining aggregates, set-returning functions, and scalar functions are still in progress.
