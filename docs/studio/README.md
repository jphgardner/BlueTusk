# BlueTusk Studio

New preview ASP.NET Core developer workspace: an embedded SQL editor, schema
browser, query-plan viewer, authorized event traces, Live inspection and audited
quarantine replay. SQL/schema, Events and Control Plane integrations are separate
packages so the core workspace does not require either operational runtime.

```csharp
builder.Services.AddBlueTuskStudio<MyDatabaseScopeResolver, MyDurableAuditSink>(
    new StudioOptions
    {
        ReadPolicy = "database-developer",
        QueryPolicy = "database-query-operator",
        MaximumConcurrentQueries = 8,
        MaximumRows = 500,
        MaximumReplyBytes = 4 * 1024 * 1024,
        QueryTimeoutSeconds = 10,
    });
// Configure authentication and both policies in the host.
app.UseAuthentication();
app.UseAuthorization();
app.MapBlueTuskStudio();
```

The scope resolver is required. It chooses the authenticated principal's
least-privilege PostgreSQL data source/role, a stable non-sensitive
`StudioDatabaseScope.AuditScopeId` identifying the selected database/tenant
scope, and the schemas exposed in the
browser. Schema visibility is not a SQL authorization boundary: PostgreSQL
grants, RLS and function privileges must enforce that principal's actual query
access. Studio does not borrow an unrestricted operator data source implicitly.
The caller owns all resolved data sources. Durable audit is also required.
This preview upgrade requires existing host resolvers to set `AuditScopeId` to
an opaque value that is stable across replicas and restarts and distinct for
different database/tenant scopes. Leaving it empty fails the query audit closed.

`PostgreSqlStudioAuditSink` supplies a durable borrowed-data-source implementation.
Provision its schema during deployment with `InitializeAsync`, using a deployment
role, then register the configured sink instance before `AddBlueTuskStudio`:

```csharp
var audit = new PostgreSqlStudioAuditSink(auditDataSource, "developer_audit");
await audit.InitializeAsync();
builder.Services.AddSingleton(audit);
builder.Services.AddBlueTuskStudio<MyDatabaseScopeResolver, PostgreSqlStudioAuditSink>(options);
```

The runtime audit role needs SELECT/INSERT only on that dedicated audit
repository; the database scope resolver must use a separate least-privilege role
for user queries. Retries of an operation/outcome identity require identical
actor, scope, fingerprint and row count; exact duplicates leave the stored tuple
unchanged. Unknown durable versions reject initialization
and append. Initialization upgrades known v1 audit storage to v2 in one
deployment transaction, marking older rows `legacy-unknown` because their
database/tenant scope cannot be recovered. Run this migration in a maintenance
window: the added column requires a table lock, and old runtimes stop appending
after the version changes. The v1 upgrade adds its length constraint as
`NOT VALID` to avoid scanning the audit table under that lock; separately run
`ALTER TABLE developer_audit.studio_audit VALIDATE CONSTRAINT studio_audit_scope_length`
after cutover. New writes obey the constraint before validation. Completion
audit failure leaves an uncertain response outcome: inspect
the operation's audit rather than assuming the handler did not run. Retention,
archive/restore, access controls on actor identity and storage alerting are host
operations; the initial sink does not silently delete audits. Do not prune the
table solely by `occurred_at`: operation UUIDs are currently random and have no
trusted creation time, so a retry after deletion could recreate an older
operation/outcome as a fresh row with different fields. Bounded retention needs
an explicit durable retry fence, an archive/reconciliation policy, and a
versioned migration before deletion can preserve immutable audit semantics.

For multi-replica SQL and schema admission, register an optional shared gate
before `AddBlueTuskStudio`:

```csharp
builder.Services.AddSingleton<IStudioDistributedAdmission>(
    new PostgreSqlStudioAdmission(admissionDataSource, "developer_studio",
        maximumConcurrentOperations: 16, maximumConcurrentPerScope: 4));
```

