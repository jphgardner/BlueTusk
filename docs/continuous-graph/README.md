# Keep graph query results current

Continuous Graph runs a bounded typed graph query and maintains the result for
Live clients after relevant committed changes. The default BlueTusk Graphs
backend uses ordinary PostgreSQL joins on versions 15–18. Use it for fraud paths,
dependency maps, and network reachability.

Use BlueTusk 1.2 for these examples.

## How results are maintained

PostgreSQL is always authoritative. BlueTusk chooses the cheapest update path
whose correctness it can prove:

| Tier                       | What happens                                                                  | Use                                                  |
| -------------------------- | ----------------------------------------------------------------------------- | ---------------------------------------------------- |
| Trusted CDC delta          | Explicitly trusted application code updates known affected results in memory. | Fastest; opt in only with a complete trust contract. |
| Authoritative scoped query | BlueTusk reruns the typed graph query for affected keys.                    | Automatic incremental default.                       |
| Full authoritative repair  | BlueTusk reruns the complete registered query and diffs the result.           | Safety fallback and periodic drift repair.           |

Unknown schemas, incomplete old rows, truncation, two-phase commits, affected-key
overflow, unsafe deletes, uncertain top-N ranking, projector uncertainty, and
drift checks all force a full repair. Repair reads the complete authorized result and replaces the prior snapshot.

## Run a working example

The fraud and network samples create their schema, compile a query, execute the
initial result, and exercise updates:

```powershell
docker compose -f eng/compose/postgres.yml up -d --wait postgres18
$fixturePassword = docker compose -f eng/compose/postgres.yml exec -T postgres18 printenv POSTGRES_PASSWORD
$env:BLUETUSK_CONNECTION_STRING = "Host=localhost;Port=5418;Username=postgres;Password=$fixturePassword;Database=bluetusk_tests;SSL Mode=Disable;Channel Binding=Disable"

dotnet run --project samples/BlueTusk.Samples.ContinuousGraph.Fraud
dotnet run --project samples/BlueTusk.Samples.ContinuousGraph.Network
```

The connection disables TLS only for the isolated repository container.

## 1. Define a bounded query

```csharp
var definition = new ContinuousGraphQueryDefinition<RiskContext, FraudPath, long>(
    name: "suspicious-transfers",
    databaseIdentity: "risk-primary",
    version: "v1",
    graphName: "payments",
    graphSchema: "risk",
    elementTableAliases: ["accounts", "transfers"],
    parameters: [new LiveQueryParameter("accountId", typeof(long))],
    validationArguments: new Dictionary<string, object?> { ["accountId"] = 42L },
    maximumResultCount: 100,
    queryFactory: (db, arguments) =>
    {
        var accountId = arguments.Get<long>("accountId");
        return db.PropertyGraph("payments", "risk")
            .Match(pattern => pattern
                .Vertex<Account>("source", account => account.Id == accountId)
                .Outgoing<Transfer>("transfer")
                .Vertex<Account>("target"))
            .Select<FraudPath>(projection => projection
                .Property<Account, long>("source", x => x.Id, x => x.SourceId)
                .Property<Transfer, long>("transfer", x => x.Id, x => x.TransferId)
                .Property<Transfer, decimal>("transfer", x => x.Amount, x => x.Amount)
                .Property<Account, long>("target", x => x.Id, x => x.TargetId))
            .OrderByDescending(x => x.Amount)
            .ThenBy(x => x.TransferId)
            .Take(100);
    },
    keySelector: row => row.TransferId,
    rowComparer: FraudPathComparer.Instance);

var plan = await ContinuousGraphQueryCompiler.CompileAsync(
    contextFactory,
    definition,
    cancellationToken: cancellationToken);
```

The compiler verifies graph aliases, bounded output, stable ordering, direct
result keys, dependencies, and EF translation before a client subscribes. For this one-hop watch, use the transfer ID as the unique result key so parallel transfers remain separate. Order rows by amount descending and transfer ID ascending in both the database query and the result comparer.

## 2. Start with authoritative maintenance

Bind only declared parameters and preserve the caller's security scope:

```csharp
var arguments = plan.Bind(new Dictionary<string, object?>
{
    ["accountId"] = 42L,
});

await using var session = plan.CreateIncrementalSession(
    arguments,
    new LiveSecurityScope("tenant:acme:user:17", "fraud-policy-v4"),
    new ContinuousGraphIncrementalOptions<FraudPath, long>
    {
        ResultOrdering = FraudPathOrdering.Instance,
        KeyOrdering = Comparer<long>.Default,
        MaximumAffectedKeys = 512,
        RepairAfterTransactions = 1_000,
    });
```

Use the automatic authoritative path first. Add an
`IContinuousGraphCdcProjector<TResult,TKey>` only after the required old/new
columns, replica identity, changed-column knowledge, schema fingerprint, and
security contract are proven and tested.

## 3. Make it observable

Register the compiled plan with `ContinuousGraphQueryRegistry`. Track the
selected tier, fallback reason, affected keys, scoped/full query count,
latency, allocation, repair count, and checkpoint lag. A rising repair rate is
a capacity or trust-contract signal, not a reason to disable repair.

For dashboard execution, register a separate bounded projector and require the
graph-execution authorization policy. Never expose arbitrary graph SQL from a
browser.

## Watch a bounded traversal

Use traversal registration for reachability, a first neighbor page, or a shortest path. This registration follows all tables in the
graph. A relevant committed change re-runs the traversal under the current
subscriber authorization, then Live diffs the keyed result.

```csharp
var impact = ContinuousGraphQueryCompiler.CompileReachability(
    modelContext,
    name: "service-impact",
    databaseIdentity: "topology-primary",
    version: "v1",
    graphName: "service_topology_graph",
    graphSchema: null,
    parameters: [new LiveQueryParameter("serviceId", typeof(long))],
    anchor: args => new BlueTuskGraphIdentity("services", args.Get<long>("serviceId")),
    authorizedContextFactory: CreateAuthorizedContextAsync,
    options: new BlueTuskGraphTraversalOptions
    {
        Direction = BlueTuskGraphEdgeDirection.Incoming,
        MaximumDepth = 6,
        MaximumResults = 250,
    });
```

Implement `CreateAuthorizedContextAsync` using the authenticated execution
scope and the same EF graph mapping, returning a newly authorized context.
Use an ordinary Live session or the registered incremental session with
result ordering by hop count and ordinal keys. A budget-exhausted
traversal fails the evaluation before replay append or acknowledgement.
These watches prefer the [server engine](../graph/engine.md) once its extension
and a matching graph are registered. Set `UseServerEngine = false` to use
portable traversal. A change to `EdgeAliases` changes the traversal identity.
The same traversal can change execution engines while retaining its replay
identity.

## Production checklist

- Install and register the BlueTusk extension where you need server-side traversal. Choose explicit SQL/PGQ mode only on a server with that grammar and graph support.
- Keep `LiveSecurityScope`, PostgreSQL permissions, and RLS active on every query.
- Bound result rows, affected keys, hops, top-N, execution time, and concurrency.
- Persist replay append, session commit, and Streams acknowledgement in order.
- Differentially compare incremental results with a fresh authoritative query.
- Keep periodic full repair enabled.

The [full Continuous Graph reference](reference.md) documents expanded patterns,
the trust contract, mutation ordering, every fallback, dashboard projection,
dependencies, replay, and diagnostics.
