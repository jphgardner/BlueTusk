# Query a property graph

Map existing PostgreSQL tables as vertices and edges. With the default
BlueTusk Graphs compatibility mode, EF Core translates a typed graph pattern
into parameterized joins over those tables. Use PostgreSQL 15–18 and a
BlueTusk 1.2.

Bounded neighbor, reachability, and shortest-path operations prefer the
[BlueTusk server engine](engine.md) when a compatible extension and a matching
graph registration are available. They use portable traversal when those
prerequisites are unavailable. `Match(...).Select(...)` composes over
relational joins independently of the traversal engine.

## 1. Map existing tables

Configure normal EF table mappings first. This example uses `people` as
vertices and `friendships` as directed edges, with a foreign key at each
endpoint.

```csharp
modelBuilder.HasPropertyGraph("social", graph =>
{
    graph.Vertex<Person>("people", vertex => vertex
        .HasLabel("person")
        .HasKey(x => x.Id)
        .Properties(x => new { x.Id, x.Name }));

    graph.Edge<Friendship>("friendships", edge => edge
        .HasLabel("follows")
        .HasKey(x => x.Id)
        .Properties(x => new { x.Id })
        .HasSource<Person>(x => x.FromPersonId, x => x.Id)
        .HasDestination<Person>(x => x.ToPersonId, x => x.Id));
});
```

Migrate the underlying tables and endpoint indexes as usual. Portable graph
mappings are stored in EF metadata, and their migrations create only
relational database objects.

## 2. Query a directed hop

```csharp
var matches = await db.PropertyGraph("social")
    .Match(pattern => pattern
        .Vertex<Person>("source", person => person.Id == personId)
        .Outgoing<Friendship>("follow")
        .Vertex<Person>("target"))
    .Select<FollowMatch>(projection => projection
        .Property<Friendship, long>("follow", edge => edge.Id, row => row.EdgeId)
        .Property<Person, string>("target", person => person.Name, row => row.Name))
    .OrderBy(row => row.EdgeId)
    .Take(50)
    .ToListAsync(cancellationToken);
```

Define `FollowMatch` as a class with writable `long EdgeId` and `string Name`
properties. You can compose ordinary EF filtering, ordering, and limits on
the projected rows. Each matching walk produces a row. Parallel edges can
therefore return the same target more than once.

## 3. Index the direction you traverse

Index the edge’s source columns for outgoing queries and destination columns
for incoming queries. Keep both indexes for undirected traversal. Index
composite endpoint keys together in the declared key order. Use an anchor
predicate, a bounded path, and a timeout for high-degree vertices. Check
`EXPLAIN (ANALYZE, BUFFERS)` against representative data before relying on a
particular plan.

The database role needs access to the mapped vertex and edge tables. Apply
tenant restrictions through PostgreSQL RLS or explicit predicates in the graph
query. EF filters on a source entity do not automatically constrain the
vertices and edges inside the generated SQL.

## Choose the next operation

- Use the [Graph reference](reference.md) for bounded patterns, composite keys, native SQL/PGQ, and migration compatibility.
- Use [bounded traversals](traversals.md) for neighbors, reachability, and shortest paths.
- Use [Continuous Graph](../continuous-graph/README.md) for authorized results that refresh after relevant commits.
- Run the [Graph sample](../../samples/BlueTusk.Samples.Graph/) from a checkout to see a complete model and query.
