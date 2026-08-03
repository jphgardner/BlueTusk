# @bluetusk/live-react

React `useSyncExternalStore` adapter for `@bluetusk/live`.

```ts
const state = useBlueTuskLiveQuery<Order, string, Parameters>(
  client,
  useMemo(() => ({
    query: "recent-orders",
    parameters: { tenant }
  }), [tenant])
);
```

Memoize the request when its semantic values have not changed. The hook starts the query after mount and stops it during cleanup.
