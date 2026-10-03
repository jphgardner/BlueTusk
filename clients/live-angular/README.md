# @bluetusk/live-angular

Angular signals adapter for `@bluetusk/live`.

```ts
bootstrapApplication(AppComponent, {
  providers: [
    provideBlueTuskLive(new BlueTuskLiveClient({
      endpoint: "/bluetusk/live/sse"
    }))
  ]
});
```

Inject `BlueTuskLiveAngular`, call `createQuery`, and bind its read-only `state`, `rows`, `phase`, and `error` signals. The adapter stops its underlying fetch stream when `destroy()` is called.

Call `start()` explicitly and bind `destroy()` to the owning component/service
lifetime, for example `destroyRef.onDestroy(() => query.destroy())`. Destruction
is idempotent, cancels queued signal publication, and prevents restarting the
destroyed adapter. `stop()` is for a temporary pause of an otherwise live owner.

The [core client](../live/README.md) batches events
before materializing results; this adapter then coalesces signal updates in one
microtask. Tests use actual Angular signals, environment-provider injection and
a 100,000-row core-client SSE journey. Run `npm run check:clients` from the
repository root; this is local developer validation, not release qualification.
