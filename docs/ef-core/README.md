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

var documents = await context.Documents
    .Where(document =>
        EF.Functions.ILike(document.Name, "blue%")
        && EF.Functions.ArrayContains(document.Tags, requiredTags)
        && EF.Functions.RangeContains(document.ValidIds, activeWindow)
        && EF.Functions.JsonContains(document.Metadata, jsonFilter))
    .ToListAsync(cancellationToken);
```

The preview covers:

- text `ILIKE`, case-sensitive `~`, and case-insensitive `~*`;
- array containment (`@>`, `<@`) and overlap (`&&`);
- range containment, element containment, overlap, strict left/right, and
  adjacency, plus multirange containment and overlap;
- JSONB containment and key tests, and JSONPath `@?`/`@@`;
- `inet`/`cidr` containment and overlap; and
- `tsvector @@ tsquery` full-text matching.

These methods deliberately throw if evaluated as ordinary CLR methods. A query
must translate completely, and SQL null behavior follows the underlying
PostgreSQL operator rather than pretending to be an in-memory implementation.
Operator behavior is defined by PostgreSQL's
[pattern](https://www.postgresql.org/docs/current/functions-matching.html),
[array](https://www.postgresql.org/docs/current/functions-array.html),
[range/multirange](https://www.postgresql.org/docs/current/functions-range.html),
[JSON](https://www.postgresql.org/docs/current/functions-json.html),
[network](https://www.postgresql.org/docs/current/functions-net.html), and
[text-search](https://www.postgresql.org/docs/current/functions-textsearch.html)
documentation. SQL-generation tests cover every exposed operator family, and
live acceptance executes typed parameters against PostgreSQL 15–19.

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

The provider gate runs against PostgreSQL and covers service lifetimes, core and wire-native scalar mappings, generated values and concurrency, CRUD and transactions, common LINQ and compiled queries, raw SQL composition and parameters, tracking modes and identity resolution, split-query includes and relationship fix-up, bulk update/delete, schema creation, migrations and idempotent scripts, and database-first C# generation. The native type gate round-trips network, geometric, bit-string, LSN, arbitrary-numeric, temporal, full-text, JSON/JSONB/XML, JSON-path, array, range, multirange, enum, domain, typed composite, and lossless record values through EF. The PostgreSQL-specific query gate executes parameterized operator predicates across PostgreSQL 15–19; scalar, aggregate, and set-returning function work remains in progress.
