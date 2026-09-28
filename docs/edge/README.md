# BlueTusk.Edge

BlueTusk.Edge `0.1.0-preview.1` supplies offline synchronization contracts and a bounded coordinator. BlueTusk.Edge.Sqlite provides a durable file cache and queued mutations. BlueTusk.Edge.Server supplies a PostgreSQL record repository with an atomic mutation inbox, consistent snapshots and a retained change feed. BlueTusk.Edge.Http and BlueTusk.Edge.AspNetCore connect that repository through authenticated HTTP. `@bluetusk/edge` supplies durable IndexedDB storage, an interoperable HTTP client and a bounded reconnect helper.

```csharp
var local = new SqliteEdgeStore(new SqliteEdgeOptions { DatabasePath = "cache/warehouse.db" });
await local.InitializeAsync();
var scope = new EdgeScope("customer-a", "warehouse-readers", epoch: 1);
await local.ActivateScopeAsync(scope);

// Initialize from an authenticated, consistent server snapshot.
var snapshot = new EdgeSnapshot(Guid.NewGuid(), position: 42);
await local.BeginSnapshotAsync(scope, snapshot);
await local.ApplySnapshotBatchAsync(scope, snapshot.Id,
    [new EdgeRecord("order-42", revision: 7, "{\"status\":\"pending\"}"u8.ToArray())]);
await local.CommitSnapshotAsync(scope, snapshot.Id);

await local.EnqueueAsync(new EdgeMutation(scope, Guid.NewGuid(), "order-42", 7,
    EdgeMutationKind.Upsert, "{\"status\":\"picked\"}"u8.ToArray()));
var offline = await local.GetAsync(scope, "order-42"); // Includes the pending local value.
```

The host obtains tenant, selective scope identity and increasing epoch from an authenticated server contract. A scope may represent a user, permission set and selection filter; changing any access boundary should rotate its epoch. The client cannot authorize its own arbitrary scope ID or epoch. Local persistence is not an authentication boundary or encryption mechanism; the host owns local file/browser profile access and logout/revocation handling.

## Cache, snapshots and changes

SQLite keys include tenant, scope and epoch. IndexedDB uses the same composite identity. Activating a newer epoch atomically purges prior cached/staged state and receipts, resets its checkpoint, and invalidates older-epoch reads. Pending writes make rotation fail by default. The host must explicitly choose `DiscardPending`/`discard` after its pending-write policy is resolved; the library does not silently discard offline user changes. A host detecting authorization revocation must stop using the old scope while resolving that policy.

Snapshots use stable identities, stage bounded batches separately from the active cache, and publish their full selective record set plus checkpoint in one transaction. Reopening/restarting does not expose partial snapshots, and repeating the same begin preserves staged batches. Completing a new snapshot removes records outside its authorized selection. The cache budget includes active plus staged rows, so reserve capacity for cutover. Hosts must drain pushes before obtaining a refresh snapshot and guarantee that all subsequent authorized changes remain available after its position. The store rejects change/ack interleaving while a snapshot is being staged.

Change batches must start at exactly the committed checkpoint and end at a strictly later position. Every record has a positive revision. Higher revisions replace prior values; equal identical records are idempotent, equal conflicting content fails the whole batch, and lower revisions cannot resurrect tombstones. Records and the new checkpoint commit together. Tombstones count against bounded cache cardinality and are not automatically discarded. Expiring a revision/deletion fence requires a server-proven replay floor or a fresh authorized snapshot.

Offline pages use bounded keyset reads. SQLite applies a cumulative payload byte budget in SQL. The browser merges ordered cache/queue cursors instead of loading the whole scope into page memory. Local deletions disappear from pages, while a targeted `GetAsync`/`get` exposes deletion state. Pending mutations overlay authoritative content; the returned server revision remains available to explain a conflict.

## Durable mutations and conflicts

