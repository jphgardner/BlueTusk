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

Call `stop()` when the owning view is destroyed. The client never sends SQL or expression trees; `query` is the name of a trusted server registration.
