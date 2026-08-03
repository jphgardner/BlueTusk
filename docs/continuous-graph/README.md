# BlueTusk Continuous Graph

`BlueTusk.ContinuousGraph` is the post-1.0-platform graph preview. Trusted
server code registers bounded typed SQL/PGQ queries; the compiler validates the
query against EF property-graph metadata and then delegates gap-free
invalidation, authoritative requery, and keyed result diffs to BlueTusk Live.
It never reads replication or pgoutput messages.

## Registration

Configure the property graph in the EF model first, then register the specific
element-table aliases used by the query:

```csharp
var definition =
    new ContinuousGraphQueryDefinition<RiskContext, FraudPath, long>(
        name: "suspicious-transfers",
        databaseIdentity: "risk-primary",
        version: "1",
        graphName: "payments",
        graphSchema: "risk",
        elementTableAliases: ["accounts", "transfers"],
        parameters: [new LiveQueryParameter("accountId", typeof(long))],
        validationArguments: new Dictionary<string, object?>
        {
            ["accountId"] = 42L,
        },
        maximumResultCount: 100,
        queryFactory: (context, arguments) =>
        {
            var accountId = arguments.Get<long>("accountId");
            return context.PropertyGraph("payments", "risk")
                .Match(pattern => pattern
                    .Vertex<Account>("source", account => account.Id == accountId)
                    .Outgoing<Transfer>("transfer")
                    .Vertex<Account>("target"))
                .Select<FraudPath>(projection => projection
                    .Property<Account, long>(
                        "source", account => account.Id, result => result.SourceId)
                    .Property<Transfer, decimal>(
                        "transfer", transfer => transfer.Amount, result => result.Amount)
                    .Property<Account, long>(
                        "target", account => account.Id, result => result.TargetId))
                .OrderByDescending(result => result.Amount)
                .ThenBy(result => result.TargetId)
                .Take(100);
        },
        keySelector: result => result.TargetId,
        rowComparer: FraudPathComparer.Instance);

var plan = await ContinuousGraphQueryCompiler.CompileAsync(
    contextFactory,
    definition,
    cancellationToken: cancellationToken);
```

The alias declaration is deliberately explicit. The compiler resolves each
alias through the configured `BlueTuskPropertyGraphDefinition` and records only
the corresponding relational tables as Live invalidation dependencies. A
change to an unrelated graph element therefore does not rerun this query.
Unknown and duplicate aliases fail during registration.

The preview query envelope permits outer `Where`, `Select`, deterministic
`OrderBy`/`ThenBy`, one bounded `Take`, and `AsNoTracking`. Ordering must include
the direct result key. The ordinary typed graph builder remains responsible for
safe graph-pattern and projection translation, and EF must produce
`GRAPH_TABLE` SQL at registration. Unsupported query operators, translation
failures, unbounded results, and unstable ordering fail before clients can
subscribe.

## Capability and correctness

The production capability probe opens the factory context and requires
`BlueTuskConnection.SupportsSqlPgq == true`. PostgreSQL 15–18 and PostgreSQL 19
servers without the negotiated SQL/PGQ capability are rejected. A custom
`IContinuousGraphCapabilityProbe` exists for deterministic tests; bypassing the
production probe in an application is not a compatibility promise.

The compiled plan owns an ordinary `LiveQueryPlan`. Bind only its declared
scalar arguments, create a session with the caller's complete
`LiveSecurityScope`, and use the normal Live invalidation log:

```csharp
var arguments = plan.Bind(new Dictionary<string, object?>
{
    ["accountId"] = 42L,
});

await using var session = plan.CreateSession(
    arguments,
    new LiveSecurityScope("tenant:acme:user:17", "fraud-policy-v4"),
    invalidationLog);

var initial = await session.StartAsync(cancellationToken);
var update = await session.RefreshToCurrentAsync(cancellationToken);
```

The initial result uses Live's cursor-reserve/query/replay boundary. Later
affected transactions are coalesced, the authorised `GRAPH_TABLE` query is run
again, and keyed additions, updates, removals, reorderings, or a bounded reset
are emitted. PostgreSQL/EF query execution—not CDC row images—is always the
source of client-visible data. The context factory and registered query remain
responsible for applying RLS, tenant settings, and application authorisation.
Security scopes participate in Live subscription identity and are never shared.

Cancellation flows through context creation and EF execution. Incremental graph
maintenance, arbitrary client SQL/LINQ, unbounded paths, and dependency
inference from caller-authored raw SQL are outside this preview.

## Samples

The executable [fraud sample](../../samples/BlueTusk.Samples.ContinuousGraph.Fraud)
registers a high-value transfer traversal and shows a newly suspicious transfer
entering the authorised result after an affected edge changes. The executable
[network sample](../../samples/BlueTusk.Samples.ContinuousGraph.Network)
registers gateway dependencies and shows a vertex health update becoming a
keyed Live graph event. Both use connection-scoped temporary property graphs,
require PostgreSQL 19, and use the deterministic in-memory invalidation log
only to keep the sample self-contained:

```powershell
$env:BLUETUSK_CONNECTION_STRING = "Host=localhost;Port=5419;Username=postgres;Password=postgres;Database=bluetusk_tests;SSL Mode=Disable;Channel Binding=Disable"
dotnet run --project samples/BlueTusk.Samples.ContinuousGraph.Fraud
dotnet run --project samples/BlueTusk.Samples.ContinuousGraph.Network
```

## Operations

`ContinuousGraphQueryRegistry` stores non-generic registration descriptors
without retaining bound parameter values or result rows. Register each compiled
plan with the application registry. `HostedContinuousGraphControlPlaneQueryService`
projects query fingerprints, graph names, databases, element aliases, exact
table dependencies, result limits, and capabilities. The authorised dashboard
exposes the same inventory at `/graphs` and `/api/graphs`; every application
value is HTML encoded.

## Release state

The package is independently versioned as `0.1.0-preview.1` and remains
non-publishable while the benchmark and final packaging gates are completed.
Offline compiler tests cover exact dependency extraction, stable fingerprints,
Live session handoff, unsupported-server rejection, and fail-closed query
shapes. The opt-in PostgreSQL 19 acceptance test creates a real property graph,
materialises the initial result, mutates an affected vertex, observes the
authoritative keyed update, and cancellation-aborts a graph query blocked on an
exclusive table lock.

Run that live gate against the repository's PostgreSQL 19 service:

```powershell
docker compose -f eng/compose/postgres.yml --profile preview up -d postgres19
$env:BLUETUSK_TEST_CONNECTION_STRING = "Host=localhost;Port=5419;Username=postgres;Password=postgres;Database=bluetusk_tests;SSL Mode=Disable;Channel Binding=Disable"
dotnet test tests/BlueTusk.ContinuousGraph.Tests
```
