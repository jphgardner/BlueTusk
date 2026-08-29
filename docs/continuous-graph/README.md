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

### Expanded graph patterns in 1.2

The typed builder supports the PostgreSQL 19 SQL/PGQ shapes needed by larger
relationship models:

```csharp
var related = context.PropertyGraph("payments", "risk")
    .Match(pattern => pattern
        .Vertex<Account>("source", account => account.Id == accountId)
        .LabelsAnyOf("account", "customer")
        .UndirectedPath<Transfer>("path", minimumHops: 1, maximumHops: 4)
        .LabelsAnyOf("sent", "received")
        .Vertex<Account>("target")
        .LabelsAnyOf("account", "customer"))
    .Select<RelatedAccount>(projection => projection
        .Property<Account, long>(
            "source", account => account.Id, result => result.SourceId)
        .Property<Account, long>(
            "target", account => account.Id, result => result.TargetId));
```

- `Undirected` matches either edge direction using PostgreSQL's native
  `-[...]-` syntax. `Outgoing` and `Incoming` remain directional.
- `LabelsAnyOf` emits the SQL/PGQ `label-a|label-b` OR expression. Every selected
  label must exist on the resolved element table, and projected properties must
  be identical across those labels.
- `OutgoingPath`, `IncomingPath`, and `UndirectedPath` accept a bounded range of
  one to eight hops. PostgreSQL 19 does not provide native variable-length path
  syntax, so BlueTusk compiles the range into fixed-hop `GRAPH_TABLE` branches
  joined with `UNION ALL`. At most 64 branches are allowed.

A multi-hop edge variable cannot be projected because one match can contain
several edges. Project the start and end vertices instead. Repeating a path is
allowed only when the edge connects the same vertex element table to itself.
These rules are checked during translation. The generated SQL has also been
materialised against the digest-pinned PostgreSQL 19 Beta 3 development image;
PostgreSQL 19 GA and its exact digest remain mandatory for stable publication.

Continuous Graph treats variable-length and undirected patterns as broad-impact
queries. They receive authoritative full repair rather than affected-key delta
maintenance because a changed edge can alter reachability for keys that are not
present in the changed row. Multi-label expressions retain scoped delta support
when the ordinary endpoint proof remains complete.

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

Cancellation flows through context creation and EF execution. Unbounded paths,
more than eight hops, more than 64 compiled branches, and dependency inference
from caller-authored raw SQL remain unsupported.

## Three-tier incremental maintenance in 1.1

The 1.1 compiler retains an immutable impact plan alongside the executable
query. The plan contains the resolved graph pattern, physical element tables,
vertex and edge keys, edge endpoints, projected columns, predicates, ordering,
result key, and bounded result limit. That metadata drives three maintenance
tiers; it is not inferred again from a replication message.

1. **Trusted CDC delta.** An explicitly registered projector can update affected
   rows directly from complete old/new tuples.
2. **Authoritative scoped delta.** The automatic default extracts affected
   element keys and runs the compiler-generated, prepared key-scoped
   `GRAPH_TABLE` query.
3. **Authoritative repair.** The original complete query is rerun whenever
   correctness of either delta path cannot be proven.

The simple automatic overload selects tiers two and three:

```csharp
var incremental = plan.CreateIncrementalSession(
    arguments,
    new LiveSecurityScope("tenant:acme:user:17", "fraud-policy-v4"),
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

The scoped query executes under the original `LiveSecurityScope`; PostgreSQL
permissions, RLS, tenant settings, and the registered query remain the
authority. The engine never constructs client-visible authoritative rows from
untrusted tuple bytes.

### Opting in to direct CDC projection

Tier one is intentionally explicit. Implement
`IContinuousGraphCdcProjector<TResult,TKey>` and provide a
`ContinuousGraphCdcTrustContract` whose impact-plan fingerprint matches the
compiled plan. All four trust facts must be true: complete required old/new
values, exact changed columns, sufficient replica identity, and enforcement of
the original security scope. A mismatch or incomplete fact bypasses the
projector and continues through the authoritative tiers.

```csharp
var incremental = plan.CreateAutomaticIncrementalSession(
    arguments,
    securityScope,
    options,
    resultLimit: 100,
    trustedProjector: projector);
```

Treat projector registration as privileged application code. Schema
fingerprints must be regenerated after graph or projection changes. Do not
claim the trusted tier merely because a publication happens to include the
columns in one observed transaction.

### Ordered mutation and fallbacks

The session indexes visible result keys and retains the ordered top-N. For a
small exact change it sorts only affected candidates, merges them with the
already ordered unaffected sequence, and uses Live's affected-key mutation path
when membership and order are unchanged. The latter clones only the row array
and shares the immutable key/index structure.

The session forces authoritative repair for incomplete or undecodable tuples,
unknown schemas, truncation, commit-prepared, unsafe deletes, affected-key
overflow, rank-boundary uncertainty, unsupported query shapes, projector
uncertainty, visible rows leaving the predicate, rank worsening, and periodic
drift detection. Repairs recover candidates outside the visible top-N.
Malformed projector or scoped-query output fails closed.

The custom `IContinuousGraphIncrementalEvaluator<TResult,TKey>` overload remains
available and source/binary compatible for applications with an existing
authoritative affected-key implementation.

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

`ContinuousGraphIncrementalSession.Status` exposes counts for trusted CDC,
authoritative delta, and authoritative repair evaluations, plus affected-key
and query totals and the last fallback reason. OpenTelemetry instruments add
maintenance tier, affected-key count, query count, evaluation latency,
repair/fallback counters, and a redacted detail code. Alert on sustained repair
rate, affected-key-limit fallbacks, schema mismatch, or drift-repair differences
rather than attempting to suppress repair.

## Release state

The two 1.0.0 packages remain immutable. `BlueTusk.ContinuousGraph` contains
the runtime; `BlueTusk.ContinuousGraph.ControlPlane` contains the optional
operations adapter. The coordinated `1.1.0-rc.1` packages are public for
production-like evaluation. Stable `1.1.0` remains blocked until PostgreSQL 19
GA is digest-pinned, every lower product family has passed its exact stable-
candidate gates, the 24-hour Continuous Graph endurance run is archived, and
the performance-leadership evidence passes. The release script
machine-enforces dependency readiness and protected tag ordering. The 1.0
public surface remains a compatible subset of the hash-locked 1.1 candidate
surface; see the
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
