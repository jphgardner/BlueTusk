# @bluetusk/live-svelte

Svelte 5 stores for `@bluetusk/live`. The adapter batches rapid server events into one microtask, exposes standard read-only stores, resumes through the core client, and stops the stream during component destruction.

```ts
const liveOrders = useBlueTuskLiveQuery<Order, string, Parameters>(
  client,
  { query: "recent-orders", parameters: { tenant } }
);
```

Use `$liveOrders.rows`, `$liveOrders.phase`, and `$liveOrders.error` in a component. Outside component initialization, call `createBlueTuskLiveQuery`, subscribe to its stores, and take explicit responsibility for `destroy()`.
