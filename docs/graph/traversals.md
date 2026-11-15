# Run a bounded traversal

Use traversal methods to explore relationships from a known vertex. They
share the graph mapping configured by `HasPropertyGraph`.

## Choose the operation

| Method | Result |
| --- | --- |
| `NeighborsAsync` | An ordered page of authorized incident edges and neighboring vertices. Parallel edges remain separate. |
| `ReachableAsync` | Each authorized reachable vertex with its minimum hop count. |
| `ShortestPathAsync` | One minimum-hop path, with a deterministic edge-key tie break. |

Keys are ordered tuples containing one to eight integer, bigint, or UUID
values. Use the vertex element alias from the mapping and supply the entire
key tuple. Configure incoming, outgoing, or undirected traversal according to
the question you want to answer.

## Find upstream dependencies

```csharp
var graph = context.PropertyGraph("service_topology_graph");
var dependencies = await graph.ReachableAsync(
    new BlueTuskGraphIdentity("services", serviceId),
    new BlueTuskGraphTraversalOptions
    {
        Direction = BlueTuskGraphEdgeDirection.Incoming,
        MaximumDepth = 6,
        MaximumResults = 250,
    },
    cancellationToken);
```

Set depth, frontier, examined-edge, result, and engine-memory budgets to fit
your workload. An exhausted budget is an incomplete operation. Check `BudgetExhausted` and `BudgetReason` before interpreting an empty result. A completed shortest-path
operation can report that no path exists inside the searched depth.

## Set traversal limits

| Option | Default | Allowed range |
| --- | ---: | ---: |
| `MaximumDepth` | 8 | 0–64 |
| `MaximumFrontier` | 4,096 | 1–100,000 |
| `MaximumEdges` | 100,000 | 1–1,000,000 |
| `MaximumResults` | 1,000 | 1–10,000 |
| `MaximumBytes` | 8 MiB | 64 KiB–64 MiB |

Use `NextEdge` with the same anchor, direction, and options to read another
neighbor page. `EdgesExamined` and `PeakFrontier` help explain how much work
a search needed. Set a command timeout as a separate bound on database work.

## Choose an execution path

BlueTusk prefers the [server extension](engine.md) for these operations when
the extension and graph registration match. `UsedServerEngine` identifies the
execution path. Set `UseServerEngine = false` to request portable traversal.
The portable path uses batched reads in a repeatable-read transaction, or an
existing repeatable-read or serializable transaction.

Use `EdgeAliases` to restrict traversal to registered edge elements. Null
selects all edge elements and an empty list selects none. Server-side edge
filters require extension 1.2. Database errors and cancellation propagate.
A budget-exhausted result includes its reason and contains no partial search.

Use the [SQL operations](engine.md#additional-sql-operations) for weighted
paths, what-if comparisons, and searches starting from multiple anchors.

Use [Continuous Graph](../continuous-graph/README.md) when the same traversal
should be rerun after committed changes. Give each registration a stable query
name, bounded output, verified dependency mappings, and a subscriber-authorized
context factory.
