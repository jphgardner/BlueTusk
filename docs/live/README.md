# BlueTusk Live

BlueTusk Live is the authorised real-time query layer built on BlueTusk Streams.
Trusted server code registers bounded query plans by default. V1 also provides
an explicitly enabled client-query capability for bounded exploratory SQL and a
finite remote LINQ document. Uploaded CLR expression trees and dynamically
compiled client code are never accepted.

## Security and sharing boundary

Every shared-subscription identity binds the database identity, query-plan fingerprint, canonical typed parameters, tenant/user security scope, authorisation-policy version, and result limit. A change to any field creates a different subscription. This prevents result and replay sharing across security boundaries.

PostgreSQL/EF query results remain authoritative. CDC will be used only to invalidate an affected registration, after which BlueTusk reruns the authorised query and computes a keyed result diff.

## Core delivery contracts

The core package currently provides:

- exact typed parameter binding for a restricted scalar allowlist;
- stable plan, parameter, and subscription fingerprints;
- duplicate-key rejection and keyed initial/add/update/remove/reorder/reset output;
- a bounded diff budget that falls back to an authoritative reset;
- signed, expiring, versioned, subscription-bound resume tokens with signing-key rotation.

## Gap-free initial delivery

`LiveQuerySession<T, TKey>` reserves the current durable invalidation cursor before executing the authorised query. It then checks the log through the cursor observed after that query. If an affected table changed, the result is discarded and queried again. Only a result that reaches a quiet cursor boundary is emitted as `InitialResult`; subsequent refreshes start strictly after that cursor.

Refreshes coalesce every invalidation since the last cursor into at most one authoritative query. Unrelated-table activity advances the cursor without querying. A backward cursor, an over-limit result, duplicate keys, or perpetual initial churn fails closed with a specific diagnostic.

`BlueTusk.Live.DependencyInjection` supplies a PostgreSQL invalidation store in the relay control schema. It atomically deduplicates source transactions, records the distinct affected tables, and acknowledges the Streams delivery only after the invalidation commit succeeds. Typed and dynamic row changes use the same dependency extraction path. Failed writes are nacked for safe redelivery.

## EF query registration

`BlueTusk.Live.EntityFrameworkCore` compiles trusted `IQueryable<TEntity>` factories at startup. The compiler accepts one mapped root entity with one primary key, simple predicates, explicit tenant isolation, deterministic ordering including the primary key, and one bounded `Take`. Vetted one-to-many `Include` chains and PostgreSQL full-text predicates are supported. Every mapped entity reached by an include becomes an invalidation dependency, and multi-table plans no longer advertise the `SingleTable` capability. It asks the configured EF provider to translate the query during registration, so unsupported shapes fail before a client can subscribe.

Tenant isolation must be declared as PostgreSQL RLS, an EF global query filter, or a registered entity-property/typed-parameter equality that the compiler verifies in the predicate. The EF query factory and key selector are server-owned delegates; this trusted-registration path does not accept a client query document.

`CompileProjectionAsync` accepts a separate mapped root and immutable result
type for bounded projections. Its current allowlist covers:

- model-proven one-to-many joins expressed as `SelectMany` over a direct
  collection navigation;
- `GroupBy` projections with `Count`, `LongCount`, `Sum`, `Min`, `Max`, and
  `Average`; and
- PostgreSQL full-text expressions inside otherwise supported predicates.

The registered result key must be a direct result property and deterministic
ordering must include the same property. The compiler requires one bounded
`Take`, verifies registered-predicate tenant isolation on the mapped root
before projection, derives every invalidation table from the EF model and
expression, and asks EF to translate the complete query at startup. Raw
`Join`/`GroupJoin`, unproven `SelectMany`, client evaluation, unbounded output,
and arbitrary method calls fail registration with an actionable diagnostic.

## Capability-secured client queries

`LiveClientQueryCompiler` creates an ordinary
`LiveQueryPlan<LiveClientRow, string>` from a trusted application-issued
`LiveClientQueryPolicy`. Trusted registration remains the default; a client
cannot enable this path or choose its data source, policy, database identity,
security scope, role, relation allowlist, timeouts, or resource limits.

Remote LINQ is a JSON relational document—not a serialized CLR expression
tree. It permits an allowlisted table and columns, parameter-bound comparisons,
null tests, starts-with/contains filters, projection, deterministic ordering
including every result key, and a mandatory limit. BlueTusk quotes every
identifier and generates named-parameter SQL. The exact relation becomes the
invalidation dependency.

