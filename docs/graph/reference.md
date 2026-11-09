# BlueTusk Graphs reference

Use `HasPropertyGraph` with the default BlueTusk Graphs backend for portable
typed queries. Vertex and edge definitions describe tables, keys, labels, and
exposed scalar columns. Queries run on those tables under the caller's database
role. EF global filters on the source entity do not automatically apply to
the graph SQL. Put tenant and authorization checks in PostgreSQL row-level
security or in the authorized graph query.

Native SQL/PGQ is selected through the EF context's graph compatibility option.
It can also be used directly through the command APIs. ADO.NET detects
`SupportsSqlPgq` through the server catalogue. Native release evidence is
tracked in the [PostgreSQL compatibility programme](../postgresql19-programme.md).

The live PostgreSQL 19 Beta 3 acceptance tests cover:

- `CREATE PROPERTY GRAPH`, `CREATE TEMP PROPERTY GRAPH`, `ALTER PROPERTY GRAPH`,
  and `DROP PROPERTY GRAPH`;
- `GRAPH_TABLE` vertex and directed-edge patterns;
- prepared and typed value parameters applied to graph results;
- field metadata, batches, mixed relational/graph statements, and data-source
  pooling;
- typed discovery of graphs, vertex and edge tables, key columns, labels,
  element properties, edge endpoints, column mappings, and property data types;
- typed EF model configuration, migration diffing/scaffolding, and database-first
  graph retention;
- live capability-guarded and centrally quoted EF migration SQL for graph
  creation, rename/schema alteration, and removal;
- typed EF linear-path matching, parameterized predicates, DTO and tracked-entity
  materialization, and relational composition;
- capability-guarded empty discovery results on PostgreSQL 15–18; and
- the normal provider suite, including cancellation, pipeline mode, types,
  COPY, replication, and EF regression coverage.

Native discovery reads the information-schema views exposed by the historical
SQL/PGQ fixture. These include `property_graphs`, `pg_element_tables`,
`pg_element_table_key_columns`, `pg_element_table_labels`,
`pg_element_table_properties`, `pg_edge_table_components`, `pg_labels`,
`pg_label_properties`, and `pg_property_data_types`. Portable graphs are
defined in EF metadata and do not use these views.

## Native raw SQL example

