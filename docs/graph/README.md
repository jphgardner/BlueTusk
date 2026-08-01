# PostgreSQL 19 SQL/PGQ preview

BlueTusk `0.3.0-preview.1` supports PostgreSQL 19 SQL/PGQ through raw ADO.NET
SQL. ADO.NET sessions enable `SupportsSqlPgq` only after probing the documented
`information_schema.property_graphs` view; a major-version check alone is not
used as evidence.

The live PostgreSQL 19 Beta 2 acceptance test covers:

- `CREATE TEMP PROPERTY GRAPH`, `ALTER PROPERTY GRAPH`, and
  `DROP PROPERTY GRAPH`;
- discovery through `information_schema.property_graphs`;
- `GRAPH_TABLE` vertex and directed-edge patterns;
- prepared and typed parameters applied to graph results;
- field metadata, batches, mixed relational/graph statements, and
  data-source pooling; and
- the normal provider suite, including cancellation, pipeline mode, types,
  COPY, replication, and EF regression coverage.

The official PostgreSQL references are [Property
Graphs](https://www.postgresql.org/docs/19/ddl-property-graphs.html), [Graph
Queries](https://www.postgresql.org/docs/19/queries-graph.html), [`CREATE
PROPERTY GRAPH`](https://www.postgresql.org/docs/19/sql-create-property-graph.html),
and [`information_schema.property_graphs`](https://www.postgresql.org/docs/19/infoschema-property-graphs.html).

## Raw SQL example

The executable `BlueTusk.Samples.Graph` project checks the capability, creates
temporary vertex/edge tables, defines a property graph, and reads a directed
edge through `GRAPH_TABLE`:

```powershell
$env:BLUETUSK_CONNECTION_STRING = "Host=localhost;Port=5419;Username=postgres;Password=postgres;Database=bluetusk_tests;SSL Mode=Disable;Channel Binding=Disable"
dotnet run --project samples/BlueTusk.Samples.Graph
```

Typed graph metadata, migrations, reverse engineering, query roots, and EF
translation remain planned. Applications must not interpolate identifiers or
graph patterns from untrusted input; parameters can represent values, not SQL
grammar.
