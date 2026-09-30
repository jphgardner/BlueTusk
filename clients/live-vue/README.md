# @bluetusk/live-vue

Vue 3 composables for `@bluetusk/live`. The adapter batches rapid server events into one microtask, exposes read-only refs, resumes through the core client, and stops the stream when the component scope is disposed.

```ts
const liveOrders = useBlueTuskLiveQuery<Order, string, Parameters>(
  client,
  { query: "recent-orders", parameters: { tenant } }
);
```

Read `liveOrders.rows.value`, `phase.value`, or `error.value` from setup code and templates. For use outside a Vue setup/effect scope, call `createBlueTuskLiveQuery` and take explicit responsibility for `destroy()`.
