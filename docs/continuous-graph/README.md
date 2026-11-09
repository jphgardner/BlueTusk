# Keep graph query results current

Continuous Graph runs a bounded typed graph query and maintains the result for
Live clients after relevant committed changes. The default BlueTusk Graphs
backend uses ordinary PostgreSQL joins on versions 15–18. Use it for fraud paths,
dependency maps, and network reachability.

**Availability.** The portable implementation passed local compatibility tests
on PostgreSQL 15–18. Use a checkout or local candidate containing the change.
The public `1.1.0-rc.1` packages predate it. The native SQL/PGQ backend is an
explicit opt-in and its historical Beta 3 coverage is separate.
[Stable qualification](../releases/release-tracks.md#portable-graph-status)
is still open.

## The safe mental model

PostgreSQL is always authoritative. BlueTusk chooses the cheapest update path
whose correctness it can prove:

| Tier                       | What happens                                                                  | Use                                                  |
| -------------------------- | ----------------------------------------------------------------------------- | ---------------------------------------------------- |
| Trusted CDC delta          | Explicitly trusted application code updates known affected results in memory. | Fastest; opt in only with a complete trust contract. |
| Authoritative scoped query | BlueTusk reruns the typed graph query for affected keys.                    | Automatic incremental default.                       |
| Full authoritative repair  | BlueTusk reruns the complete registered query and diffs the result.           | Safety fallback and periodic drift repair.           |

Unknown schemas, incomplete old rows, truncation, two-phase commits, affected-key
overflow, unsafe deletes, uncertain top-N ranking, projector uncertainty, and
drift checks all force a full repair. That fallback is expected behavior, not
silent data loss.

## Run a working example

The fraud and network samples create their schema, compile a query, execute the
initial result, and exercise updates:

```powershell
docker compose -f eng/compose/postgres.yml up -d --wait postgres18
$env:BLUETUSK_CONNECTION_STRING = "Host=localhost;Port=5418;Username=postgres;Password=postgres;Database=bluetusk_tests;SSL Mode=Disable;Channel Binding=Disable"

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
                .Property<Transfer, decimal>("transfer", x => x.Amount, x => x.Amount)
                .Property<Account, long>("target", x => x.Id, x => x.TargetId))
            .OrderByDescending(x => x.Amount)
            .ThenBy(x => x.TargetId)
            .Take(100);
    },
    keySelector: row => row.TargetId,
    rowComparer: FraudPathComparer.Instance);

var plan = await ContinuousGraphQueryCompiler.CompileAsync(
    contextFactory,
    definition,
    cancellationToken: cancellationToken);
```

The compiler verifies graph aliases, bounded output, stable ordering, direct
result keys, dependencies, and EF translation before a client subscribes.

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

## Production checklist

- Use BlueTusk Graphs by default. For native mode, verify SQL/PGQ capability on the target server.
- Keep `LiveSecurityScope`, PostgreSQL permissions, and RLS active on every query.
- Bound result rows, affected keys, hops, top-N, execution time, and concurrency.
- Persist replay append, session commit, and Streams acknowledgement in order.
- Differentially compare incremental results with a fresh authoritative query.
- Keep periodic full repair enabled.

The [full Continuous Graph reference](reference.md) documents expanded patterns,
the trust contract, mutation ordering, every fallback, dashboard projection,
metrics, tests, and release evidence.
