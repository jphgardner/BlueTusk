# @bluetusk/live

Framework-neutral BlueTusk Live client for the fetch-streaming SSE endpoint. It applies keyed result events, persists the latest signed resume token through an application callback, reconnects with bounded exponential backoff, and discards a stale token only after the server explicitly reports an expired replay window.

```ts
const query = new BlueTuskLiveClient({
  endpoint: "/bluetusk/live/sse"
}).createQuery<Order, string, { tenant: string }>({
  query: "recent-orders",
  parameters: { tenant: "acme" }
});

query.subscribe(state => render(state.rows));
query.start();
```

Call `stop()` when the owning view is destroyed. The default `createQuery`
path never sends SQL or expression trees; `query` is the name of a trusted
server registration.

## Bounded updates

The client reduces up to 64 already available SSE frames before creating one
ordered rows array and notifying subscribers. It still validates and applies
each event in sequence. A partial batch is published at the end of the current
network read: there is no timer, animation-frame delay, or wait for 64 events.
Framework adapters additionally coalesce their notifications in a microtask.

```ts
const client = new BlueTuskLiveClient({
  endpoint: "/bluetusk/live/sse",
  maximumBatchEvents: 64, // Default; integer from 1 through 1024.
  onResumeToken: token => saveTokenForThisQuery(token)
});
```

Use `maximumBatchEvents: 1` if a core subscriber must observe every event's
intermediate snapshot. Tokens are saved only after the corresponding batch's
rows and sequence are committed to `query.state`; a callback can read that
state. A later invalid event leaves the valid prefix available and never saves
a token beyond it. If token persistence throws, the query faults rather than
retrying that application error in a transport loop.

Previously published rows arrays are not changed by later events. Treat them
and their row objects as read-only: this is shallow immutability, not a deep
freeze. `LiveResultStore.apply()` retains per-event snapshots; `applyEvent()`
reduces without materializing and `applyBatch()` returns one snapshot after a
sequential batch. A failed batch retains its successful prefix, not an atomic
rollback of the entire batch.

A resume token is not a saved result snapshot. Do not construct a new empty
query using only a persisted token and expect it to reconstruct missing rows.
An existing query can reconnect with its retained state; a fresh query needs an
authoritative initial result. Explicit replay expiry/reset clears the token and
requires a fresh authoritative result before deltas, including after a server
restart with a lower sequence.

Batching is not a release-performance claim. Run `npm run check:clients` at the
repository root to build and test all five clients.

An application may separately expose a capability-secured client-query
resolver. This is opt-in server policy, not an unrestricted database endpoint:

```ts
const query = client.createClientQuery("orders-read", {
  language: "linq",
  linq: {
    schema: "sales",
    table: "orders",
    columns: ["id", "tenant_id", "total"],
    filters: [
      { column: "tenant_id", operator: "Equal", parameter: "tenant" }
    ],
    orderings: [
      { column: "id", direction: "Ascending" }
    ]
  },
  keyColumns: ["id"],
  maximumResultCount: 100,
  parameters: {
    tenant: { type: "string", value: "acme" }
  }
});
```

Rows expose `values` and a stable `fingerprint`; Live event keys are stable
SHA-256 strings derived from the configured key columns. The server authorizes
the named capability on every connection, selects the database/RLS scope and
hard limits, and may disable raw SQL while allowing the remote LINQ document.
No CLR expression tree or executable client code crosses the transport.
