# Published projections in Live

`BlueTusk.Projections.Live` serves durable joined read models through the existing `BlueTusk.Live`
session, replay, resume-token and transport APIs. It introduces no CDC client or network transport.
Streams remains the source of committed table changes; the projection definition writes source
mirrors, dependencies, exact aggregate deltas and read models alongside its checkpoint.

## Atomic invalidation and authorized reads

The projection schema is version 5. `InitializeAsync` transactionally upgrades versions 1 through 4 under
the existing deployment advisory lock. The published `heads` row carries a monotonic publication
revision. Successful application to the active version increments that revision in the same
transaction as the documents, aggregates and checkpoint. Promotion increments it atomically with
the published version pointer. Rolled-back handlers, duplicate source deliveries and unpublished
snapshot/rebuild writes cannot advance it.

`ProjectionLiveInvalidationLog` coalesces this durable materialized commit marker into Live's cursor
contract. Its work is O(1) per refresh, without an accumulating invalidation log or a second WAL slot.
It deliberately invalidates all bounded subscriptions to one projection, across tenants; authoritative
reads always use the subscription's authenticated tenant and immutable security scope. This can
cause harmless cross-tenant reruns, but cannot disclose another tenant's rows. Publication revisions
are serialized on the per-projection heads row; latency/lock behavior under concurrent rebuilds needs
the production performance gates.

```csharp
var query = new ProjectionLiveQuery<OrderView>(
    projectionStore, "orders", authenticatedTenant,
    new LiveSecurityScope("tenant:" + authenticatedTenant, "policy:v1"),
    "orders-window", "application-database", "orders-window-v1",
    AppJson.Default.OrderView,
    new ProjectionLiveQueryOptions { MaximumDocuments = 100, MaximumPayloadBytes = 1_048_576 });
var metadata = (JsonTypeInfo<LiveResultEvent<ProjectionLiveRow<OrderView>, string>>)
    AppJson.Default.GetTypeInfo(typeof(LiveResultEvent<ProjectionLiveRow<OrderView>, string>))!;
await using var live = new ProjectionLiveSubscription<OrderView>(query, durableLiveReplayStore, metadata);
await live.StartAsync(cancellationToken);
```

The application's JSON context must register both `OrderView` and
`LiveResultEvent<ProjectionLiveRow<OrderView>, string>`. The adapter always supplies this metadata.
The additive `LiveSharedSubscriptions.CreateWithJsonMetadata` factory preserves the existing Live
constructor. Native AOT subscriptions require the metadata factory; the legacy reflection serializer
remains available on JIT runtimes. Built-in Live scalar arguments also have native JSON metadata.

## Bounded pages and filters

`ReadActivePageAsync` returns documents in ordinal key order with explicit tenant, row and payload
byte bounds. SQL removes every over-budget payload from the returned wire data before deserialization.
A continuation binds the published version, publication revision and last key. If any projection update
or cutover commits between pages, continuing fails: restart the first page rather than mixing revisions.
This contract provides coherent individual pages and detects a changed page sequence; it does not hold
a long-lived database snapshot across requests.

`ProjectionLiveQueryOptions.AfterKey`/`ThroughKey` define a fixed key window. An optional server-owned
predicate filters only the bounded page, **not a global filtered top-N query**. A predicate/version change
must change `queryVersion`; include its bound parameter values in that version. The tenant is fixed by
the authenticated resolver and included in the plan fingerprint; arbitrary client-supplied tenants,
predicates, SQL and unregistered argument names are not accepted. Application authentication and
tenant authorization are still required before constructing the query.

`ReadActiveAggregateAsync` returns one tenant's published exact aggregate and its publication metadata
from one SQL statement. It is useful for dashboard totals alongside joined order documents.

## Cutover, reconnect and restart

`ProjectionLiveSubscription.RefreshAsync` detects a published-version change and persists an
authoritative `ResultReset` with `SchemaChanged` through Live's replay/fan-out boundary, including when
both versions yield an empty filtered result. It also checks the version observed by the authoritative
query to cover a cutover that races the preliminary publication read. Otherwise normal Live keyed
add/update/remove/reorder diffs apply.

Connect and signed-token reconnect first refresh committed revisions before opening the existing Live
replay/fan-out boundary. A revision committed during a query remains pending for the next refresh;
the host must keep refreshing, as the sample does. These subscriptions deliver authoritative materialized
state and can coalesce intermediate commits. Use Events when every business event must be processed.

A new Live server process reads the durable replay head once and appends a source-generated
authoritative `ServerRestart` reset at the next sequence. An ambiguous initial append retains the exact
proposal for retry. Clients replay old retained events and the reset in sequence, or receive the existing
explicit replay-expired/limit response. Signing keys and authorized subscription identities must remain
stable across restart.

