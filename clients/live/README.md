# @bluetusk/live

Framework-neutral BlueTusk Live client for the fetch-streaming SSE endpoint. It applies keyed result events, persists the latest signed resume token through an application callback, reconnects with bounded exponential backoff, and discards a stale token only after the server explicitly reports an expired replay window.

```ts
const query = new BlueTuskLiveClient({
  endpoint: "/bluetusk/live/sse"
}).createQuery<Order, string>({
  query: "recent-orders",
  parameters: { tenant: "acme" }
});

query.subscribe(state => render(state.rows));
query.start();
```

Call `stop()` when the owning view is destroyed. The default `createQuery`
path never sends SQL or expression trees; `query` is the name of a trusted
server registration.

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
