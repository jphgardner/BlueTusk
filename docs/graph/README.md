# Query a PostgreSQL property graph

> **Preview, not part of 1.1.0.** Graph needs a PostgreSQL server that
> provides SQL/PGQ. PostgreSQL removed SQL/PGQ in PostgreSQL 19 Beta 4, so this
> feature waits for a PostgreSQL release that ships it. Do not use it in
> production. See [product status](../getting-started/install.md#product-status).

PostgreSQL SQL/PGQ lets you describe vertices and edges over ordinary tables
and query relationships with `GRAPH_TABLE`. BlueTusk supports raw parameterized
SQL, typed schema discovery, EF model/migrations, and a bounded typed EF query
builder.

This feature requires the server to report SQL/PGQ capability. Do not enable it
from a PostgreSQL version string alone. SQL/PGQ was removed in PostgreSQL 19
Beta 4. This is preview functionality on the pinned historical Beta 3 fixture,
not a production PostgreSQL 19 feature. Keep it on the separate
[Graph release track](../releases/release-tracks.md); other products do not wait
for its future availability.

## Run the complete example

```powershell
docker compose -f eng/compose/postgres.yml --profile preview up -d postgres19
$env:BLUETUSK_CONNECTION_STRING = "Host=localhost;Port=5419;Username=postgres;Password=postgres;Database=bluetusk_tests;SSL Mode=Disable;Channel Binding=Disable"
dotnet run --project samples/BlueTusk.Samples.Graph
```

The sample creates temporary tables and a temporary graph, queries one edge,
prints `Ada knows Grace`, and removes the graph. TLS is disabled only for the
isolated local container.

## 1. Check capability

```csharp
await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();
await using var connection = await dataSource.OpenConnectionAsync();

if (connection.ServerCapabilities is not { SupportsSqlPgq: true })
    throw new NotSupportedException("This PostgreSQL server does not expose SQL/PGQ.");
```

BlueTusk probes PostgreSQL's documented information-schema graph views. A major
version check is not sufficient.

## 2. Define a graph over relational tables

```sql
CREATE PROPERTY GRAPH people_graph
    VERTEX TABLES (app.people KEY (id) LABEL person)
    EDGE TABLES (
        app.knows KEY (id)
            SOURCE KEY (source_id) REFERENCES app.people (id)
            DESTINATION KEY (destination_id) REFERENCES app.people (id)
            LABEL knows);
```

The relational tables remain authoritative. A property graph defines how their
keys, labels, endpoints, and properties form a graph view.

## 3. Query it safely

```csharp
await using var command = connection.CreateCommand();
command.CommandText =
    """
    SELECT source_name, destination_name
    FROM GRAPH_TABLE (
        people_graph
        MATCH (source IS person)-[IS knows]->(destination IS person)
        COLUMNS (
            source.name AS source_name,
            destination.name AS destination_name))
    WHERE source_name = @name
    """;
command.Parameters.Add(new BlueTuskParameter<string>("Ada")
{
    ParameterName = "name",
});

await using var reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
    Console.WriteLine($"{reader.GetString(0)} knows {reader.GetString(1)}");
```

Parameters are bound outside the SQL text. Graph names and labels are schema
identifiers and should come from trusted application configuration, not user
input.

## 4. Inspect an existing graph

```csharp
var inspector = new BlueTuskPropertyGraphSchemaInspector(dataSource);
var graphs = await inspector.InspectAsync(
    new BlueTuskPropertyGraphInspectionOptions
    {
        Schema = "app",
        Name = "people_graph",
    },
    cancellationToken);
```

Use discovery for diagnostics, tooling, or validation. Define production graph
changes through reviewed migrations.

## What to use next

- Use EF graph configuration when the application owns graph migrations.
- Use the typed EF graph builder when you need compile-time entity/property
  selection and a supported bounded pattern.
- Use [Continuous Graph](../continuous-graph/README.md) when a bounded graph
  result must remain current after committed changes.

The [SQL/PGQ reference](reference.md) documents EF configuration, migrations,
reverse engineering, typed matching, the exact supported query subset, and
the PostgreSQL 19 verification boundary.