Each queued write has a caller-stable UUID, document key, expected server revision, operation kind and payload fingerprint. Expected revision zero means an absent local record; other revisions must match cached state before enqueue. One pending/leased/conflicted operation per document avoids pretending that later offline writes know the result of an earlier unacknowledged write. Repeating an identical identity is idempotent; reusing it for another operation raises an identity error.

Claims persist a lease deadline and strictly advancing fence. Multiple instances cannot own the same current lease. After restart/expiration, a new claimant reuses the same mutation identity with a higher fence. A stale or expired owner cannot update the cache, queue or receipts. Deadlines use the host/device clock; clock-movement and mobile suspend behavior require further qualification.

An acknowledgement atomically updates the authoritative cache, retires or marks the queue entry, and records the identity/outcome receipt. Failure anywhere rolls all three back. A duplicate matching acknowledgement is idempotent. Successful outcomes must carry a newer authoritative revision and matching upsert/delete state. A newer cached record is never overwritten by an older acknowledgement.

Conflicts preserve the user's queued payload as an explicit overlay while exposing the current authoritative server revision. They are not silently merged or retried. `ResolveConflictAsync`/`resolveConflict` accepts a caller-selected merged payload, current expected revision and new stable identity, atomically retires the conflict and queues that replacement. Replaying the same resolution identity is idempotent. Age/count-bounded receipt cleanup excludes unresolved active mutation identities; the application must honor the server's durable deduplication/replay horizon when pruning receipts.

`EdgeSynchronizationCoordinator` and browser `synchronizeEdge` obtain an initial snapshot, push bounded claims and pull bounded changes. A transport failure leaves the persisted lease for recovery. Hosts serialize reconnect passes for a local scope and explicitly handle expired replay, epoch changes and conflicts. They do not automatically drop queued user changes in response to HTTP 410.

## Durable server and authenticated transport

`PostgreSqlEdgeServerStore` accepts a provider-neutral `DbDataSource` with explicit borrowed/owned lifetime. The core has no Npgsql or EF dependency. Initialize its dedicated schema and activate a trusted `EdgeScope` before exposing endpoints. Activation is an administrative host operation; the HTTP client cannot create arbitrary authorized scopes. Store tenant/scope/epoch identity represents an already selected record set, such as a warehouse's orders. The host owns selection, scope negotiation and permission-epoch rotation.

```csharp
await using var server = new PostgreSqlEdgeServerStore(dataSource,
    new EdgeServerOptions { Schema = "warehouse_edge" });
await server.InitializeAsync();
await server.ActivateScopeAsync(new EdgeScope("customer-a", "warehouse-readers", 1));

// Register authentication and an authenticated authorization policy before mapping.
app.MapBlueTuskEdge(server, (context, requestedScope, cancellationToken) =>
    accessPolicy.AuthorizeAsync(context.User, requestedScope, cancellationToken),
    new EdgeEndpointOptions { AuthorizationPolicy = "warehouse-edge" });
```

Every mutation locks the scope's state row, looks up its durable UUID receipt before checking the current record, and compares the expected revision. An applied write commits the record, a strictly ordered feed position, capacity counters and its receipt in the same owned database transaction. A conflict commits its authoritative outcome receipt without changing the business record or feed. A repeated identical UUID returns its original outcome; changed input for the same UUID fails. An injected receipt-insert failure rolls back the entire write.

`ApplyMutationWithBusinessAsync` and the host's optional `EdgeEndpointOptions.WriteBusinessAsync` execute application-table writes in that same owned transaction, only for the first applied mutation. The callback receives the original mutation, authoritative Edge record, exact `DbConnection`/`DbTransaction` and cancellation token. Receipt replays and conflicts do not invoke it. The host owns business validation and mapping; rejection or a failed application-table write rolls back record, feed, receipt, counters and application effects together. The callback must use the supplied transaction, must not commit/dispose it, and must not perform external I/O. Unrelated databases, external services and independently committed Documents sessions are outside this boundary.

