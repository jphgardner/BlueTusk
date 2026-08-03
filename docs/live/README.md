# BlueTusk Live

BlueTusk Live is the authorised real-time query layer built on BlueTusk Streams. Trusted server code registers bounded query plans; remote clients can select a registration and supply only its declared scalar parameters. SQL and expression trees are never accepted from clients.

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

`BlueTusk.Live.EntityFrameworkCore` compiles trusted `IQueryable<TEntity>` factories at startup. The preview compiler accepts one mapped entity with one primary key, simple predicates, explicit tenant isolation, deterministic ordering including the primary key, and one bounded `Take`. It asks the configured EF provider to translate the query during registration, so unsupported shapes fail before a client can subscribe.

Tenant isolation must be declared as PostgreSQL RLS, an EF global query filter, or a registered entity-property/typed-parameter equality that the compiler verifies in the predicate. The query factory and key selector are server-owned delegates; no client SQL, LINQ, or expression tree crosses the transport boundary.

## Replay window

Live replay events use a versioned JSON media type and a SHA-256 integrity hash. The PostgreSQL store appends a contiguous sequence only when its expected prior sequence matches, treats a byte-identical crash retry as already stored, and rejects divergent forks. Reads distinguish current, available, expired, and unknown subscriptions. Retention pruning advances an explicit first-available watermark so an expired resume token produces a reset instead of a silent gap.

## Shared subscriptions and backpressure

`LiveSharedSubscription<T, TKey>` owns one authoritative query session for one complete subscription identity. Matching clients share its query and replay append; different parameter, tenant, user, policy-version, database, plan, or limit fingerprints cannot enter the same registry entry.

Reconnect is serialized with publication so replay and the newly attached bounded channel have no race. Subscriber counts, replay batch size, shared subscription count, and per-client pending messages are bounded. A slow client is either disconnected with a specific error or sent a `ResetRequired` control message after its buffer is drained, according to explicit policy. No path silently drops a diff while allowing the client to continue.

## ASP.NET transports

`BlueTusk.Live.AspNetCore` defines the authenticated transport session and an application-supplied resolver. The resolver is trusted server code: it selects a registered plan, binds the request JSON to that plan's declared scalar parameters, derives the caller's security scope, and returns the matching shared subscription. Anonymous callers and non-object parameter payloads are rejected before connection.

`BlueTusk.Live.SignalR` exposes a streaming hub, and `BlueTusk.Live.ServerSentEvents` exposes a fetch-streaming POST endpoint. Both send replay before new events and attach a fresh signed, expiring, subscription-bound resume token to every sequence. SSE disables proxy buffering and maps quota, invalid token, expired replay, and unavailable subscription states to explicit HTTP responses.

The next Live slices connect the invalidation contract to the PostgreSQL relay, add EF query registration, replay retention, ASP.NET transports, and client SDKs. Package publication stays disabled until those vertical gates pass.
