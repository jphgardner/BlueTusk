# BlueTusk Continuous Graph

`BlueTusk.ContinuousGraph` provides the maintained graph runtime. Trusted
server code registers bounded typed graph queries; the compiler validates the
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

The query envelope permits outer `Where`, `Select`, deterministic
`OrderBy`/`ThenBy`, one bounded `Take`, and `AsNoTracking`. Ordering must include
the direct result key. The ordinary typed graph builder remains responsible for
safe graph-pattern and projection translation, and EF must produce
the resolved backend SQL at registration. Unsupported query operators, translation
failures, unbounded results, and unstable ordering fail before clients can
subscribe.

### Expanded graph patterns in 1.2

Both backends support the typed patterns needed by larger
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

- `Undirected` matches either edge direction. `Outgoing` and `Incoming` remain directional.
- `LabelsAnyOf` matches any of the named labels. Every selected
  label must exist on the resolved element table, and projected properties must
  be identical across those labels.
- `OutgoingPath`, `IncomingPath`, and `UndirectedPath` accept a bounded range of
  one to eight hops. BlueTusk compiles the range into fixed-hop query branches
  joined with `UNION ALL`. At most 64 branches are allowed.

A multi-hop edge variable cannot be projected because one match can contain
several edges. Project the start and end vertices instead. Repeating a path is
allowed only when the edge connects the same vertex element table to itself.
BlueTusk checks these rules during translation. For native SQL/PGQ, verify the server capabilities before registration.

Continuous Graph treats variable-length and undirected patterns as broad-impact
queries. They receive authoritative full repair rather than affected-key delta
maintenance because a changed edge can alter reachability for keys that are not
present in the changed row. Multi-label expressions retain scoped delta support
when the ordinary endpoint proof remains complete.

## Capability and correctness

The compiler verifies that the query originates from the typed graph builder and
that its graph and table dependencies match the registration. Portable
registration requires no SQL/PGQ capability. In explicit native mode the
capability probe checks the connected PostgreSQL server. The backend and resolved
mappings are part of replay identity.

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
affected transactions are coalesced, the authorised graph query is run
again, and keyed additions, updates, removals, reorderings, or a bounded reset
are emitted. PostgreSQL/EF query execution—not CDC row images—is always the
source of client-visible data. The context factory and registered query remain
responsible for applying RLS, tenant settings, and application authorisation.
Security scopes participate in Live subscription identity and are never shared.

Cancellation flows through context creation and EF execution. Unbounded paths,
more than eight hops, more than 64 compiled branches, and dependency inference
from caller-authored raw SQL remain unsupported.

## Incremental maintenance

The compiler retains an immutable impact plan alongside the executable
query. The plan contains the resolved graph pattern, physical element tables,
vertex and edge keys, edge endpoints, projected columns, predicates, ordering,
result key, and bounded result limit. That metadata drives three maintenance
tiers; it is not inferred again from a replication message.

1. **Trusted CDC delta.** An explicitly registered projector can update affected
   rows directly from complete old/new tuples.
2. **Authoritative scoped delta.** The automatic default extracts affected
   element keys and runs the compiler-generated, prepared key-scoped
   graph query.
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

### Opting in to connection-scoped reads

Set `ContinuousGraphIncrementalOptions.ScopedConnection` when the registered
query shape and its bound filter values stay fixed for the subscription.
BlueTusk binds the typed query for that subscription and reuses it for eligible
single-key updates. Each update resolves the subscriber's authorized
`BlueTuskDataSource` and takes a fresh connection lease. The resolver must
choose the same database and enforce the same subscriber scope as the EF
registration. Check current access in the resolver or the optional
`AuthorizeAsync` callback, including revocation and policy version changes.

Use the callback to apply any required PostgreSQL role or tenant settings on
the supplied connection before the read. BlueTusk owns the connection and
returns it to the provider pool after the query. PostgreSQL evaluates its
permissions and RLS on that connection. If the application's query shape or
bound filters change with policy, start a new subscription for that scope.

For applications that already manage authorized connections, pass an
`IContinuousGraphScopedConnectionLease` factory to
`ContinuousGraphScopedConnection`. BlueTusk calls the factory for every
eligible read, including when PostgreSQL has prepared the statement. Enforce
current subscriber access and return an open connection to the registered
database with the subscriber's role and tenant settings applied. The
connection must be exclusive for the lease and outside any active transaction.
BlueTusk disposes the lease after the read, and the application is responsible
for returning or closing the connection. The two-row probe can reuse the
provider's bounded statement cache when the leased connection has
`Max Auto Prepare` enabled.

The optimization applies to simple ordered portable graph projections with
supported scalar keys and materialization. Composed queries, custom EF
interceptors, transactions, and unsupported mappings use the scoped EF path.
Initial snapshots and authoritative repairs use the registered EF query.
Eligible ordered queries reuse a compiled EF read while the model, options,
ordering, and limit match registration. Every full read invokes the registered
context and query factories and binds the current values. A changed ordering
or limit uses the current query through the ordinary EF pipeline.

Use a restricted PostgreSQL identity and verify RLS on every mapped table. An authorized scoped lease must be exclusive, already open, and scoped to the subscriber. Check authorization revocation and lease replacement, and compare results with a fresh query under the same identity.

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