Revisions come from a global noncycling sequence, preserving deletion/recreation fences. Scope writes serialize commit order to avoid a reader skipping an earlier uncommitted feed position; different scopes have independent state locks. Cardinality/byte counters make capacity admission constant work instead of scanning the full record/feed tables. Read operations use shared scope locks. Throughput, lock contention and storage qualification still need representative benchmarks.

A snapshot copies the authorized scope's current live records and exact feed head in one transaction. Its durable UUID pages are ordered by document key with cumulative byte limits and survive server-process restart until their database-clock lifetime expires. Tombstones are omitted from a fresh snapshot. Default limits permit four snapshots per scope with a two-minute lifetime; these materialized copies can add substantial storage. Release snapshots after consuming them. Expired copies are removed when a new snapshot is admitted.

Changes resume from an exact position, return bounded ordered records, and commit on the client together with the new checkpoint. `PruneChangesAsync` removes only a host-selected feed prefix and advances its replay floor atomically. An older client receives explicit replay-expired HTTP 410 and must obtain a fresh authorized snapshot under its pending-write policy. Record tombstones and mutation receipts are retained instead of silently removing their fences; capacity exhaustion returns an explicit failure. Receipt archival/expiry requires a negotiated deduplication horizon and remains an operational extension.

The ASP.NET endpoints require a named authenticated authorization policy and a per-request scope callback. Both tenant and scope/epoch must be authorized from the principal. The browser client obtains a bearer token for each request, sends `credentials: "omit"`, and never sends authentication cookies. Cookie-authenticated hosts must provide a separate CSRF policy. CORS, token validation/renewal, rate limits, logout/revocation and tenant mapping belong to the host. No administrative activation, pruning or arbitrary SQL route is exposed.

Wire codecs use source-generated JSON. Epochs, revisions and positions are canonical decimal Int64 strings; mutation/snapshot identities are UUIDs. Base64 payloads preserve the original UTF-8 JSON bytes, including whitespace and Unicode, so a replay has the same input fingerprint across .NET and JavaScript. Standalone browser storage accepts other bounded stable IDs, but HTTP mutations require UUIDs. Request/streamed-response, decoded record and batch limits are enforced independently; byte budgets include base64 expansion and per-record envelope overhead. A host's credential callback must itself finish promptly.

```typescript
const remote = new EdgeHttpRemoteTransport({ endpoint: "https://api.example.com/edge",
  bearerToken: () => credentials.currentAccessToken() });
await synchronizeEdge(local, remote, scope);
```

## Operator health and telemetry

`PostgreSqlEdgeServerStore.ReadHealthAsync` reads committed scope counters and database time under a shared scope lock. It validates the installed storage version/record-byte contract and reports replay head/floor, retained record/receipt/feed counts and bytes, active snapshots and capacity flags. It fetches no document or mutation payload. Snapshot observation reads at most `MaxSnapshotsPerScope + 1` rows; the extra row is an over-capacity sentinel if other hosts use different limits. Expired snapshots are excluded without deleting them. Other counters are the exact values maintained atomically with mutations/pruning. The probe does not repair state or prove physical PostgreSQL disk/WAL health.

`BlueTusk.Edge.AspNetCore` registers a fixed host-selected scope with the standard ASP.NET health system:

```csharp
builder.Services.AddHealthChecks().AddBlueTuskEdgeServer(
    "warehouse-edge-ready", server, operatorScope,
    new EdgeServerHealthCheckOptions { Timeout = TimeSpan.FromSeconds(2) });
// Configure the operators authorization policy before mapping this host-owned endpoint.
app.MapHealthChecks("/ops/edge", new HealthCheckOptions
{
    ResultStatusCodes = { [HealthStatus.Degraded] = 503 },
}).RequireAuthorization("operators");
```