Raw SQL is separately disabled by default. Enabling it requires a policy with
both `DatabaseRowLevelSecurity` and `DedicatedReadOnlyRole`. Execution uses the
grant's application-owned data source and:

- begins a read-only transaction before the query;
- enables `row_security`;
- applies statement, lock, and idle-in-transaction timeouts;
- binds only declared scalar parameters;
- bounds query bytes, parameter count, result rows, columns, and serialized
  bytes; and
- rejects comments, multiple statements, positional parameters, database
  mutation/administration commands, row locks, and known side-effecting server
  functions.

Those lexical checks are defense in depth, not a SQL sandbox. The dedicated
role must not be a database owner, superuser, or have `BYPASSRLS`. Revoke
function execution from `PUBLIC` and grant only capability-approved
side-effect-free functions; a read-only transaction cannot undo an external
side effect performed by a user-defined function. SQL subscriptions
conservatively invalidate on every relation declared by the policy.

`LiveClientQueryTransportResolver` implements the existing authenticated
transport resolver. The application supplies `ILiveClientQueryAuthorizer`; it
is called for every connection and returns a `LiveClientQueryGrant` or denies
the request. The resolver parses a fail-closed transport document, compiles and
binds the plan, partitions it by the returned `LiveSecurityScope`, starts the
ordinary gap-free Live session, and shares only an identical complete
subscription identity.

See [ADR 0015](../architecture/decisions/0015-capability-secured-client-queries.md)
for the threat model and operator obligations.

## Replay window

Live replay events use a versioned JSON media type and a SHA-256 integrity hash. The PostgreSQL store appends a contiguous sequence only when its expected prior sequence matches, treats a byte-identical crash retry as already stored, and rejects divergent forks. Reads distinguish current, available, expired, and unknown subscriptions. Retention pruning advances an explicit first-available watermark so an expired resume token produces a reset instead of a silent gap.

A new client never depends on an initial event that may already have expired. When the retained beginning is unavailable, a tokenless connection makes the shared subscription run a new authoritative query, persists and broadcasts a `ReplayExpired` reset, and starts the client from that sequence. A resumed client still receives an explicit unavailable result and must reconnect without its stale token.

## Shared subscriptions and backpressure

`LiveSharedSubscription<T, TKey>` owns one authoritative query session for one complete subscription identity. Matching clients share its query and replay append; different parameter, tenant, user, policy-version, database, plan, or limit fingerprints cannot enter the same registry entry.

Reconnect is serialized with publication so replay and the newly attached bounded channel have no race. Subscriber counts, replay batch size, shared subscription count, and per-client pending messages are bounded. A slow client is either disconnected with a specific error or sent a `ResetRequired` control message after its buffer is drained, according to explicit policy. No path silently drops a diff while allowing the client to continue.

Each shared subscription now exposes an allocation-free operational snapshot: open/connected counts, active subscribers, fan-out deliveries, resume attempts/rejections, replay rejections/events/bytes appended, quota rejections, and the last bounded-buffer disconnect code. The registry exposes sorted snapshots plus its own shared-query quota pressure without revealing result rows or parameter values.

## ASP.NET transports

`BlueTusk.Live.AspNetCore` defines the authenticated transport session and an application-supplied resolver. The resolver is trusted server code: it selects a registered plan or an explicitly granted client-query capability, binds the request JSON to declared scalar parameters, derives the caller's security scope, and returns the matching shared subscription. Anonymous callers and non-object parameter payloads are rejected before connection.

`BlueTusk.Live.SignalR` exposes a streaming hub, and `BlueTusk.Live.ServerSentEvents` exposes a fetch-streaming POST endpoint. Both send replay before new events and attach a fresh signed, expiring, subscription-bound resume token to every sequence. SSE disables proxy buffering and maps quota, invalid token, expired replay, and unavailable subscription states to explicit HTTP responses.

`BlueTusk.Live.Grpc` exposes the same authenticated stream as a versioned protobuf service. Parameters remain JSON inside the trusted query-registration envelope and are size-bounded before parsing. gRPC status codes distinguish authentication, invalid input/token, expired replay, quota pressure, and temporary unavailability; event payloads remain the same versioned Live JSON used by replay and browser clients.
The package includes both the ASP.NET service base and the generated .NET
streaming client for the versioned contract.

## Browser clients

