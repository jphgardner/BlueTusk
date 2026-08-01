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
configuration applies to the current no-configuration overloads. Geometric,
date/time, and remaining scalar functions are still planned and are not
implied by this preview.

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
aggregate has no selected input rows. JSON/statistical/ordered-set aggregates,
and remaining aggregates remain planned.

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
joins, and captured JSON/JSONPath values remain parameters. Arbitrary
`jsonb_to_recordset` shapes remain planned.

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
combinations, and general user-defined table functions, remain planned.

## Migrations

`Database.GenerateCreateScript()` and `IRelationalDatabaseCreator.CreateTables()` generate PostgreSQL DDL for ordinary relational models. The supported create-schema surface includes tables, primary and foreign keys, indexes, defaults, length and precision facets, and `GENERATED BY DEFAULT AS IDENTITY` integer keys. This path is covered both by SQL-shape tests and by executing the generated commands against PostgreSQL.

Runtime migrations support the PostgreSQL `__EFMigrationsHistory` repository, transaction-scoped migration locking, up/down application, and idempotent scripts. The initial DDL surface covers tables, columns, keys and constraints, indexes, sequences, defaults, comments, schema moves, and alter/rename/drop operations. Acceptance tests apply an idempotent script twice, re-enter `Database.MigrateAsync()`, move back to an earlier migration, and finally revert to the empty database.

PostgreSQL 19 property graphs have typed model metadata, migration diffing and operation scaffolding, central identifier quoting, live `CREATE`/`ALTER`/`DROP PROPERTY GRAPH` coverage, and an execution-time SQL/PGQ capability guard. The optional citext EF package also provides explicit `EnsureBlueTuskCitext` and `DropBlueTuskCitext` migration operations. General extension modelling and other PostgreSQL-specific schema features such as enum types, operator classes, table partitioning, and row-level security remain in progress. See [the executable roadmap](../roadmap.md) for the exact status.

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

The design-time provider integrates with EF Core reverse engineering. It discovers ordinary tables and views, columns and PostgreSQL store types, defaults and generated values, primary and unique keys, foreign keys, indexes, comments, standalone sequences, and PostgreSQL 19 property graphs. Graph metadata includes vertex and edge tables, keys, labels, properties, and source/destination column mappings. Sequence metadata is read directly from PostgreSQL's catalogues, avoiding the relation-opening behavior of `pg_sequences` when another session is concurrently changing schema. Schema and table filters are supported, and caller-owned open connections remain open.

```bash
dotnet ef dbcontext scaffold \
  "Host=localhost;Database=app;Username=app;Password=..." \
  BlueTusk.EntityFrameworkCore \
  --context AppDbContext \
  --output-dir Models \
  --schema public
```

Generated contexts configure `UseBlueTusk`. Reverse-engineered graphs are retained through a provider model annotation and participate in later migration diffs. PostgreSQL-complete discovery—including extensions, enums, domains, composite and range types, expression indexes, partition metadata, policies, and routines—belongs to the advanced scaffolding milestone.

## Validation

The provider gate runs against PostgreSQL and covers service lifetimes, core and wire-native scalar mappings, generated values and concurrency, CRUD and transactions, common LINQ and compiled queries, raw SQL composition and parameters, tracking modes and identity resolution, split-query includes and relationship fix-up, bulk update/delete, schema creation, migrations and idempotent scripts, and database-first C# generation. The native type gate round-trips network, geometric, bit-string, LSN, arbitrary-numeric, temporal, full-text, JSON/JSONB/XML, JSON-path, array, range, multirange, enum, domain, typed composite, and lossless record values through EF. The PostgreSQL-specific query gate executes parameterized operator predicates, the documented scalar-function subset, typed array/string/boolean/range aggregates, lateral array expansion, typed series and JSONB roots, and integer/text multi-array expansion across PostgreSQL 15–19. Aggregate ordering, `DISTINCT`, and `FILTER`, plus single/multi-array `unnest` filtering, ordinality, nullable elements, null padding, inner/outer lateral composition, standalone/correlated/compiled `generate_series`, and JSONB element/key/path/pair expansion are covered in generated SQL and live execution; remaining aggregates, set-returning functions, and scalar functions are still in progress.