Every replica must point to the same PostgreSQL database and use the same
namespace and limits. The gate uses a dedicated borrowed data source and holds
one transaction-level advisory lock for the stable audit scope and one for the
global pool until the operation finishes. It probes slots without waiting;
exhaustion returns 429 before an audit attempt or query. A storage error fails
closed with 503. The admission role needs only database access and permission
to call PostgreSQL advisory-lock functions; it does not need access to user
query data or the audit table. The local gate still limits request parsing and
scope resolution before the shared gate is reached. Keep the admission data source's
pool at least as large as the intended global cap, monitor pool/connection
health, and size the global cap against the actual database resource budget.
The per-scope cap prevents one scope from occupying all global slots when other
scopes have work, but does not promise strict scheduling fairness. A lost
admission connection releases PostgreSQL locks even if the separate user-query
connection has not yet stopped; PostgreSQL role and pool resource limits remain
the hard backstop for that failure mode. The gate is opt-in and currently covers
core SQL and schema routes; Events and Control Plane adapters have independent
process-local gates.

Every route requires the read policy; execution additionally requires the query
policy and a session-bound antiforgery token, issued through `/session`. Query
audit records contain operation identity, actor, query fingerprint, outcome and
row count, never SQL or result values. The fingerprint binds SQL text, explain
mode and effective row limit, so reuse of an operation ID with different
execution parameters fails its immutable attempt audit. Clients must supply a nonempty
`OperationId` UUID with each `/query` request and retain it after an uncertain
response; the UI generates and displays one. The response also exposes
`X-BlueTusk-Studio-Operation-Id`. The host must provide a stable actor subject
claim, unique across identity providers. This is a preview HTTP contract change:
older callers that omit `OperationId` receive HTTP 400. Audit attempts persist
after database scope resolution and before SQL
execution; scope-resolution failures have no query-attempt audit. Operation IDs
provide correlation, not query idempotency: retrying one can execute SQL again,
and the caller should reconcile an uncertain result before another execution.
The query route takes a nonqueued process-local capacity slot before reading
its body, resolving its database scope or writing an audit. A capacity rejection
returns 429 without a durable attempt row; hosts should count these separately.

Read query admission understands quoted identifiers/strings, escaped strings,
dollar quoting and nested comments; it rejects multiple statements and malformed
tokens. PostgreSQL owns grammar and function behavior. Execution uses a read-only
transaction, an explicit standard-string mode, server statement deadline,
application cancellation and a database-side row bound. Explain never enables
ANALYZE. There are nonqueued concurrency, request-byte and reply-byte limits.
The SQL reader uses sequential access so an unparameterized result is not
buffered by the provider before Studio enforces its reply limit.
Schema capture shares that concurrency gate and deadline and has an exact
serialized reply limit. Results admit at most 256 columns. Encoded PostgreSQL
field sizes are checked before decoding values; final JSON expansion is checked
against the reply bound as well. These limits do not bound every transport/CLR
allocation of the underlying reader.
PostgreSQL read-only transactions do not sandbox external effects of functions;
the selected database role must have appropriate function privileges.
The row bound limits returned rows, not PostgreSQL's work to produce them:
sorts, aggregates and functions may use substantial CPU, memory, temporary
files or I/O before the deadline. The host must use resource-limited database
roles and an appropriately isolated query pool. Without the optional shared
gate, query admission is per host process, not a cross-replica or per-tenant
quota.

The UI uses external same-origin assets, CSP, no-store and nosniff headers. It
renders schema names and result values using `textContent`; exact bigint/decimal
values are strings to avoid browser numeric rounding. Database error messages
are not returned to the browser. Host logging must also preserve that boundary.

## Operational adapters

```csharp
builder.Services.AddBlueTuskStudioEvents<MyEventScopeResolver>(new()
    { MaximumEvents = 100, MaximumPayloadBytes = 8 * 1024 * 1024 });
builder.Services.AddBlueTuskStudioControlPlane<MyOperationsScopeResolver>(new()
    { ReplayPolicy = "quarantine-replay-operator", MaximumSubscriptions = 100 });
// After authentication and authorization middleware:
app.MapBlueTuskStudioEvents();
app.MapBlueTuskStudioControlPlane();
```