`@bluetusk/live` is the framework-neutral fetch-streaming client. It parses chunked UTF-8 SSE frames, applies every keyed Live event to a local result, rejects invalid sequence/key transitions, persists signed resume tokens through an application callback, and reconnects with bounded jittered backoff. A `409` only discards a resume token when one was actually supplied; tokenless conflicts and malformed payloads fail closed.

`@bluetusk/live-angular` exposes the same query state through Angular read-only signals. `@bluetusk/live-react` uses `useSyncExternalStore`, preserving React concurrent-render consistency. Both adapters own only lifecycle integration; protocol, recovery, and result semantics remain in `@bluetusk/live`.

## Hosting and testing

`BlueTusk.Live.Aspire` wires distinct application-query and relay-control resources plus explicit quota, buffer, replay-retention, and transport settings into a Live host. It rejects a topology that aliases the application and control database.

`BlueTusk.Live.Testing` provides database-scoped deterministic invalidations, a sequence-fenced and retention-aware in-memory replay store, and a public replay-store conformance kit for custom providers. The in-memory components are for tests only and preserve the same replay identity, integrity, idempotency, and expiry rules as durable storage.

## Security and load gates

The offline adversarial suite covers initial-query/change races, concurrent
reconnect/publication ordering, tampered and cross-scope resume tokens, expired
replay, malicious parameter shapes, duplicate keys, cursor regression,
slow-client exhaustion, and tenant/policy partitioning. A repeated race gate
proves that whether reconnect or publication wins the subscription lock, each
of 64 successive sequences arrives exactly once through replay or the bounded
live channel.

The deterministic scale gate coalesces 100 relevant invalidations into one
authoritative refresh and fans its single update to 64 subscribers. The
checked-in Ryzen 7 5800X/.NET 10 ShortRun measured a 1,000-row/one-update keyed
diff at 76.4 µs and 221,872 B, replay serialization at 881 ns and 832 B, and
the complete 100-invalidation/64-subscriber lifecycle at 92.3 µs and 175,060
B. Machine-checked budgets cap those paths at 235,000 B, 900 B, and 185,000 B
respectively. These are local regression baselines, not network latency or
universal throughput claims.

The stable `1.0.0` release-prepared family has passed its implementation audit. The
PostgreSQL 15–19 matrix persists initial and update replay in the production
store and drives signed disconnect/resume delivery through real SSE,
SignalR/WebSockets, and HTTP/2 gRPC endpoints. Live candidate packages are
reproducible, but publication is disabled until its V1 dependencies and
exact-commit release gates pass. See the
[1.0.0 release record](release-notes-1.0.0.md) for exact scope and
boundaries.

The [public API candidate freeze](api-compatibility.md) and
[durable format registry](format-compatibility.md) prepare the Live 1.0 surface
without claiming that its dependency release gates or publication have completed.

Run the production-store transport gate against any supported PostgreSQL
service:

```powershell
$env:BLUETUSK_TEST_CONNECTION_STRING = "Host=localhost;Port=5419;Username=postgres;Password=postgres;Database=bluetusk_tests;SSL Mode=Disable;Channel Binding=Disable"
dotnet test tests/BlueTusk.Live.Tests --filter FullyQualifiedName~LivePostgreSqlTransportMatrixTests
```

Run the offline client-query policy, transport, sharing, and bounded-execution
suite:

```powershell
dotnet test tests/BlueTusk.Live.Tests/BlueTusk.Live.Tests.csproj --filter FullyQualifiedName~LiveClientQueryTests
npm run build --prefix clients/live
npm test --prefix clients/live
```

Setting `BLUETUSK_TEST_CONNECTION_STRING` adds the live PostgreSQL proof that
parameters materialise correctly and an otherwise-allowed user function cannot
write through the enforced read-only transaction.

## Control-plane visibility

`HostedLiveControlPlaneQueryService` projects registry and subscription telemetry without exposing rows or parameter values. Query, parameter, and subscription identities remain fingerprints. The default scope redactor preserves only a safe category such as `tenant` and a short one-way hash; applications can provide a stricter or operator-friendly redactor. Invalidation head/cursor lag is calculated per database and cursor regression is surfaced as a diagnostic instead of an unsigned value.

The dashboard adds authorised JSON and HTML Live views for shared-query counts, active clients, fan-out, query fingerprints, redacted tenant/security scopes, invalidation lag, replay bytes/events, resume rejection history, quota pressure, and slow-client disconnect causes.