For multiple server nodes, use `PostgreSqlProjectionLiveReplayStore.InitializeAsync` during deployment,
then `AcquireAsync` for the query session's exact authorized `Identity`. Acquisition returns one
`ProjectionLivePublisher`, or null while another unexpired owner exists. Pass that lease-bound publisher
as the replay store to `ProjectionLiveSubscription`. Publisher fencing/expiry and replay CAS are checked
inside the **same** database append transaction, and rechecked after insertion before commit. A preflight
check around a separate store append would not satisfy this contract. The adapter validates database
ownership before start/refresh/connect, renews at one third of the configured lease duration and closes
old local subscriber queues on fencing, including when there is no new projection revision.

The publisher lease uses database time and a permanent increasing token. Dispose releases only its own
token; a stale node cannot release the replacement. Every local shared subscription requires its own
publisher instance. Reconnect on the replacement uses retained replay and an authoritative next-sequence
server reset. The host must keep refreshing/validating even without clients, route a query identity to
its owner and retry another node after loss; the sample returns HTTP 503 on contention. This boundary
does not add a proxy, transport or CDC source. A disconnected client must reconnect with its last signed
token; an expired/limited replay remains explicit.

Owned replay admission defaults to 1,024 events, 1 MiB per event and 8 MiB per append/read payload batch,
with 4,096 events per read. Partial byte-bounded replay causes Live's explicit replay-limit response;
missing sequences fail closed. Global pruning removes at most 1,024 expired prefix events per transaction
and preserves the head/identity/fence tombstone forever, so sequence and ownership cannot reset after
retention. The schema is independently versioned as owned replay version 1. Legacy replay stores can
still be supplied for existing single-owner applications; they do not acquire these publisher guarantees.

The `BlueTusk.Projections.Live` meter records publisher acquired/contended attempts, fencing, committed
replay events, exact duplicate retry events and pruned prefix events. These omit tenant/query identity
labels and tolerate failing observer callbacks; committed replay measurements follow the owned commit.

The projection worker itself resumes the retained ordered Streams source from its colocated checkpoint;
target commit precedes feedback. Lost slots, source failover, publication changes and exhausted WAL
retention fail closed and require an explicit rebuild policy.

Separate-slot rebuilding requires the explicit core lineage policy: capture actual system/database,
timeline, publication OID/flags and table/column/type/key metadata, then register that evidence before
the first snapshot. Snapshot tables must match that persisted contract. Capture fresh cutover evidence
and its verified transactional source barrier; the candidate must consume WAL through it and the locked
active checkpoint before the pointer can switch. The sample creates a second version/slot alongside the
active one and exposes an explicit equivalent-lineage promotion request. Strict promotion remains the
default. Source/publication DDL must remain immutable throughout snapshot and retained WAL; raw row
CDC cannot certify historical DDL reversal. Filtered/partial-column publications and cross-timeline
failover histories are rejected. See [the core contract](README.md).

## Deployable example and evidence

See `samples/BlueTusk.Projections.Orders.Live/README.md`. The server builds a genuine two-table
customer/order join, maintains exact tenant order totals, drains customer dependency pages, bulk-writes
source mirrors and joined documents, and serves the existing SSE endpoint. It authenticates a configured
tenant using a provisioned sample API key; adapt the resolver to your application's identity provider.

Tests cover byte/count/key bounds, stale continuations, rollback/duplicate revision behavior, tenant and
security scope rejection, filters, updates/deletes, unpublished rebuild invisibility, version cutover with
empty-result reset, durable PostgreSQL replay and signed reconnect. A real exported snapshot/pgoutput
test drives the same adapter. `OrdersLiveSampleTests` launches the actual sample server, exercises real
WAL plus SSE, disconnect/reconnect, abrupt process restart/checkpoint recovery and a second server's
concurrent snapshot/slot rebuild plus Live cutover reset, and cleans only its generated resources.
The native smoke executes the joined snapshot, bulk mirrors, aggregate/checkpoint/publication,
source-generated owned durable Live replay and actual lineage/barrier capture against PostgreSQL.
Ownership tests use two independent stores, actual expiry/takeover, a database sleep inside an append,
retained exact retries, byte/count bounds and prefix pruning. The two-process sample rejects a concurrent
publisher, then proves signed reconnect and new real WAL updates on the replacement HTTP node.

Performance/endurance, large backlog/fan-out, cluster routing, failover/restore, schema compatibility,
and automated rebuild deployment/recovery remain release requirements. The sample and tests are concrete
preview evidence, not a massive-production readiness claim.
