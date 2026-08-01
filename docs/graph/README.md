# PostgreSQL 19 SQL/PGQ preview

BlueTusk `0.3.0-preview.1` supports PostgreSQL 19 SQL/PGQ through raw ADO.NET
SQL and exposes typed, read-only property-graph schema discovery. ADO.NET
sessions enable `SupportsSqlPgq` only after probing the documented
`information_schema.property_graphs` view; a major-version check alone is not
used as evidence.

The live PostgreSQL 19 Beta 2 acceptance tests cover:

- `CREATE PROPERTY GRAPH`, `CREATE TEMP PROPERTY GRAPH`, `ALTER PROPERTY GRAPH`,
  and `DROP PROPERTY GRAPH`;
- `GRAPH_TABLE` vertex and directed-edge patterns;
- prepared and typed value parameters applied to graph results;
- field metadata, batches, mixed relational/graph statements, and data-source
  pooling;
- typed discovery of graphs, vertex and edge tables, key columns, labels,
  element properties, edge endpoints, column mappings, and property data types;
- capability-guarded empty discovery results on PostgreSQL 15–18; and
- the normal provider suite, including cancellation, pipeline mode, types,
  COPY, replication, and EF regression coverage.

The discovery API reads only PostgreSQL 19's documented information-schema
views: [`property_graphs`](https://www.postgresql.org/docs/19/infoschema-property-graphs.html),
[`pg_element_tables`](https://www.postgresql.org/docs/19/infoschema-pg-element-tables.html),
[`pg_element_table_key_columns`](https://www.postgresql.org/docs/19/infoschema-pg-element-table-key-columns.html),
[`pg_element_table_labels`](https://www.postgresql.org/docs/19/infoschema-pg-element-table-labels.html),
[`pg_element_table_properties`](https://www.postgresql.org/docs/19/infoschema-pg-element-table-properties.html),
[`pg_edge_table_components`](https://www.postgresql.org/docs/19/infoschema-pg-edge-table-components.html),
[`pg_labels`](https://www.postgresql.org/docs/19/infoschema-pg-labels.html),
[`pg_label_properties`](https://www.postgresql.org/docs/19/infoschema-pg-label-properties.html),
and [`pg_property_data_types`](https://www.postgresql.org/docs/19/infoschema-pg-property-data-types.html).

## Raw SQL example

The executable `BlueTusk.Samples.Graph` project checks the capability, creates
temporary vertex/edge tables, defines a property graph, and reads a directed
edge through `GRAPH_TABLE`:

```powershell
$env:BLUETUSK_CONNECTION_STRING = "Host=localhost;Port=5419;Username=postgres;Password=postgres;Database=bluetusk_tests;SSL Mode=Disable;Channel Binding=Disable"
dotnet run --project samples/BlueTusk.Samples.Graph
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
$env:BLUETUSK_CONNECTION_STRING = "Host=localhost;Port=5419;Username=postgres;Password=postgres;Database=bluetusk_tests;SSL Mode=Disable;Channel Binding=Disable"
dotnet run --project tooling/BlueTusk.SchemaInspector -- --schema application --graph social
dotnet run --project tooling/BlueTusk.SchemaInspector -- --schema application --json
```

The tool reports an empty graph collection and `supportsSqlPgq: false` on
servers without SQL/PGQ instead of querying views that do not exist.

## Exact preview boundary

Supported now:

- all SQL/PGQ syntax accepted by PostgreSQL can pass through the ordinary
  command and batch APIs as caller-authored raw SQL;
- values outside graph grammar can use ordinary positional or named BlueTusk
  parameters; and
- the typed schema model and inspector are read-only discovery APIs.

Still raw-SQL-only or planned:

- graph names, labels, property names, graph patterns, and other SQL grammar
  cannot be parameters and have no typed query builder yet;
- EF Core does not yet translate typed graph query roots or `GRAPH_TABLE`;
- graph-aware EF migrations and reverse engineering are not yet implemented;
  and
- schema inspection does not create, alter, or drop graphs.

Applications must not interpolate untrusted identifiers or graph patterns.
Identifier quoting, capability-guarded EF migrations/reverse engineering, and
typed EF graph translation remain tracked as separate roadmap gates. The core
PostgreSQL references are [Property Graphs](https://www.postgresql.org/docs/19/ddl-property-graphs.html),
[Graph Queries](https://www.postgresql.org/docs/19/queries-graph.html), and
[`CREATE PROPERTY GRAPH`](https://www.postgresql.org/docs/19/sql-create-property-graph.html).
