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
least-privilege PostgreSQL data source/role and the schemas exposed in the
browser. Schema visibility is not a SQL authorization boundary: PostgreSQL
grants, RLS and function privileges must enforce that principal's actual query
access. Studio does not borrow an unrestricted operator data source implicitly.
The caller owns all resolved data sources. Durable audit is also required.

`PostgreSqlStudioAuditSink` supplies a durable borrowed-data-source implementation.
Provision its schema during deployment with `InitializeAsync`, using a deployment
role, then register the configured sink instance before `AddBlueTuskStudio`:

```csharp
var audit = new PostgreSqlStudioAuditSink(auditDataSource, "developer_audit");
await audit.InitializeAsync();
builder.Services.AddSingleton(audit);
builder.Services.AddBlueTuskStudio<MyDatabaseScopeResolver, PostgreSqlStudioAuditSink>(options);
```

The runtime audit role needs SELECT/INSERT/UPDATE only on that dedicated audit
repository; the database scope resolver must use a separate least-privilege role
for user queries. Retries of an operation/outcome identity require identical
actor, fingerprint and row count. Unknown durable versions reject initialization
and append. Completion audit failure leaves an uncertain response outcome: inspect
the operation's audit rather than assuming the handler did not run. Retention,
archive/restore, access controls on actor identity and storage alerting are host
operations; the initial sink does not silently delete audits.

Every route requires the read policy; execution additionally requires the query
policy and a session-bound antiforgery token, issued through `/session`. Query
audit records contain operation identity, actor, query fingerprint, outcome and
row count, never SQL or result values. Audit attempts persist before executing.

Read query admission understands quoted identifiers/strings, escaped strings,
dollar quoting and nested comments; it rejects multiple statements and malformed
tokens. PostgreSQL owns grammar and function behavior. Execution uses a read-only
transaction, an explicit standard-string mode, server statement deadline,
application cancellation and a database-side row bound. Explain never enables
ANALYZE. There are nonqueued concurrency, request-byte and reply-byte limits.
Schema capture shares that concurrency gate and deadline and has an exact
serialized reply limit. Results admit at most 256 columns. Encoded PostgreSQL
field sizes are checked before decoding values; final JSON expansion is checked
against the reply bound as well. These limits do not bound every transport/CLR
allocation of the underlying reader.
PostgreSQL read-only transactions do not sandbox external effects of functions;
the selected database role must have appropriate function privileges.

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
