# Configure BlueTusk Live

This page lists every setting you can change in BlueTusk Live, on the server
and in the browser client, with its type and default. For what the settings
mean together, read [Concepts](concepts.md) first. The
[full engineering reference](reference.md) has background detail.

Live has no `appsettings.json` section of its own. You set options in code when
you create each object. To drive them from configuration, read your own section
and pass the values in, as shown in [Aspire](#aspire).

## What you configure, and where

| Object | Package | Purpose |
| --- | --- | --- |
| [`PostgreSqlLiveStoreOptions`](#postgresqllivestoreoptions) | `BlueTusk.Live.DependencyInjection` | Invalidation log and replay window in PostgreSQL. |
| [`LiveEfQueryDefinition`](#query-registration) | `BlueTusk.Live.EntityFrameworkCore` | One registered EF Core query. |
| [`LiveQuerySessionOptions`](#subscriptions-and-limits) | `BlueTusk.Live` | First result and diff size. |
| [`LiveSharedSubscriptionOptions`](#subscriptions-and-limits) | `BlueTusk.Live` | Clients, queues and replay per subscription. |
| [`LiveSharedSubscriptionRegistryOptions`](#subscriptions-and-limits) | `BlueTusk.Live` | Number of shared subscriptions. |
| [`LiveResumeTokenProtector`, `LiveAspNetCoreOptions`](#resume-tokens-and-requests) | `BlueTusk.Live` / `BlueTusk.Live.AspNetCore` | Token signing, token lifetime, request size. |
| [Transport mapping](#transports) | `BlueTusk.Live.ServerSentEvents`, `.SignalR`, `.Grpc` | Endpoints and paths. |
| [`LiveClientOptions`](#browser-client-options) | `@bluetusk/live` | Browser connection, retries and batching. |
| [`BlueTuskLiveAspireOptions`](#aspire) | `BlueTusk.Live.Aspire` | Settings passed from an Aspire AppHost. |

## PostgreSqlLiveStoreOptions

`PostgreSqlLiveInvalidationStore` implements the invalidation log
(`ILiveInvalidationLog`), the sink that Streams writes to
(`ILiveInvalidationSink`) and the replay store (`ILiveReplayStore`). It creates
its tables on first use, or when you call `InitializeAsync`.

```csharp
var store = new PostgreSqlLiveInvalidationStore(new PostgreSqlLiveStoreOptions
{
    ControlDataSource = controlDataSource,
    ControlSchema = "bluetusk_live",
    ReplayRetentionWindow = TimeSpan.FromHours(1),
    MaximumReplayEventBytes = 4 * 1024 * 1024,
    ReplayPruneBatchSize = 1_000,
    MaximumDependenciesPerTransaction = 1_024,
    MaximumDependenciesPerQuery = 128,
});
```

| Option | Type | Default | Meaning |
| --- | --- | --- | --- |
| `ControlDataSource` | `DbDataSource` | required | Where the Live tables live. |
| `ControlSchema` | `string` | `"bluetusk_streams"` | Schema for the Live tables. At most 63 UTF-8 bytes. |
| `ReplayRetentionWindow` | `TimeSpan` | 1 hour | `PruneAsync` removes replay events older than this. |
| `MaximumReplayEventBytes` | `int` | 4 MiB | Largest single serialized event the store accepts. |
| `ReplayPruneBatchSize` | `int` | 1,000 | Events removed per `PruneAsync` call. |
| `MaximumDependenciesPerTransaction` | `int` | 1,024 | Distinct tables recorded for one committed transaction. |
| `MaximumDependenciesPerQuery` | `int` | 128 | Tables one query may depend on when checking for changes. |

The store never prunes by itself. Run `PruneAsync` on a schedule:

```csharp
public sealed class LiveReplayPruner(PostgreSqlLiveInvalidationStore store) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            while (await store.PruneAsync(stoppingToken) > 0)
            {
            }
        }
    }
}
```

If the control tables are in the same database as your data, do not add them
to the publication that feeds Live. Otherwise each invalidation write would
produce another change.

## Query registration

`LiveEfQueryDefinition<TContext, TEntity, TKey>` takes these constructor
arguments. See the [quick start](quickstart.md#3-write-the-server) for a
complete example.

| Argument | Meaning |
| --- | --- |
| `name` | The name clients send as `query`. Unique per `LiveQueryRegistry`. |
| `databaseIdentity` | A stable name for the database. Part of the subscription identity. |
| `version` | Change it when the query's meaning changes; it changes the plan fingerprint. |
| `parameters` | `LiveQueryParameter(name, type, allowNull = false)` values. Scalars only. |
| `validationArguments` | Sample values used to check and translate the query at startup. |
| `maximumResultCount` | Hard row limit. `Take` must be between 1 and this value. |
| `queryFactory` | `(context, arguments) => IQueryable<TEntity>` in the supported shape. |
| `keySelector` | The primary key property, for example `todo => todo.Id`. |
| `rowComparer` | Decides whether a row with the same key changed. |
| `tenantIsolationMode` | `RegisteredPredicate`, `EfGlobalQueryFilter` or `DatabaseRowLevelSecurity`. |
| `tenantBinding` | `LiveEfTenantBinding(entityProperty, parameterName)`; required for `RegisteredPredicate` only. |

Rows are sent to clients with default `System.Text.Json` settings, so property
names keep their C# spelling. Use `[JsonPropertyName]` on the row type if you
want other names.

## Subscriptions and limits

```csharp
var session = new LiveQuerySession<TRow, long>(
    plan,
    arguments,
    scope,
    invalidations,
    resultLimit: null, // null uses the plan's maximumResultCount
    options: new LiveQuerySessionOptions
    {
        MaximumInitialCatchUpPasses = 32,
        Diff = new LiveDiffOptions { MaximumEventsPerRefresh = 1_024 },
    });

var subscription = new LiveSharedSubscription<TRow, long>(
    session,
    replay,
    new LiveSharedSubscriptionOptions
    {
        MaximumSubscribers = 1_000,
        SubscriberBufferCapacity = 128,
        MaximumReplayEventsPerConnect = 1_024,
        SlowClientPolicy = LiveSlowClientPolicy.Disconnect,
    });

var registry = new LiveSharedSubscriptionRegistry(new LiveSharedSubscriptionRegistryOptions
{
    MaximumSharedSubscriptions = 10_000,
});
```

| Option | Type | Default | Meaning |
| --- | --- | --- | --- |
| `resultLimit` (session argument) | `int?` | plan limit | A lower row limit for this subscription. Part of its identity. |
| `LiveQuerySessionOptions.MaximumInitialCatchUpPasses` | `int` | 32 | Attempts to take a first result while the tables keep changing. |
| `LiveDiffOptions.MaximumEventsPerRefresh` | `int` | 1,024 | Larger diffs are sent as one `ResultReset`. |
| `LiveSharedSubscriptionOptions.MaximumSubscribers` | `int` | 1,000 | Connected clients per subscription. More get HTTP 429. |
| `LiveSharedSubscriptionOptions.SubscriberBufferCapacity` | `int` | 128 | Messages queued for one client before it counts as slow. |
| `LiveSharedSubscriptionOptions.MaximumReplayEventsPerConnect` | `int` | 1,024 | Most events replayed to one connecting client. More get HTTP 409. |
| `LiveSharedSubscriptionOptions.SlowClientPolicy` | `LiveSlowClientPolicy` | `Disconnect` | `Disconnect` ends the connection; `RequireReset` sends a reset message first. |
| `LiveSharedSubscriptionRegistryOptions.MaximumSharedSubscriptions` | `int` | 10,000 | Distinct subscriptions in one registry. |

## Resume tokens and requests

```csharp
builder.Services.AddBlueTuskLiveAspNetCore(
    new LiveResumeTokenProtector(
    [
        new LiveResumeTokenKey("2026-10", newKey, isPrimary: true),
        new LiveResumeTokenKey("2026-04", previousKey),
    ]),
    new LiveAspNetCoreOptions
    {
        ResumeTokenLifetime = TimeSpan.FromMinutes(30),
        MaximumRequestBytes = 64 * 1024,
    });
```

`LiveResumeTokenKey(keyId, secret, isPrimary = false)` rules:

- the secret must be at least 32 bytes, and the key ID at most 255 UTF-8 bytes;
- key IDs must be unique, and exactly one key must be primary;
- new tokens are signed with the primary key; every listed key can validate.

To rotate, add the new key as primary and keep the old key listed for at least
`ResumeTokenLifetime`. Load secrets from a secret store, not source code. Every
server instance must use the same keys.

| Option | Type | Default | Meaning |
| --- | --- | --- | --- |
| `LiveAspNetCoreOptions.ResumeTokenLifetime` | `TimeSpan` | 30 minutes | How long each issued token stays valid. |
| `LiveAspNetCoreOptions.MaximumRequestBytes` | `long` | 64 KiB | Largest subscription request body. Larger gets HTTP 413 (SSE) or `ResourceExhausted` (gRPC). |

## Transports

Each transport needs its ASP.NET Core services and the Live services above.
The SignalR hub and the gRPC service carry `[Authorize]`, so register
authentication and authorization.

```csharp
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();
builder.Services.AddSignalR();
builder.Services.AddGrpc();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapBlueTuskLiveServerSentEvents("/bluetusk/live/sse").RequireAuthorization();
app.MapBlueTuskLiveHub("/bluetusk/live");
app.MapBlueTuskLiveGrpc();
```

| Transport | Method | Default | Notes |
| --- | --- | --- | --- |
| Server-sent events | `MapBlueTuskLiveServerSentEvents(pattern)` | `/bluetusk/live/sse` | `POST` with JSON `{ "query", "parameters", "resumeToken" }`. Responds with `text/event-stream`, `Cache-Control: no-cache, no-store` and `X-Accel-Buffering: no`. Returns a `RouteHandlerBuilder`, so you can add `RequireAuthorization`, `RequireCors` or rate limiting. |
| SignalR | `MapBlueTuskLiveHub(pattern)` | `/bluetusk/live` | Streaming hub method `SubscribeAsync(LiveSubscriptionRequest)`. Errors arrive as `HubException`. |
| gRPC | `MapBlueTuskLiveGrpc()` | service `bluetusk.live.v1.BlueTuskLive` | Server-streaming `Subscribe`. Parameters travel as JSON in `parameters_json`. Needs HTTP/2. |

`parameters` must be a JSON object, even when the query has no parameters
(`{}`). HTTP status and gRPC status for each failure are listed in
[troubleshooting](troubleshooting.md#what-each-error-response-means).

## Browser client options

`new BlueTuskLiveClient(options)` from `@bluetusk/live`:

```typescript
import { BlueTuskLiveClient } from "@bluetusk/live";

const client = new BlueTuskLiveClient({
  endpoint: "/bluetusk/live/sse",           // required
  fetch: (input, init) => fetch(input, init),
  headers: { authorization: `Bearer ${accessToken}` },
  credentials: "same-origin",
  initialRetryDelayMs: 250,
  maximumRetryDelayMs: 15_000,
  retryJitter: 0.2,
  random: Math.random,
  onResumeToken: (token) => console.debug("resume token", token !== null),
  maximumBatchEvents: 64
});
```

| Option | Type | Default | Meaning |
| --- | --- | --- | --- |
| `endpoint` | `string` | required | URL of the SSE endpoint. |
| `fetch` | `typeof fetch` | `globalThis.fetch` | The fetch function. In browsers, pass `(input, init) => fetch(input, init)`; see [troubleshooting](troubleshooting.md#the-page-says-illegal-invocation). |
| `headers` | `Record<string, string>` | none | Extra request headers, for example `authorization`. |
| `credentials` | `RequestCredentials` | `"same-origin"` | Use `"include"` to send cookies to another origin. |
| `initialRetryDelayMs` | `number` | 250 | First reconnect delay. |
| `maximumRetryDelayMs` | `number` | 15,000 | Longest reconnect delay. The delay doubles per attempt up to this. |
| `retryJitter` | `number` | 0.2 | Random spread, from 0 to 1, applied to each delay. |
| `random` | `() => number` | `Math.random` | Random source for jitter; replace in tests. |
| `onResumeToken` | `(token: string \| null) => void` | none | Called after each applied batch with the latest token, or `null` when the token is discarded. |
| `maximumBatchEvents` | `number` | 64 | New in 1.1.0. Events reduced before one `rows` update. Integer from 1 to 1,024. |

`createQuery<TRow, TKey, TParameters>({ query, parameters, resumeToken? })`
creates a `LiveQuery`. Call `start()`, `subscribe(listener)` and `stop()`.
Leave `resumeToken` unset: a new query must start from a full result, and the
running query already keeps its token for reconnects.

The Vue and Svelte helpers accept a third argument,
`{ autoStart?: boolean }`, default `true`. See [framework guides](clients.md).

## Client query policies

If you enable [client queries](concepts.md#registered-queries),
`LiveClientQueryPolicy` sets their limits:

| Argument | Default |
| --- | --- |
| `allowSql` | `false` (only the structured LINQ document is accepted) |
| `maximumQueryBytes` | 32 KiB |
| `maximumParameters` | 64 |
| `maximumResultCount` | 1,000 |
| `maximumResultColumns` | 128 |
| `maximumResultBytes` | 8 MiB |
| `statementTimeout` | 5 seconds (at most 5 minutes) |
| `lockTimeout` | 1 second (at most `statementTimeout`) |

`securityMode` must include `DatabaseRowLevelSecurity`,
`DedicatedReadOnlyRole` or both. See the
[full reference](reference.md#capability-secured-client-queries).

## Aspire

In an Aspire AppHost, `WithBlueTuskLive` passes two connection strings and the
Live limits to your API project. The application database and the control
database must be separate Aspire resources.

```csharp
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres");
var appDatabase = postgres.AddDatabase("app");
var controlDatabase = postgres.AddDatabase("live-control");

builder.AddProject("api", "../Api/Api.csproj")
    .WithBlueTuskLive(
        appDatabase,
        controlDatabase,
        new BlueTuskLiveAspireOptions
        {
            ControlSchema = "bluetusk_live",
            ReplayRetention = TimeSpan.FromMinutes(30),
            Transports = BlueTuskLiveAspireTransports.ServerSentEvents,
        });

builder.Build().Run();
```

| `BlueTuskLiveAspireOptions` | Default | Environment variable in the API project |
| --- | --- | --- |
| (application database) | | `BLUETUSK_LIVE_SOURCE` |
| (control database) | | `BLUETUSK_LIVE_CONTROL` |
| `ControlSchema` | `"bluetusk_streams"` | `BlueTusk__Live__ControlSchema` |
| `MaximumSharedSubscriptions` | 10,000 | `BlueTusk__Live__MaximumSharedSubscriptions` |
| `MaximumSubscribersPerQuery` | 1,000 | `BlueTusk__Live__MaximumSubscribersPerQuery` |
| `SubscriberBufferCapacity` | 128 | `BlueTusk__Live__SubscriberBufferCapacity` |
| `MaximumReplayEventsPerConnect` | 1,024 | `BlueTusk__Live__MaximumReplayEventsPerConnect` |
| `ReplayRetention` | 30 minutes | `BlueTusk__Live__ReplayRetentionSeconds` |
| `Transports` | `All` | `BlueTusk__Live__Transports` |

The Live packages do not read these variables themselves. Read them in the API
project and pass them to the options:

```csharp
var live = builder.Configuration.GetSection("BlueTusk:Live");
var subscriptionOptions = new LiveSharedSubscriptionOptions
{
    MaximumSubscribers = live.GetValue("MaximumSubscribersPerQuery", 1_000),
    SubscriberBufferCapacity = live.GetValue("SubscriberBufferCapacity", 128),
    MaximumReplayEventsPerConnect = live.GetValue("MaximumReplayEventsPerConnect", 1_024),
};
var registry = new LiveSharedSubscriptionRegistry(new LiveSharedSubscriptionRegistryOptions
{
    MaximumSharedSubscriptions = live.GetValue("MaximumSharedSubscriptions", 10_000),
});
```

## Telemetry

Live publishes an `ActivitySource` and a `Meter`, both named `BlueTusk.Live`.
Subscribe with OpenTelemetry or any .NET metrics listener. Tags never contain
row values or parameter values.

| Instrument | Type | Tags |
| --- | --- | --- |
| `bluetusk.live.authoritative_query.duration` (s) | histogram | `bluetusk.live.query.name`, `bluetusk.live.outcome` |
| `bluetusk.live.authoritative_query.rows` | histogram | `bluetusk.live.query.name`, `bluetusk.live.outcome` |
| `bluetusk.live.refresh.duration` (s), `bluetusk.live.refresh.events` | histogram | `bluetusk.live.query.name`, `bluetusk.live.outcome` |
| `bluetusk.live.connections` | counter | `bluetusk.live.connection.outcome`: `connected`, `notstarted`, `quotaexceeded`, `replayunavailable`, `replaylimitexceeded` |
| `bluetusk.live.clients.active` | up-down counter | |
| `bluetusk.live.fanout.deliveries` | counter | |
| `bluetusk.live.replay.events`, `bluetusk.live.replay.bytes` | counter | `bluetusk.live.replay.operation` |
| `bluetusk.live.slow_client.disconnects` | counter | `bluetusk.live.slow_client.policy`: `disconnect`, `requirereset` |
| `bluetusk.live.resume.validations` | counter | `bluetusk.live.resume.outcome`: `valid`, `expired`, `invalidsignature`, `unknownkey`, `identitymismatch`, `malformed`, `unsupportedversion` |

Each shared subscription also exposes `Status` (`LiveSharedSubscriptionStatus`)
with subscriber counts, quota and resume rejections, and
`LastDisconnectCode` (`slow-client-disconnect` or `slow-client-reset`).
