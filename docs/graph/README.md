# Query connected data with BlueTusk Graphs

> **Preview.** Stable release qualification is tracked separately.

BlueTusk Graphs turns EF graph patterns into parameterized PostgreSQL joins.
Map your existing tables as vertices and edges with `HasPropertyGraph`, then
query them with `PropertyGraph(...).Match(...).Select(...)`. The default
backend works on PostgreSQL 15–18 without SQL/PGQ, a server extension, or graph
DDL. Use [Continuous Graph](../continuous-graph/README.md) to maintain a bounded
result after commits.

## Run the sample

```powershell
docker compose -f eng/compose/postgres.yml up -d --wait postgres18
$env:BLUETUSK_CONNECTION_STRING = "Host=localhost;Port=5418;Username=postgres;Password=postgres;Database=bluetusk_tests;SSL Mode=Disable;Channel Binding=Disable"
dotnet run --project samples/BlueTusk.Samples.Graph
```

The sample creates two temporary tables and prints `Ada knows Grace`. For real
schemas, index edge source columns for outgoing traversal and edge destination
columns for incoming traversal. Index both for undirected traversal. For
composite endpoints, index all key columns together in endpoint order. Keep
selective predicates on the starting vertex and configure an EF command timeout
for broad queries.

## Select native SQL/PGQ

Use the same model declaration and select the backend in EF options:

```csharp
options.UseBlueTusk(connectionString,
    ef => ef.UseGraphCompatibility(BlueTuskPropertyGraphBackend.NativeSqlPgq));
```

Native mode requires negotiated server support and the native graph objects.
PostgreSQL 19 Beta 4 removed SQL/PGQ. The pinned Beta 3 fixture is retained
for native regression coverage. The default portable mode uses ordinary table
permissions and row-level security. [PostgreSQL Beta 4 announcement](https://www.postgresql.org/about/news/postgresql-19-beta-4-released-3386/)

See the [graph reference](reference.md) for typed mapping and migration guidance
and the [release tracks](../releases/release-tracks.md) for qualification status.