`IStudioEventScopeResolver` returns the borrowed event store and a principal's
explicit safe-alias to tenant/stream mapping. No raw tenant or stream key is
accepted from the browser. Event reads have row, payload-read, reply, concurrency
and deadline limits, and persist audit attempts/completions. Payload bytes are
never returned: traces include event UUID, type/version, occurrence, payload
length and decimal-string sequence. Continuations use an exclusive sequence;
an empty page marks the current end. Payload read budgets must match the Event
store's configured maximum individual/append bounds, so a permitted event cannot
permanently stall a page. The UI renders all metadata with `textContent`.

`IStudioControlPlaneScopeResolver` returns principal-selected Control Plane
services, actor roles, exact authorized subscription fingerprints and safe replay
aliases mapped to private Control Plane targets. Neither a supplied fingerprint
nor an authenticated session grants access to an unrelated tenant. Live replies
omit query parameter fingerprints, raw security scope, policy labels, diagnostics
and global registry totals. Long counters are decimal strings; ordered fingerprint
pages are observations of a changing registry, not a durable snapshot. Hosts must
bound the underlying Control Plane registry and honor cancellation: the adapter
rejects oversized provider results after the provider returns them.

Quarantine replay requires the additional replay policy, a session CSRF token,
an authorized alias, an operation UUID, a reason and exact `ReplayQuarantine:alias`
confirmation. The adapter translates that alias to the private target and delegates
to `ControlPlaneOperationExecutor`, which independently enforces roles and writes
durable requested/completion audits. Studio records separate fingerprint-only
attempt/completion audits. Every failed response omits exception bodies. Handler
idempotency remains the Control Plane host's responsibility; a UUID alone does
not make a replay handler idempotent. The UI retains a failed request's operation
UUID while its input remains unchanged and asks the operator to inspect the audit
before repeating an uncertain outcome. Process/browser restart requires external
audit reconciliation. No checkpoint rewind, consumer removal or slot deletion
route is exposed by this adapter.

The two adapters use independent bounded, nonqueued admission gates; exhaustion
returns 429. Unconfigured adapters have no operational routes. The workspace's
optional panels allow the host to enable either integration independently.

## Local verification

`bluetusk-studio` is the separate Studio .NET tool. It performs read-only
operational inspection using a host-issued bearer token from
`BLUETUSK_STUDIO_TOKEN`; credentials never appear in command arguments or errors.

```text
bluetusk-studio subscriptions --endpoint https://ops.example.com/bluetusk/studio/ --limit 100
bluetusk-studio streams --endpoint https://ops.example.com/bluetusk/studio/
bluetusk-studio events --endpoint https://ops.example.com/bluetusk/studio/ --stream orders --after 42 --limit 100
```

Endpoints require HTTPS except for loopback fixtures. Redirects are disabled,
cookies are not sent, streamed JSON is capped at 1 MiB with a 20-second deadline,
and unexpected top-level/row fields are rejected. Output uses JSON escaping and
preserves long counters as strings. Exit 2 means denied; exit 1 means invalid
arguments, cancellation or failure. Bearer validation/authorization is supplied
by the same Studio host; a cookie-only host cannot serve this CLI until it adds
its token policy. The tool deliberately has no command that executes replay.
Two actual Kestrel CLI tests pass, covering bearer headers, exact values,
escaping, denied requests, oversized responses, unknown payload fields and
redirect rejection.

Fourteen tests pass against an isolated PostgreSQL 18 fixture with zero skips.
Real Kestrel tests cover authentication, CSRF, scope aliases, metadata redaction,
pagination, exact Int64 counters, replay confirmation/role/audit rejection and
nonqueued capacity. Actual PostgreSQL tests cover bounded SQL/schema, durable
concurrent audit retries/reopen/future-version rejection and authorized domain
event pages. These tests do not constitute production workload qualification.
The least-privilege case provisions a separate temporary login role: PostgreSQL
RLS removes the other tenant's rows, an ungranted table stays inaccessible, and
server-file reads/role escalation are denied. Subsequent permitted reads still
succeed after the denied transactions. The fixture role/schema are removed.

Remaining product requirements include saved/versioned queries, interactive
database scope selection, full durable audit deployment/archive examples,
browser accessibility/visual tests,
load/fault campaigns, health/telemetry and production release qualification.
See the full [implementation programme](../ecosystem/implementation-programme.md).