For the portable EF sample, follow the [Graph quickstart](README.md#run-the-sample).
Use `GRAPH_TABLE` directly only after `SupportsSqlPgq` succeeds and the native
property graph exists. For example, given a graph named `social_graph` with
`person` vertices and `follows` edges:

```sql
SELECT *
FROM GRAPH_TABLE (
  social_graph
  MATCH (source IS person)-[edge IS follows]->(target IS person)
  COLUMNS (source.name AS source_name, target.name AS target_name)
);
```

## Typed schema discovery

Use a long-lived `BlueTuskDataSource` for normal application discovery. The
inspector opens and returns its own pooled connection:

```csharp
using BlueTusk.Data;
using BlueTusk.Data.Schema;

await using var dataSource = BlueTuskDataSource.Create(connectionString);
var inspector = new BlueTuskPropertyGraphSchemaInspector(dataSource);
var graphs = await inspector.InspectAsync(
    new BlueTuskPropertyGraphInspectionOptions
    {
        Schema = "application",
        Name = "social",
    },
    cancellationToken);
```

Construct the inspector with an already-open `BlueTuskConnection` when session
scope matters, including discovery of a temporary property graph. That overload
does not open, close, or own the connection. Both constructors provide genuine
synchronous and asynchronous inspection methods. Filters match PostgreSQL's
reported catalogue, schema, and graph names exactly.

The `BlueTusk.SchemaInspector` executable renders the same model as readable
text or JSON. Prefer the environment variable so credentials do not enter shell
history:

```powershell
$fixturePassword = docker compose -f eng/compose/postgres.yml exec -T postgres19 printenv POSTGRES_PASSWORD
$env:BLUETUSK_CONNECTION_STRING = "Host=localhost;Port=5419;Username=postgres;Password=$fixturePassword;Database=bluetusk_tests;SSL Mode=Disable;Channel Binding=Disable"
dotnet run --project tooling/BlueTusk.SchemaInspector -- --schema application --graph social
dotnet run --project tooling/BlueTusk.SchemaInspector -- --schema application --json
```

The tool reports an empty graph collection and `supportsSqlPgq: false` on
servers without SQL/PGQ instead of querying views that do not exist.

## EF model, migrations, and reverse engineering

Configure relational table/column mappings before adding the graph. The graph
builder accepts only direct mapped-property selectors, so identifiers are
resolved from EF metadata rather than caller-authored SQL:

```csharp
modelBuilder.HasPropertyGraph(
    "social",
    graph =>
    {
        graph.Vertex<Person>("people", vertex => vertex
            .HasLabel("person")
            .HasKey(person => person.Id)
            .Properties(person => new { person.Id, person.Name }));

        graph.Edge<Friendship>("friendships", edge => edge
            .HasLabel("follows")
            .HasKey(friendship => friendship.Id)
            .Properties(friendship => new { friendship.Id, friendship.Since })
            .HasSource<Person>(
                friendship => friendship.FromPersonId,
                person => person.Id)
            .HasDestination<Person>(
                friendship => friendship.ToPersonId,
                person => person.Id));
    },
    schema: "application");
```

The model snapshot stores the resolved backend. Portable graph definitions emit
ordinary table and index migrations. In native mode, migration diffing emits
graph creation after its relational tables and graph removal before table
removal. A graph rename or schema move emits `ALTER PROPERTY
GRAPH`; changing an element definition emits drop/create because PostgreSQL's
individual label/element alterations cannot represent every model change as
one atomic command.

Generated migration C# uses the public fluent graph builder rather than a
serialized definition string. It describes vertices, edges, labels,
properties, keys and endpoints directly and validates the definition before
adding the operation. The strongly typed model builder above remains the
preferred source of truth; the migration builder makes the generated schema
change readable and reviewable.

Use the same compatibility setting at runtime and design time. Legacy snapshots
with no backend describe native graphs. To retain native migrations, set
`UseGraphCompatibility(BlueTuskPropertyGraphBackend.NativeSqlPgq)` in both
configurations. Maintain separate migration histories if you deploy both modes.
For a portable deployment with a native migration history, establish a reviewed
relational baseline from the existing schema and migration history. Fresh
portable installations must start with a migration sequence that contains
portable table DDL. An existing preview database may need a guarded migration
to drop its native graph object before changing dependent tables.

All generated graph DDL is executed inside a server-side guard. PostgreSQL
15–18 receive SQLSTATE `0A000` with a BlueTusk-specific requirement message;
the PostgreSQL 19 DDL is dynamic inside the guard so an older parser never sees
SQL/PGQ syntax. Graph, schema, table, alias, key-column, label, and property
identifiers use the provider's central SQL delimiter. Live tests include spaces
and embedded double quotes in every relevant identifier category.

Database-first scaffolding reuses the same documented information-schema
reader and retains discovered graphs in generated context code through
`HasPropertyGraphs`. The retained metadata participates in subsequent
migration diffs and typed query translation.

## Typed EF graph queries

Start a graph query from its configured model name. Pattern variables are
caller-chosen identifiers, while labels, properties, element aliases, and
tables are resolved from the typed EF graph metadata and quoted by the
provider. Captured values become ordinary EF parameters:

```csharp
var query = db.PropertyGraph("social", "application")
    .Match(pattern => pattern
        .Vertex<Person>("source", person => person.Id == personId)
        .Outgoing<Friendship>("relationship")
        .Vertex<Person>("target"))
    .Select<FriendResult>(projection => projection
        .Property<Person, int>(
            "source", person => person.Id, result => result.SourceId)
        .Property<Person, string>(
            "source", person => person.Name, result => result.SourceName)
        .Property<Friendship, DateOnly>(
            "relationship", edge => edge.Since, result => result.Since)
        .Property<Person, int>(
            "target", person => person.Id, result => result.TargetId)
        .Property<Person, string>(
            "target", person => person.Name, result => result.TargetName))
    .Where(result => result.TargetName != "blocked")
    .OrderBy(result => result.TargetName)
    .Take(20);

var friends = await query.ToListAsync(cancellationToken);
```

`Incoming<TEdge>` reverses a directed traversal. When one CLR entity maps to
more than one graph element table, pass the configured element alias to
`Vertex` or `Outgoing`/`Incoming` to disambiguate it.

The projection target can be an unmapped class, as above. It can also be a
mapped EF entity; every mapped scalar property must then be projected, and EF
applies its normal tracking behavior. The resulting `IQueryable` supports
ordinary outer LINQ composition, including filters, joins, grouping, sorting,
pagination, and further projections.

## Exact V1 typed-subset boundary

Supported now:

- all SQL/PGQ syntax accepted by PostgreSQL can pass through the ordinary
  command and batch APIs as caller-authored raw SQL;
- values outside graph grammar can use ordinary positional or named BlueTusk
  parameters;
- the typed schema model and inspector are read-only discovery APIs;
- EF models, migrations, and database-first scaffolding retain property-graph
  schema semantics; and
- typed EF queries support linear vertex/edge patterns, both edge directions,
  undirected paths, label OR matching, and direct scalar property
  projections, and vertex predicates made from direct property comparisons to
  captured or constant values joined by `&&` or `||`.

Typed paths can span one to eight hops per bounded segment and at most 64
fixed branches. Unbounded paths and predicates beyond the documented comparison
subset require other SQL. The portable renderer supports column-valued
properties. Native SQL can express additional graph constructs. Unsupported
typed constructs raise `BlueTuskGraphTranslationException`; there is no string
concatenation fallback. Schema inspection remains read-only.

Applications using raw SQL must not interpolate untrusted identifiers or graph
patterns. The core PostgreSQL references are [Property Graphs](https://www.postgresql.org/docs/19/ddl-property-graphs.html),
[Graph Queries](https://www.postgresql.org/docs/19/queries-graph.html), and
[`CREATE PROPERTY GRAPH`](https://www.postgresql.org/docs/19/sql-create-property-graph.html).
