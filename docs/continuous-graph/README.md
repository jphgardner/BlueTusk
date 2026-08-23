# BlueTusk Continuous Graph

`BlueTusk.ContinuousGraph` is the published stable graph family. Trusted
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

The V1 query envelope permits outer `Where`, `Select`, deterministic
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

Cancellation flows through context creation and EF execution. Unbounded paths
and dependency inference from caller-authored raw SQL remain unsupported.

## Incremental maintenance

For high-change workloads, a compiled plan can maintain its bounded result from
authorised affected-key queries and use a full `GRAPH_TABLE` query only when
correctness cannot be proved:

```csharp
var incremental = plan.CreateIncrementalSession(
    arguments,
    new LiveSecurityScope("tenant:acme:user:17", "fraud-policy-v4"),
    affectedKeyEvaluator,
    new ContinuousGraphIncrementalOptions<FraudPath, long>
    {
        ResultOrdering = FraudPathOrdering.Instance,
        KeyOrdering = Comparer<long>.Default,
        MaximumAffectedKeys = 512,
        RepairAfterTransactions = 1_000,
        MaximumRepairInterval = TimeSpan.FromMinutes(5),
    });

var consumer = new ContinuousGraphIncrementalConsumer<FraudPath, long>(
    incremental,
    replayStore);
```

`IContinuousGraphIncrementalEvaluator<TResult,TKey>` receives one immutable
Streams transaction and the bound Live security context. It must derive the
complete affected result-key set and run a registered, authorised key-scoped
database query. Return `Exact` only when that set is complete, `Unrelated` when
the transaction cannot affect the plan, or `RequiresRepair` when coverage is
uncertain. CDC tuple values are never suitable as the returned rows.

The engine safely applies new candidates, in-place changes, and rank
improvements. It performs an authoritative full query when a visible row is
removed, leaves the predicate, worsens in rank, exceeds the affected-key
budget, comes through commit-prepared, or reaches a transaction/time repair
interval. These fallbacks recover rows outside the currently visible top-N.
Malformed evaluator output fails closed.

Each evaluation is a prepared transition. Disposing it rolls back; committing
it advances the source position and event sequence. The supplied Streams
consumer persists Live replay first, commits the transition second, and
acknowledges the source delivery last. Byte-identical replay retry closes the
post-store/pre-ack crash window. A process restart emits a new authoritative
initial result at the next replay sequence before source consumption resumes.
See [ADR 0016](../architecture/decisions/0016-authoritative-incremental-graph-maintenance.md).

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
plan with the application registry. Install the optional
`BlueTusk.ContinuousGraph.ControlPlane` package to add
`HostedContinuousGraphControlPlaneQueryService`. It projects query
fingerprints, graph names, databases, element aliases, exact table
dependencies, result limits, and capabilities. The authorised dashboard
exposes the same inventory at `/graphs` and `/api/graphs`; every application
value is HTML encoded. The optional adapter owns the graph dependency; the
Control Plane core does not reference ContinuousGraph.

## Release state

The two packages are published at stable `1.0.0`.
`BlueTusk.ContinuousGraph` contains
the runtime; `BlueTusk.ContinuousGraph.ControlPlane` contains the optional
operations adapter. The family remains unpublished until PostgreSQL 19 GA,
Provider, Streams, Live, and Control Plane 1.0.0, the exact 24-hour endurance
report, and at least one independent ContinuousGraph pilot pass. The release
script machine-enforces dependency readiness and protected tag ordering. The
V1 public surface is hash-locked by the
[API compatibility contract](api-compatibility.md). See the
[1.0.0 release record](release-notes-1.0.0.md) for the exact support and
publication boundary.
Offline compiler tests cover exact dependency extraction, stable fingerprints,
Live session handoff, unsupported-server rejection, and fail-closed query
shapes. Incremental state-machine tests cover exact top-N updates, repair
fallbacks, bounded affected keys, two-phase lifecycle handling, rollback,
duplicate delivery, evaluator contract failures, and replay-before-ack
recovery. The opt-in PostgreSQL 19
acceptance test creates a real property graph,
materialises the initial result, mutates an affected vertex, observes the
authoritative keyed update, and cancellation-aborts a graph query blocked on an
exclusive table lock.

Run that live gate against the repository's PostgreSQL 19 service:

```powershell
docker compose -f eng/compose/postgres.yml --profile preview up -d postgres19
$env:BLUETUSK_TEST_CONNECTION_STRING = "Host=localhost;Port=5419;Username=postgres;Password=postgres;Database=bluetusk_tests;SSL Mode=Disable;Channel Binding=Disable"
dotnet test tests/BlueTusk.ContinuousGraph.Tests
```

Run only the deterministic incremental contract:

```powershell
dotnet test tests/BlueTusk.ContinuousGraph.Tests/BlueTusk.ContinuousGraph.Tests.csproj --filter FullyQualifiedName~ContinuousGraphIncrementalTests
```

The checked-in live workload measures capability-guarded registration,
authoritative materialisation of 999 graph paths, and an affected invalidation
through PostgreSQL requery plus keyed Live diff. On the repository's reference
machine the ShortRun recorded 988 µs/103,446 B, 2.827 ms/666,055 B, and
4.225 ms/888,159 B respectively. These three-iteration values are regression
budgets, not service-level objectives; see the
[benchmark guide](../../benchmarks/README.md).

Reproduce the gated NuGet candidate without opening the publication gate:

```powershell
./eng/pack-product-family.ps1 -Family ContinuousGraph -Candidate -NoRestore
```