The adapter does not map a route or accept a client-selected tenant/scope. The host owns authorization, safe response writing and the store lifetime. Its default is one nonqueued probe with a two-second deadline; concurrency is at most 64 and the deadline at most ten seconds. A saturated probe is degraded. Storage/scope/version failure or timeout is unhealthy, with fixed diagnostic codes and no attached exception or error payload. Record/feed/receipt or snapshot capacity is degraded: existing reads and some mutations can still succeed. ASP.NET normally returns HTTP 200 for degraded health; the example selects HTTP 503 for a readiness endpoint. Caller cancellation propagates. If a data source ignores cancellation, its slot remains reserved until late completion, and late faults are observed without logging messages.

The `BlueTusk.Edge.Server.Health` meter emits `bluetusk.edge.health.probes` and `bluetusk.edge.health.duration` in seconds. The only tag is a fixed `status` (`healthy`, `degraded`, `unhealthy`, `cancelled`); it includes no tenant, scope, IDs, SQL, parameters, connection strings, payloads or provider exception messages. Duration measures the reported probe result, not the eventual completion of a timed-out uncooperative driver. Hosts connect their chosen metrics exporter and operator alert policy. This covers server readiness/capacity probes; browser/SQLite disk health and mutation/synchronization throughput telemetry still need integrations.

## Storage and bounds

SQLite initialization enables WAL and `synchronous=FULL`, uses immediate write transactions and a bounded busy timeout, and provisions durable schema version 2. Version 1 is the same cache/queue format before receipt storage; its upgrade preserves cache, checkpoints and queued writes. Unknown future versions are rejected. Microsoft.Data.Sqlite operations may execute synchronously underneath their async signatures; isolate substantial local I/O from a UI thread. Each operation opens and closes its own connection with pooling disabled, avoiding accidental retained transactions or advisory state. Optimizing that connection strategy needs workload evidence.

Defaults are 256 scopes, 100000 cache records/256 MiB payload, 10000 pending mutations/32 MiB payload, 100000 receipts, 512 records/8 MiB per incoming batch, 512 KiB per record and 1000 records/8 MiB per page. Cardinality also bounds key/metadata overhead. Capacity errors roll back the complete proposed state transition; no silent LRU eviction can destroy a revision fence or queued write. SQLite file/WAL physical size, disk pressure and checkpoint policy need operational qualification beyond logical byte budgets.

The browser requests strict IndexedDB transaction durability and keeps schema upgrades nondestructive. Browser epochs, revisions and checkpoints are decimal Int64 strings; numbers above JavaScript's safe integer range are rejected instead of rounded. Wire transport must encode these fields as strings too. Browser quotas/eviction, private modes and cross-browser durability are host/platform concerns requiring targeted testing; IndexedDB transaction completion is the adapter's acknowledgement boundary.

## Verification and remaining product gates

```powershell
dotnet test tests/BlueTusk.Edge.Tests/BlueTusk.Edge.Tests.csproj -c Release -nr:false
npm run build --prefix clients/edge --workspaces=false
npm test --prefix clients/edge --workspaces=false
npm run test:browser --prefix clients/edge --workspaces=false
dotnet build tests/BlueTusk.Edge.BrowserHttpSmoke/BlueTusk.Edge.BrowserHttpSmoke.csproj -c Release -nr:false
dotnet run --project tests/BlueTusk.Edge.BrowserHttpSmoke/BlueTusk.Edge.BrowserHttpSmoke.csproj -c Release --no-build
```

Set `BLUETUSK_TEST_CONNECTION_STRING` to a disposable PostgreSQL database for server tests and the browser HTTP host. The .NET suite exercises real SQLite files, reopen recovery, concurrent claims, higher-fence rejection, duplicate identities/acknowledgements, explicit conflict resolution, scoped epoch rotation, snapshots/checkpoints/tombstones, capacity rollback, schema upgrade and cancellation; actual PostgreSQL competing CAS, snapshot/feed boundaries, injected receipt-trigger and application-table callback rollback, replay-floor expiry and bounded materialized snapshots; and actual authenticated Kestrel HTTP commit/disconnect, server restart, SQLite reopen, explicit conflicts and deletion. Bounded local and server workloads each commit 128 1 KiB writes through four writers; the server feed has one contiguous position per applied effect. The lost-response/server-restart test checks that a separate application business counter advances once, and replay/conflict tests ensure the host callback is skipped.

