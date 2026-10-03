# Troubleshoot BlueTusk Live

This page helps you find out why live updates do not arrive, why a client is
refused, or why it keeps reconnecting. Each section lists the symptom, the
cause and the fix, with the real error text. For background, see
[Concepts](concepts.md).

## Start with one request from the command line

Take the browser out of the picture. This request opens the SSE stream the way
the client does:

```bash
curl -N -X POST http://localhost:5080/bluetusk/live/sse \
  -H "content-type: application/json" \
  -H "X-Demo-User: alice" \
  -d '{"query":"my-todos","parameters":{}}'
```

Replace the URL, query name and authentication header with yours (for example
`-H "authorization: Bearer ..."`). In PowerShell 7.3 or later, call
`curl.exe` with the same arguments on one line.

A healthy stream starts with an `InitialResult` event and stays open:

```text
id: 1
event: change
data: {"kind":"Event","sequence":1,"resumeToken":"bt1....","event":{"sequence":1,"kind":"InitialResult",...}}
```

If you get an HTTP status instead, look it up in the next table.

## What each error response means

The SSE endpoint and the gRPC service map each failure to a status. SignalR
sends the same failures as a `HubException` with the message shown.

| SSE | gRPC | Cause | Fix |
| --- | --- | --- | --- |
| 401 | `Unauthenticated` | The request has no authenticated user (`An authenticated principal is required for a Live subscription.`), or your resolver threw `LiveTransportAuthorizationException`. | Send credentials and run `UseAuthentication()` before the endpoint. See [Unauthorized or forbidden](#unauthorized-or-forbidden). |
| 400 | `InvalidArgument` | The body is not valid JSON, `parameters` is not an object (`Live subscription parameters must be a JSON object.`), your resolver threw `LiveTransportRequestException`, or the resume token is invalid. | Send `{ "query": "...", "parameters": {} }`. For tokens, see [resume tokens](#the-client-stops-with-http-400-after-a-server-restart). |
| 405 | | A `GET` request, for example from the browser's `EventSource`. | Use `@bluetusk/live`, which sends `POST`. |
| 413 | `ResourceExhausted` | The request is larger than `MaximumRequestBytes` (64 KiB). | Send fewer or shorter parameters, or raise the limit. |
| 429 | `ResourceExhausted` | `QuotaExceeded`: the subscription already has `MaximumSubscribers` clients. | See [too many subscriptions](#too-many-subscriptions-or-clients). |
| 409 | `FailedPrecondition` | `ResumeTokenExpired`, `ReplayUnavailable` or `ReplayLimitExceeded`. | The client drops its token and reconnects. See [resume tokens](#the-client-reloads-everything-after-a-reconnect). |
| 503 | `Unavailable` | `NotStarted`: the resolver returned a subscription that was not started. | Call `StartAsync` before returning it. |
| 500 | `Internal` | Your resolver or the database threw an unexpected exception. | Read the server log. |

## No updates arrive

The page shows the first result but later changes never appear. Work through
the path in order: PostgreSQL, Streams, the invalidation log, your refresh loop.

| Check | How | Fix |
| --- | --- | --- |
| The table is in the publication | `SELECT * FROM pg_publication_tables WHERE pubname = 'live_todos';` | `ALTER PUBLICATION live_todos ADD TABLE public.todos;` |
| The replication slot is active | `SELECT slot_name, active FROM pg_replication_slots;` shows your slot with `active = t` while the app runs. | If it is missing or inactive, read the Streams worker error in the log or the `bluetusk_streams` health check. See [Streams troubleshooting](../streams/troubleshooting.md). |
| Invalidations are recorded | `SELECT database_identity, max(cursor) FROM bluetusk_live.live_invalidations GROUP BY database_identity;` grows after each write. | If not, Streams is not delivering. Check `wal_level = logical` and the role's `REPLICATION` attribute. |
| The database identities match | The `database_identity` in that result, which you pass to `new LiveInvalidationConsumer("app", store)`, equals the plan's `databaseIdentity`. | Use the same value. A mismatch means the plan never sees its invalidations. |
| Something calls `RefreshAsync` | A breakpoint or log line in your refresh loop. | 1.1.0 has no built-in scheduler; call `RefreshAsync` on every running subscription, as in the [quick start](quickstart.md#3-write-the-server). |
| The refresh does not fail | Your refresh loop's log. | Fix the reported exception; a failed refresh is retried on the next call. |
| The row really changed | Compare the old and new row. | `rowComparer` decides whether a row changed. A comparer that reports "equal" hides updates. |

A refresh that reports `Live query '<name>' returned <n> rows, exceeding its
bound of <limit>.` (`LiveQueryResultLimitException`) comes from a hand-built
plan whose query returns more rows than its limit. Add a matching `Take` or
`LIMIT`.

A first result that fails with `Live query '<name>' could not reach a quiet
invalidation boundary after 32 authoritative query passes.`
(`LiveInitialCatchUpException`) means the tables changed during every attempt.
Retry later, or raise `MaximumInitialCatchUpPasses`.

### Every row is sent again on each change

Each change produces `RowUpdated` for rows that did not change. The row type
compares by reference, so `EqualityComparer<T>.Default` treats every re-queried
row as new. Make the row type a `record`, or pass a `rowComparer` that compares
values.

## Unauthorized or forbidden

| Symptom | Cause | Fix |
| --- | --- | --- |
| HTTP 401, client `phase: "faulted"` with `BlueTusk Live endpoint returned HTTP 401.` | No authenticated user reached the endpoint. | Send the cookie or `authorization` header; register authentication middleware before mapping the endpoint. |
| HTTP 401 after the page has been open for a while | The access token in `headers` expired. `headers` is fixed when the client is created. | Supply the current token from a custom `fetch`, then call `query.start()` again. |
| HTTP 400 or 401 from your resolver | Your resolver rejected the query name or the caller's claims. | The SSE response has no body and the endpoint does not log the message. Log the reason in your resolver before you throw. |
| SignalR or gRPC returns 401 before the stream starts | The hub and service carry `[Authorize]`. | Register `AddAuthentication`, `AddAuthorization` and the matching middleware. |

A custom `fetch` that adds the current token:

```typescript
const client = new BlueTuskLiveClient({
  endpoint: "/bluetusk/live/sse",
  fetch: (input, init) => {
    const headers = new Headers(init?.headers);
    headers.set("authorization", `Bearer ${getAccessToken()}`);
    return fetch(input, { ...init, headers });
  }
});
```

Live never decides authorization by itself. If a user sees rows they should
not, check that your resolver takes tenant and user values from claims, not
from `parameters`, and that the plan's tenant isolation is set. See
[scope and authorization](concepts.md#who-may-see-what-scope-and-authorization).

## The page says "Illegal invocation"

The client's `phase` is `reconnecting` with
`Failed to execute 'fetch' on 'Window': Illegal invocation`, and it never
connects. This happens only with the published 1.0.0 and 1.1.0-rc.1 versions
of `@bluetusk/live` in a browser, including through the framework adapters.
The fix ships in 1.1.0. Until you can upgrade, pass `fetch` when you create
the client:

```typescript
fetch: (input, init) => fetch(input, init)
```

## Reconnect loops

The phase keeps switching between `live` and `reconnecting`.

| Cause | How to tell | Fix |
| --- | --- | --- |
| A proxy or load balancer closes idle connections | Reconnects happen at a fixed interval when nothing changes. The server sends no keep-alive messages. | Raise the proxy's read or idle timeout for the Live path. Reconnects then resume from the token without data loss. |
| The server returns 429, 503 or 5xx | `state.error` is a `LiveHttpError` with that `status`. | Fix the server error; the client retries with backoff up to `maximumRetryDelayMs`. |
| The client is too slow | Server status `LastDisconnectCode` is `slow-client-disconnect`; metric `bluetusk.live.slow_client.disconnects` grows. | Render less often, or raise `SubscriberBufferCapacity`. |
| A rate limiter on the endpoint | 429 from your middleware, not from Live. | Exempt or raise the limit for the Live endpoint; each reconnect is a new request. |

The client stops retrying (`phase: "faulted"`) for statuses other than 409
with a token, 429, 503 and 5xx, and for protocol errors. After you fix the
cause, call `query.start()` again.

## Resume tokens

### The client reloads everything after a reconnect

The client receives a full result (`ResultReset` or `InitialResult`) instead of
a few missed events. This is expected when:

- the token was older than `ResumeTokenLifetime` (30 minutes): status
  `ResumeTokenExpired`, HTTP 409;
- the missed events were pruned from the replay window: `ReplayUnavailable`,
  HTTP 409;
- more than `MaximumReplayEventsPerConnect` (1,024) events were missed:
  `ReplayLimitExceeded`, HTTP 409;
- the server restarted: the replay ends with a `ResultReset` whose reason is
  `ServerRestart`.

The client then discards the token and reconnects for a full result. Nothing
is lost.

### The client stops with HTTP 400 after a server restart

The token was signed with a key the server no longer has
(`InvalidResumeToken`; resume metric outcome `unknownkey` or
`invalidsignature`). This happens when the signing key is generated at startup
or differs between instances.

Load the same keys on every instance from configuration or a secret store. When
you rotate, keep the old key listed. See
[resume tokens](configuration.md#resume-tokens-and-requests).

### HTTP 409 on a brand-new connection

The client is `faulted` with `BlueTusk Live endpoint returned HTTP 409.` on its
first connection. The replay window holds more than
`MaximumReplayEventsPerConnect` events for that subscription, and a new client
replays from the start of the window. Call `PruneAsync` on a schedule (see
[configuration](configuration.md#postgresqllivestoreoptions)), shorten
`ReplayRetentionWindow`, or raise `MaximumReplayEventsPerConnect`.

### "A fresh Live connection must establish an authoritative snapshot before deltas."

A new query was created with a saved `resumeToken`. A token does not contain
the rows, so the new query receives only changes and stops. Do not pass
`resumeToken` to `createQuery`; the running query keeps its own token.

## Too many subscriptions or clients

| Symptom | Cause | Fix |
| --- | --- | --- |
| HTTP 429, client keeps retrying | One subscription has `MaximumSubscribers` (1,000) clients. | Raise the limit, or check for leaked connections: a page that creates queries without calling `stop()` or `destroy()`. |
| HTTP 500 and `The shared Live subscription limit of 10000 has been reached.` | Your resolver called `LiveSharedSubscriptionRegistry.GetOrAdd` past `MaximumSharedSubscriptions`. | Map the exception to a 429, as below, and look for parameters that create many distinct subscriptions. |
| `Live replay sequence for '<id>' is <n>, expected <m>.` (`LiveReplaySequenceException`) | Two processes publish the same subscription to one replay store, for example two server instances. | Make sure one process owns each subscription. |

Map the registry quota to HTTP 429 (gRPC `ResourceExhausted`) in your resolver:

```csharp
try
{
    selected = registry.GetOrAdd(candidate);
}
catch (LiveSubscriptionQuotaException)
{
    await candidate.DisposeAsync();
    throw new LiveTransportConnectException(LiveSubscriptionConnectStatus.QuotaExceeded);
}
```

## Proxies buffer the stream

Events arrive in bursts, or only when the connection closes. Something between
the server and the browser buffers the response.

Live already sends `Cache-Control: no-cache, no-store` and
`X-Accel-Buffering: no`, and turns off ASP.NET Core response buffering. Check
the rest of the path:

- **nginx:** `X-Accel-Buffering: no` is honored. If you strip that header, set
  `proxy_buffering off;` for the Live location.
- **Response compression:** do not compress `text/event-stream`. Compression
  middleware holds data until a block fills.
- **CDNs and API gateways:** turn off response buffering or caching for the
  Live path, and allow long-lived responses.
- **HTTP/1.1 proxies:** the stream is a chunked response; the proxy must pass
  chunks through as they arrive.

## CORS

The browser blocks the request when the page and the Live endpoint have
different origins. The `POST` with `content-type: application/json` causes a
preflight request.

Allow the origin, the `POST` method and the headers you send. If you use
cookies, also allow credentials and set `credentials: "include"` in the client:

```csharp
builder.Services.AddCors(options => options.AddPolicy("live", policy => policy
    .WithOrigins("https://app.example.com")
    .WithMethods("POST")
    .WithHeaders("content-type", "authorization")
    .AllowCredentials()));

var app = builder.Build();
app.UseCors();
app.MapBlueTuskLiveServerSentEvents().RequireCors("live");
```

## Startup errors

| Message | Cause | Fix |
| --- | --- | --- |
| `Live query '<name>' is already registered.` | Two plans with one name in a `LiveQueryRegistry`. | Use unique names. |
| `A Live query must contain one bounded Take operation.` | The EF query has no `Take`. | Add `.Take(n)` with `n` at most `maximumResultCount`. |
| `A Live query must have deterministic ordering that includes primary key '<key>'.` | The order does not include the key. | Add `.ThenBy(x => x.Id)`. |
| `Queryable method '<name>' is not supported by the initial Live compiler.` | For example `Select`, `Skip` or `Join`. | Use the supported shape, or `CompileProjectionAsync`; see [concepts](concepts.md#registered-queries). |
| `Live query predicate does not bind entity property '<property>' to parameter '<parameter>'.` | `RegisteredPredicate` isolation, but the `Where` does not compare them. | Add `.Where(x => x.Owner == owner)` using that parameter. |
| `A resume-token signing key must contain at least 32 bytes.` | The key is too short. | Generate 32 random bytes. |
| `Exactly one resume-token signing key must be primary.` | No key, or several keys, has `isPrimary: true`. | Mark exactly one. |
| `Existing slot <slot> is active or does not belong to the configured pgoutput snapshot source; it cannot be replaced safely.` | Another process uses the replication slot. | Give each process its own slot, or stop the other one. |

## Client exceptions

| Message | Cause |
| --- | --- |
| `maximumBatchEvents must be an integer from 1 through 1024.` (`RangeError`) | Invalid `maximumBatchEvents`. |
| `BlueTusk Live retry settings are invalid.` (`RangeError`) | Negative delays, a maximum below the initial delay, or jitter outside 0 to 1. |
| `The application's Live resume-token callback failed.` | Your `onResumeToken` threw. The query stops; it does not retry. |
| `Live sequence jumped from <a> to <b>.` (`LiveProtocolError`) | Events were lost between server and client, usually a proxy that drops data. |
| `useBlueTuskLiveQuery must run inside a Vue setup scope; ...` | Called outside `setup()`. Use `createBlueTuskLiveQuery` and call `destroy()` yourself. |