The session uses Live's existing immutable key index to locate affected visible
rows. It checks their proposed values against adjacent ranks, including the
proposed values of affected neighbours. When membership and order remain
unchanged, it uses Live's persistent snapshot chunks without scanning or
copying the complete result. A still-hidden candidate behind a full top-N
boundary needs no result mutation.

If a candidate enters the window or a row improves across a neighbour, the
session sorts the affected candidates, merges them with the ordered unaffected
sequence, and applies the authoritative membership/order diff. This path still
visits the complete visible result. Rank worsening and visible removals retain
authoritative repair to recover hidden candidates. Prepared proposals share
immutable history and change committed state only after replay persistence.

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
keyed Live graph event. Both use temporary tables on the default portable backend. An in-memory
invalidation log keeps each sample self-contained:

```powershell
$fixturePassword = docker compose -f eng/compose/postgres.yml exec -T postgres18 printenv POSTGRES_PASSWORD
$env:BLUETUSK_CONNECTION_STRING = "Host=localhost;Port=5418;Username=postgres;Password=$fixturePassword;Database=bluetusk_tests;SSL Mode=Disable;Channel Binding=Disable"
dotnet run --project samples/BlueTusk.Samples.ContinuousGraph.Fraud
dotnet run --project samples/BlueTusk.Samples.ContinuousGraph.Network
```

## Operations

`ContinuousGraphQueryRegistry` stores non-generic registration descriptors
without retaining bound parameter values or result rows. Register each compiled
plan with the application registry. Install the optional
`BlueTusk.ContinuousGraph.ControlPlane` package to add the metadata-only
`HostedContinuousGraphControlPlaneQueryService` or the executable explorer.

The explorer uses a separate `ContinuousGraphControlPlaneExecutionRegistry`.
Registration is deliberately explicit: provide the compiled plan, all bound
arguments, the small allowlist of parameters an operator may edit, a server-side
security-scope resolver, and a result projector. Never mark a tenant, role,
permission, or RLS binding editable.

```csharp
var executions = new ContinuousGraphControlPlaneExecutionRegistry();
executions.Register(
    compiledPlan,
    new Dictionary<string, object?>
    {
        ["minimumRisk"] = 0.75m,
        ["tenantId"] = tenantId,
    },
    editableParameters: ["minimumRisk"],
    securityScopeFactory: actor => scopes.Resolve(actor.ActorId),
    projector: row => new ControlPlaneContinuousGraphFragment(
        Nodes: ProjectNodes(row),
        Edges: ProjectEdges(row)));

builder.Services.AddSingleton(executions);
builder.Services.AddSingleton<IControlPlaneContinuousGraphQueryService>(
    new ExecutableContinuousGraphControlPlaneQueryService(metadata, executions));
builder.Services.AddSingleton<IControlPlaneContinuousGraphExecutionService>(
    new HostedContinuousGraphControlPlaneExecutionService(executions));
```

The authorised detail page can then execute only the registered fingerprint and
show every projected node, edge, category, label, endpoint, and property in the
bounded result. The canvas uses the graph's directed relationships to produce a
deterministic left-to-right topology rather than arranging decorative category
bubbles. Known Kubernetes elements use Cluster, Namespace, Workload, Controller,
Runtime, Networking, Exposure, and Image stages. Other graph shapes derive
bounded stages from their directed edges, including a deterministic cycle
fallback. Category-coloured arrows remain visible in the complete fit view;
selecting a node highlights its immediate neighbours and relationships, while
selecting an edge exposes its endpoints, direction, type, and projected
properties.

Pan, zoom, search, fit, node selection, and edge selection are additive to the
complete node and edge tables, which remain the accessible representation and
do not depend on canvas hit testing. When projected rows contain `observedAt`,
`provenance`, and `storage` properties, the explorer displays the source,
snapshot age, local time-zone-qualified observation time, and authoritative
store above the result. A snapshot older than two minutes is visibly marked
stale. This is presentation metadata only: it does not weaken the registered
query's security scope or turn browser input into SQL.

Limits, timeouts, conflicting element IDs, and dangling endpoints fail visibly;
BlueTusk does not return a partial graph and call it complete. The optional
adapter owns the graph dependency; the Control Plane core does not reference
ContinuousGraph.

`ContinuousGraphIncrementalSession.Status` exposes counts for trusted CDC,
authoritative delta, and authoritative repair evaluations, plus affected-key
and query totals and the last fallback reason. OpenTelemetry instruments add
maintenance tier, affected-key count, query count, evaluation latency,
repair/fallback counters, and a redacted detail code. Alert on sustained repair
rate, affected-key-limit fallbacks, schema mismatch, or drift-repair differences
rather than attempting to suppress repair.

## Diagnose maintenance

Check the recorded tier and fallback reason before tuning a registration.
Frequent full repairs can indicate incomplete old rows, broad dependencies,
or a top-N boundary that affected-key queries cannot prove. Review the
registered dependency mappings, the replica identity of tables used by CDC,
and the endpoint indexes. Compare the maintained rows with the same fresh
query using the subscriber’s database identity.
