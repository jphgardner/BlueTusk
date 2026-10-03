# Live concepts

This page explains how BlueTusk Live turns a committed PostgreSQL change into
an update on a user's screen, so you can design queries, authorization and
limits that behave the way you expect. It builds on the shared
[core concepts](../getting-started/concepts.md), in particular
[Live results are re-queried](../getting-started/concepts.md#live-results-are-re-queried-not-copied-from-the-change-feed).

## The path from a commit to the screen

```text
PostgreSQL commit
   │  logical replication (Streams)
   ▼
LiveInvalidationConsumer ──► invalidation log: "cursor 42 changed public.todos"
                                    │
your refresh loop ── RefreshAsync ──┘
   │  did any table this query depends on change since my cursor?
   │     no  ──► nothing to do
   │     yes ──► run the registered query again, with this subscriber's scope
   ▼
keyed diff (RowAdded, RowUpdated, RowRemoved, ResultReordered, or ResultReset)
   │  appended to the replay window, numbered by sequence
   ▼
every connected client of this subscription ──► browser applies the events
```

Live never forwards row data from the change feed. The change only says
"this table changed". The rows a user sees always come from running the
registered query again with that user's parameters and scope.

## Registered queries

A **registered query** is a query plan that trusted server code creates at
startup. Clients refer to it by name and cannot send SQL. The plan type is
`LiveQueryPlan<TRow, TKey>`. It records the name, the tables it depends on, its
typed parameters, a hard result limit, how to run it and how to get each row's
key.

Most applications create plans from EF Core with
`LiveEfQueryCompiler.CompileAsync` (package `BlueTusk.Live.EntityFrameworkCore`).
The compiler accepts this shape and rejects anything else with
`LiveEfQueryRegistrationException` before any client connects:

| Rule | Example |
| --- | --- |
| One mapped root entity with a single-column primary key | `db.Todos` |
| Simple `Where` predicates over parameters | `.Where(t => t.Owner == owner)` |
| `OrderBy`/`ThenBy` that includes the primary key | `.OrderBy(t => t.Id)` |
| Exactly one `Take`, between 1 and `maximumResultCount` | `.Take(100)` |
| Optional `Include`/`ThenInclude` of one-to-many navigations | `.Include(o => o.Lines)` |

Every table reached through an `Include` becomes a dependency of the plan, so a
change to any of them refreshes the query. `CompileProjectionAsync` accepts a
separate result type for grouped and joined projections; see the
[full reference](reference.md#ef-query-registration).

Parameters are declared with `LiveQueryParameter(name, type, allowNull)` and
must be scalars: `string`, `bool`, integer and floating-point types, `decimal`,
`Guid`, `DateOnly`, `TimeOnly`, `DateTime`, `DateTimeOffset` or an enum.
Arguments must match the declared names and types exactly.

You can also construct a `LiveQueryPlan` yourself when the query is not EF
Core. You then supply the dependencies, the fingerprint
(`LiveQueryFingerprint.Create(name, version)`) and the execute delegate.

An opt-in **client query** mode lets a browser send a restricted query document
under a server-issued policy. It is off unless you register
`LiveClientQueryTransportResolver`. See the
[full reference](reference.md#capability-secured-client-queries) and
[ADR 0015](../architecture/decisions/0015-capability-secured-client-queries.md).

## Who may see what: scope and authorization

Every request goes through your **resolver**, an
`ILiveTransportSubscriptionResolver`. The transport has already checked that
the caller is authenticated; the resolver decides what they may see. It:

1. checks the query name and the caller's claims;
2. binds arguments, taking tenant or user values from the claims rather than
   from the request;
3. creates a `LiveSecurityScope(scope, authorizationPolicyVersion)`, for
   example `new LiveSecurityScope("tenant:acme", "orders-policy-v3")`;
4. returns a started `LiveSharedSubscription`.

The **subscription identity** is the combination of database identity, query
plan fingerprint, parameter values, security scope, policy version and result
limit. Return the same subscription for an identical identity, so one query
execution and one replay window serve all those clients.
`LiveSharedSubscriptionRegistry.GetOrAdd` does this lookup for you. Any
difference gives a different identity, so results never cross a scope. Change the policy
version when your authorization rules change; old resume tokens then stop
matching.

Bind tenant isolation in the plan as well. The EF compiler requires one of
three `LiveEfTenantIsolationMode` values:

| Mode | What the compiler checks |
| --- | --- |
| `RegisteredPredicate` | The `Where` clause compares the `LiveEfTenantBinding` property with the named parameter. |
| `EfGlobalQueryFilter` | The entity has an EF global query filter. |
| `DatabaseRowLevelSecurity` | Nothing; you rely on PostgreSQL row-level security for the connection's role. |

The [security checklist](README.md#security-checklist) lists the rest.

## Change signal, re-query, diff

`LiveInvalidationConsumer` is a Streams consumer. For each committed
transaction it records the affected tables in the invalidation log
(`PostgreSqlLiveInvalidationStore`) under a new **cursor** number, and only then
acknowledges the transaction to Streams. A failed write is retried by Streams,
so a change is never lost between PostgreSQL and the log.

Each subscription remembers the cursor of its last result. When your code calls
`RefreshAsync`:

- if none of the plan's tables changed since that cursor, no query runs;
- otherwise Live runs the query **once**, however many changes arrived, and
  compares the new rows with the previous rows by key.

> **Note:** In 1.1.0 your application calls `RefreshAsync`, for example from a
> background service on a short timer, as in the [quick start](quickstart.md).
> There is no built-in refresh scheduler.

The comparison produces keyed events:

| Event | Meaning |
| --- | --- |
| `InitialResult` | The complete first result. |
| `RowAdded`, `RowUpdated`, `RowRemoved` | One row changed, with its index. |
| `ResultReordered` | The same rows in a new order. |
| `ResultReset` | A complete replacement result, with a reason. |

A reset happens when a diff would exceed `LiveDiffOptions.MaximumEventsPerRefresh`
(reason `DiffLimitExceeded`), after a server restart (`ServerRestart`), when the
replay window no longer covers a new client (`ReplayExpired`), or when you call
`ResetAsync` (`QueryShapeChanged`, `SchemaChanged`).

The first result is taken carefully: Live notes the cursor, runs the query, and
checks the log again. If a relevant table changed meanwhile, it runs the query
again, up to `MaximumInitialCatchUpPasses` times. This closes the gap between
"read the rows" and "start listening".

## Sequences, replay and resume tokens

Every event gets a **sequence** number, starting at 1 for each subscription.
Before any client sees an event, Live appends it to the **replay window** in
PostgreSQL. Calling the store's `PruneAsync` removes events older than
`ReplayRetentionWindow` (one hour by default); nothing is removed until you
call it.

Each message to a client carries a **resume token**: a signed, expiring token
that names the subscription identity and the sequence. The client keeps the
latest one. When the connection drops, it reconnects with the token and
receives only the events it missed.

```text
client has seq 7 ──disconnect──► reconnect with token(seq 7)
server replays 8, 9, 10 from the window, then continues live
```

What happens when resuming is not possible:

| Situation | Server answer | Client behavior |
| --- | --- | --- |
| Token older than `ResumeTokenLifetime` (30 min) | `ResumeTokenExpired`, HTTP 409 | Drops the token, reconnects, receives a full result. |
| Events after the token were pruned | `ReplayUnavailable`, HTTP 409 | Same. |
| Token signed by an unknown key, tampered, or for another subscription | `InvalidResumeToken`, HTTP 400 | Stops with `phase: "faulted"`. |
| Server restarted | Replay continues; a `ResultReset` (`ServerRestart`) follows | Applies the reset. |

A resume token is not a saved result. A new page that only has a stored token
still needs a full result; the client handles that by requiring an
`InitialResult` or `ResultReset` before it accepts deltas.

## Batching on the client

New in 1.1.0: the browser client reduces up to `maximumBatchEvents` (default
64) events that are already available before it builds one new `rows` array and
notifies subscribers. It never waits for a timer or for more events: a smaller
batch is published at the end of each network read. Set `maximumBatchEvents: 1`
if your code must observe every intermediate state. The resume token is saved
only after the batch's rows are in `query.state`.

The framework adapters add one more step: they combine rapid notifications into
one update per microtask.

## Backpressure and limits

Every limit has a fixed default and a clear error when it is reached:

| Limit | Default | When reached |
| --- | --- | --- |
| Rows per query (`maximumResultCount`) | set per plan | Registration fails if `Take` is larger; a hand-built plan that returns more fails the refresh with `LiveQueryResultLimitException`. |
| Events per refresh (`MaximumEventsPerRefresh`) | 1,024 | A `ResultReset` replaces the diff. |
| Messages queued per client (`SubscriberBufferCapacity`) | 128 | The slow client is handled by `SlowClientPolicy`. |
| Clients per subscription (`MaximumSubscribers`) | 1,000 | HTTP 429; the client retries. |
| Replay events per connect (`MaximumReplayEventsPerConnect`) | 1,024 | HTTP 409. |
| Shared subscriptions per registry (`MaximumSharedSubscriptions`) | 10,000 | `LiveSubscriptionQuotaException` from `GetOrAdd`. |
| Request body (`MaximumRequestBytes`) | 64 KiB | HTTP 413. |

A **slow client** is one that does not read fast enough for its 128-message
queue. Live never silently drops an event for a client that keeps going. With
`LiveSlowClientPolicy.Disconnect` (the default) the connection ends and the
client resumes from its token. With `RequireReset` the client receives a reset
message, drops its token and reconnects for a full result.

## Transports

All transports carry the same messages: an event, its sequence and a fresh
resume token.

| Transport | Package | Map with | Default path | Clients |
| --- | --- | --- | --- | --- |
| Server-sent events | `BlueTusk.Live.ServerSentEvents` | `MapBlueTuskLiveServerSentEvents()` | `POST /bluetusk/live/sse` | `@bluetusk/live` and its framework adapters |
| SignalR | `BlueTusk.Live.SignalR` | `MapBlueTuskLiveHub()` | `/bluetusk/live` | Any SignalR client; streaming method `SubscribeAsync` |
| gRPC | `BlueTusk.Live.Grpc` | `MapBlueTuskLiveGrpc()` | `bluetusk.live.v1.BlueTuskLive/Subscribe` | Generated gRPC clients; the package includes the .NET one |

The SSE endpoint is a `POST` that the client reads as a stream with `fetch`.
It is not compatible with the browser's `EventSource`, which only sends `GET`.
Choose SSE for browsers, SignalR when the app already uses it, and gRPC for
service-to-service. See [configuration](configuration.md#transports).

## Next steps

- [Configuration](configuration.md): every option and default.
- [Framework guides](clients.md): Angular, React, Vue and Svelte.
- [Troubleshooting](troubleshooting.md).
- [Full engineering reference](reference.md).
