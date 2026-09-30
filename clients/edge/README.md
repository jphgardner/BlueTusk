# @bluetusk/edge

`0.1.0-preview.1` provides an IndexedDB offline cache, durable mutation queue, bearer-authenticated HTTP transport and bounded reconnect helper. It adds stable write identity, explicit revision conflicts and local transactional persistence to the BlueTusk ecosystem.

```typescript
import { IndexedDbEdgeStore } from "@bluetusk/edge";
const local = await IndexedDbEdgeStore.open({ databaseName: "warehouse-offline" });
const scope = { tenant: "customer-a", id: "warehouse-readers", epoch: "1" };
await local.activate(scope);
await local.beginSnapshot(scope, "server-snapshot-id", "42");
await local.applySnapshot(scope, "server-snapshot-id", [
  { id: "order-42", revision: "7", payload: '{"status":"pending"}', deleted: false }
]);
await local.commitSnapshot(scope, "server-snapshot-id");
await local.enqueue({ scope, id: crypto.randomUUID(), documentId: "order-42",
  expectedRevision: "7", kind: "upsert", payload: '{"status":"picked"}' });
const offline = await local.get(scope, "order-42");
```

The host supplies an authenticated authorization scope and durable idempotent remote write protocol. Epochs, revisions and checkpoint positions are decimal Int64 strings. Inputs and payloads are snapshotted, capacities are explicit, and no network operation occurs inside an IndexedDB transaction. Pages merge ordered cache and mutation cursors. Deletion/revision fences survive restart. Conflicts preserve local content until an explicit new-identity resolution. Active and staged snapshot records have independent default limits of 100000 records/256 MiB each, so a full-cache refresh can need roughly twice the payload storage temporarily; provision browser quota accordingly.

```typescript
import { EdgeHttpRemoteTransport, synchronizeEdge } from "@bluetusk/edge";
const remote = new EdgeHttpRemoteTransport({ endpoint: "https://api.example.com/edge",
  bearerToken: () => credentials.currentAccessToken() });
await synchronizeEdge(local, remote, scope);
```

The transport interoperates with `BlueTusk.Edge.AspNetCore` and its atomic PostgreSQL mutation inbox/snapshot/feed. Requests omit cookies. The host supplies trusted endpoint/token validation, CORS, authorization and scope/epoch negotiation; the browser cannot grant itself a tenant or scope. UUID mutation identities, decimal Int64 strings and base64 raw payload bytes preserve .NET/JavaScript replay identity. Each HTTP body/record/batch has explicit limits. Serialize reconnect passes per local scope. HTTP 410 requires an explicit fresh-snapshot/pending-write policy; conflicts require `resolveConflict` with a new UUID and current authoritative revision.

`local.enqueueOrdered({ scope, documentId, expectedRevision, kind, payload })` atomically allocates a stable UUIDv8 identity with its queued write. The store durably stages the original mutation for confirmation when its outcome is acknowledged; `synchronizeEdge` flushes confirmations and the server horizon before and after pushing. `flushOrderedReceipts` is available for a bounded standalone pass. A lost response resumes from the durable outbox after browser restart. This path is opt-in; `enqueue` with random UUIDs retains permanent server retry fences. Explicit conflict replacement still uses a caller-selected ID; see the [protocol contract](../../docs/edge/README.md) for that limit and epoch policy.

Build and contract tests run with `npm run build` and `npm test`. `npm run test:browser` additionally verifies actual headless browser IndexedDB across a browser restart in its own temporary profile; Windows defaults to installed Microsoft Edge, configurable through `BLUETUSK_EDGE_BROWSER_CHANNEL`. Contract tests use fake-indexeddb and are not a substitute for browser durability evidence.

The `tests/BlueTusk.Edge.BrowserHttpSmoke` executable starts a disposable real PostgreSQL/Kestrel server and runs `test:http-browser` against it: commit/disconnect, offline cached reads, persistent-profile browser restart, original UUID deduplication, bearer/tenant denial, conflict resolution and deletion. The synthetic authentication in that fixture is test-only. It requires a built client, installed Playwright browser and `BLUETUSK_TEST_CONNECTION_STRING`.

See [Edge operations and qualification](../../docs/edge/README.md) for the complete contract, bounds, verified recovery and outstanding projection integration, platform coverage and production qualification.