The latest .NET suite passes 33 tests with no skips. Six operator-health cases verify committed counters across replay/conflict/deletion/pruning, tenant/epoch isolation, bounded snapshot observation, schema drift, fixed telemetry tags, real database-lock timeout/cancellation/recovery, retained admission for an uncooperative connection open, and an actual authenticated ASP.NET readiness endpoint. The real browser HTTP smoke additionally checks exactly four separate application-table effects after reconnect/retry/conflict/deletion; the extended native executable checks exactly two after receipt replay and conflicts. These use synthetic test authentication and a deliberately isolated business counter, not a production domain schema.

Run the browser HTTP executable from the workspace root, or pass that absolute workspace path after `--`. It validates the Node script location, binds its Kestrel listener to an ephemeral loopback port, creates its own unique database schema, owns the child Node/browser process with a two-minute deadline, and drops only its schema on exit. The Node script owns a separate loopback asset server and verified temporary browser profile. CI must provide the disposable database connection and an installed Node/.NET 10 runtime, install the workspace's locked npm dependencies, build `@bluetusk/edge`, install the matching Playwright browser, and explicitly set `BLUETUSK_EDGE_BROWSER_CHANNEL` (for example `chromium` on Linux or `msedge` when installed on Windows). The host supplies its endpoint to the child through `BLUETUSK_EDGE_HTTP_ENDPOINT`; no external service endpoint is required.

Fourteen JavaScript contract tests cover IndexedDB deterministic races/fault conditions and wire/authorization/streamed byte bounds. A separate actual headless Microsoft Edge test persists a dedicated temporary browser profile, closes and relaunches it, and verifies real IndexedDB snapshot/rollback, Int64 precision, recovered higher lease fence, duplicate atomic acknowledgement and epoch isolation. `BlueTusk.Edge.BrowserHttpSmoke` additionally starts a disposable PostgreSQL/Kestrel host and drives real browser HTTP: the server commits a write but drops its response (including browser transparent retries), offline reads retain the queued value, and a browser restart recovers the original UUID with one business effect. It checks bearer 401, tenant 403, raw Unicode/whitespace identity, conflict preservation/resolution, deletion and exact final feed position four, using a configured eight-record HTTP batch bound. `BLUETUSK_EDGE_BROWSER_CHANNEL` chooses an installed Playwright channel; Windows defaults to `msedge`, other platforms to `chromium`. Tests use their own temporary profiles. Test-only synthetic bearer validation/CORS/drop middleware is never a product authentication recommendation.

All five Edge packages enable NativeAOT/trimming analyzers. The combined `tests/BlueTusk.Edge.NativeAotSmoke` was published as win-x64 NativeAOT without warnings and executed with real PostgreSQL/pgvector fixtures plus a real SQLite file. Its Edge checks reopen the durable queue and duplicate acknowledgement; it also starts actual authenticated native Kestrel/ASP.NET endpoints and exercises PostgreSQL server snapshots, source-generated HTTP codecs, queue push/change feed, UUID receipt replay, CAS conflict and bearer/tenant denial. It checks exact operator health counters, authenticated native readiness, health-route 401, and HTTP 503 after injected storage-format drift. The server commits exactly two applied business effects. Set both `BLUETUSK_TEST_CONNECTION_STRING` and `BLUETUSK_SEARCH_VECTOR_CONNECTION_STRING`; the executable also checks Documents and Search. Other architectures and complete API paths remain unqualified.

Still required: Streams/Live or Documents projection adapters; authenticated scope negotiation; durable receipt archival with a negotiated replay horizon; browser worker/service-worker orchestration and managed conflict UI; offline indexed query extensions; disk/quota pressure, forced process death, clock/suspend, mobile and multi-browser recovery tests; additional native architectures/API paths; repeatable latency/allocation/storage workload reports and long endurance. Current code and short workloads do not establish production qualification or universal performance leadership.
