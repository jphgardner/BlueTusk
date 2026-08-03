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
