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

Keep the client instance stable too. Changing the request or client replaces the
query and cleans up the old subscription. The current candidate keeps its
`useSyncExternalStore` subscribe/getSnapshot callbacks stable while the query is
unchanged, so ordinary renders do not unsubscribe and subscribe again.

The [core client](../live/README.md) batches reduction before materialization;
React notifications are additionally coalesced in a microtask. Tests mount the
actual hook with React DOM, exercise StrictMode cleanup and request replacement,
check server rendering does not connect, and run a real core-client SSE burst.
Run `npm run check:clients` at the repository root. These developer tests are not
full browser/fan-out release-performance evidence.
