# BlueTusk PostgreSQL graph engine

The `bluetusk_graph` extension executes traversal in PostgreSQL
against the tables registered as vertices and edges. It uses the caller's
permissions and row-level security for every visible hop. The existing
portable EF graph pattern interface is available on ordinary PostgreSQL.

## Install and register

Build with PGXS and the headers for the target server. The Dockerfile uses an
Alpine-based PostgreSQL image for both build and runtime. Use the exact image
for the major you intend to run.

~~~sh
docker build -t bluetusk-graph:pg18 extensions/bluetusk_graph
~~~

Install the binary on every server that can run the database, including
failover targets. A database administrator installs the catalogue with
`CREATE EXTENSION bluetusk_graph`. Application roles need SELECT on the
mapped columns and the appropriate RLS policies. Grant graph registration or
catalogue write permissions only to a deployment role.

Register a graph over existing tables with named elements. In this example,
`topology.services(id)` and `topology.dependencies(id)` have UUID primary keys.

~~~sql
SELECT bluetusk_graph.register_graph('topology', 'dependencies', '{
  "vertices": {
    "service": {"schema":"topology", "table":"services", "key":["id"]}
  },
  "edges": {
    "depends_on": {
      "schema":"topology", "table":"dependencies", "key":["id"],
      "source":{"vertex":"service", "columns":["source_id"]},
      "destination":{"vertex":"service", "columns":["destination_id"]}
    }
  }
}');
~~~

A key is an ordered tuple of one to eight PostgreSQL integer, bigint, or UUID
values. Keys must be non-null and backed by an immediate unique index.
Endpoint columns must agree with their mapped vertex key types and order.
Create indexes on outgoing and incoming edge columns used in traversal.
Reregister a graph after updating a mapped key, endpoint, or exposed column.

## Query

~~~sql
SELECT bluetusk_graph.reachable(
    'topology', 'dependencies', 'service',
    '["ef879e5f-4689-4fcf-8621-6a10b0cd1e31"]',
    max_depth => 6, direction => 'incoming', max_results => 250
);
~~~

- `neighbors` returns an edge and vertex per row, with a continuation edge
  for the next page. Edges are ordered by canonical key encoding within
  each level. Apply the continuation to the same anchor and direction.
- `reachable` returns vertices with their minimum hop counts, excluding
  the anchor. `shortest_path` returns one minimum-hop path with a stable
  edge-key tie break. A zero-hop path contains only the anchor.
- `compare_reachability` evaluates baseline and hypothetical reachability
  in the same statement snapshot. Pass up to 128 edge identities as
  `[["edge_alias",[key_value]], ...]` to suppress in the scenario.
- `weighted_shortest_path` uses an exposed integer or bigint cost column
  on every edge element. It minimizes total cost, then hop count, then the
  edge-key sequence. A null or negative examined cost raises an error.
- Version 1.2 adds edge-element filters to `neighbors_on`, `shortest_path_on`, and
  `weighted_shortest_path_on`. Supply registered edge aliases. An empty array
  selects zero edge elements.
- `reachable_from` accepts up to 128 `[element, key tuple]` anchors and an
  optional edge filter. It uses one shared search and work budget. Each
  result gives the minimum hop count and the winning anchor. Equal distances
  use input anchor order. Duplicate, hidden, and missing anchors contribute
  no extra frontier entries.

Each result reports its status and authorized work counters. An exhausted
edge, frontier, result, or memory budget returns `budget_exhausted` with
no partial result. A completed bounded shortest-path search with no path
returns `path: null`. Engine memory covers retained traversal state. Set a
PostgreSQL statement timeout and appropriate executor memory and temporary
file limits for the calling role.

## EF Core and Continuous Graph

Use the graph already declared by `HasPropertyGraph`. Run
`RegisterEngineAsync` with the deployment role after applying relational
migrations. The deployment and application contexts should use the same
model mapping.

~~~csharp
var graph = context.PropertyGraph("service_topology_graph");
var result = await graph.ReachableAsync(
    new BlueTuskGraphIdentity("services", serviceId),
    new BlueTuskGraphTraversalOptions
    {
        Direction = BlueTuskGraphEdgeDirection.Incoming,
        MaximumDepth = 6,
        MaximumResults = 250,
    },
    cancellationToken);
~~~

`NeighborsAsync` and `ShortestPathAsync` use the same identity and options.
`UsedServerEngine` reports the selected execution path. EF uses the engine by default
for supported operations when the extension and registered definition match.
Set `EdgeAliases` to filter by registered edge element. Server-side filters
require extension 1.2. The portable
fallback runs batched reads inside one repeatable-read transaction, or uses
an existing repeatable-read or serializable transaction. Database errors
and cancellation propagate to the caller. Set `UseServerEngine` to false
to select the portable implementation explicitly.

`ContinuousGraphQueryCompiler` can register a bounded watch with
`CompileReachability`, `CompileNeighbors`, or `CompileShortestPath`. These
methods use the same default engine selection as the EF traversal methods
when options are omitted. Supply a factory that creates the caller-authorized
DbContext for each execution.
The plan tracks every vertex and edge table in the graph. For incremental
session options, order reachability rows by hop count and neighbors or
singleton paths by equal rank, with ordinal result keys as the tie-breaker.
Relevant changes request authoritative requery and Live diff. Budget failures
fail the evaluation before replay or acknowledgement. Edge aliases are part
of the watch's query identity. A change of execution engine preserves the
identity when the traversal definition is unchanged.

## Verification

`sql/traversal.sql` covers authorization, paging, budgets, shortest paths,
weighted paths, and scenario comparison. `sql/oracle.sql` compares depths
and directions on a composite-key graph against recursive SQL.
`sql/benchmark.sql` accepts `-v edge_count=100000` and compares the
frontier engine with recursive SQL and a selective indexed query. Add
`-v strategy_experiments=1` to measure the bidirectional path-bound prototype.
`sql/fraud-rank-refill.sql` and `sql/weak-components.sql` are workload
prototypes. Run each script on an empty disposable database with
`psql -X -v ON_ERROR_STOP=1`.

The engine CI builds a separate archive for each pinned PostgreSQL 15–18
Alpine image on Linux x86-64. Each archive includes its checksum, source
commit, and base image digest. Build from source with your server's
`pg_config` for other PostgreSQL distributions.

The workflow tests SQL and .NET 10 integration on every supported PostgreSQL
major and tests the EF and Continuous Graph client on each .NET runtime from
5 through 11 against PostgreSQL 18. The SQL suite installs version 1.0,
registers a graph, then verifies the 1.1 and 1.2 upgrades. For undefined-behavior instrumentation,
build with `--build-arg GRAPH_UBSAN=1` and start the container with
`-e LD_PRELOAD=/usr/local/lib/libubsan.so.1`.

For local EF/Live verification, set `BLUETUSK_GRAPH_ENGINE_TEST_CONNECTION_STRING`
to a disposable database on the extension image. Run the Continuous Graph test
project with `-p:BlueTuskGraphEngineTests=true` and filter for
`GraphEngineIntegrationTests`. The portable suite uses its own database
fixture.

Run `eng/run-native-graph-topology.ps1` to compare native traversal with
the migrated Service Topology application on a disposable, digest-pinned
PostgreSQL 18 image. The runner also exercises native Continuous Graph
replay with synthetic transaction delivery and writes each phase's TRX,
PostgreSQL logs, and provenance to a new directory under `artifacts`.
