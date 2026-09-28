import "fake-indexeddb/auto";
import assert from "node:assert/strict";
import test from "node:test";
import { IndexedDbEdgeStore, EdgeHttpRemoteTransport, EdgeHttpError, EdgeScopeError, EdgeCapacityError, EdgeRevisionError, EdgeIdentityError, EdgeLeaseError, newOrderedStreamId, orderedMutationId, parseOrderedMutationId, flushOrderedReceipts } from "../dist/index.js";

const scope = { tenant: "tenant", id: "readers", epoch: "1" };
const record = (id, revision, value) => ({ id, revision, payload: JSON.stringify({ value }), deleted: false });
const mutation = (documentId, expectedRevision, value) => ({ scope, id: crypto.randomUUID(), documentId, expectedRevision, kind: "upsert", payload: JSON.stringify({ value }) });
const req = request => new Promise((resolve, reject) => { request.onsuccess = () => resolve(request.result); request.onerror = () => reject(request.error) });
async function ready(store, current = scope) {
  await store.activate(current);
  const id = crypto.randomUUID(); await store.beginSnapshot(current, id, "0"); await store.commitSnapshot(current, id);
}
async function fixture(action, limits = {}) {
  const databaseName = `edge-${crypto.randomUUID()}`;
  let now = 1_800_000_000_000;
  const options = { databaseName, now: () => now, ...limits };
  const store = await IndexedDbEdgeStore.open(options);
  try { await action(store, options, delta => now += delta) }
  finally { store.close(); await req(indexedDB.deleteDatabase(databaseName)) }
}

test("staged snapshots are invisible, resume after reopen, and atomically replace the scope", async () => {
  await fixture(async (store, options) => {
    await store.activate(scope);
    const snapshot = crypto.randomUUID(); await store.beginSnapshot(scope, snapshot, "10");
    await store.applySnapshot(scope, snapshot, [record("1", "1", "old")]);
    assert.equal(await store.get(scope, "1"), null);
    store.close(); const reopened = await IndexedDbEdgeStore.open(options);
    try {
      await reopened.beginSnapshot(scope, snapshot, "10");
      await reopened.applySnapshot(scope, snapshot, [record("2", "2", "other")]);
      await reopened.commitSnapshot(scope, snapshot);
      assert.deepEqual(await reopened.checkpoint(scope), { position: "10", ready: true });
      assert.equal(JSON.parse((await reopened.get(scope, "1")).payload).value, "old");
      const refresh = crypto.randomUUID(); await reopened.beginSnapshot(scope, refresh, "20");
      await reopened.applySnapshot(scope, refresh, [record("1", "3", "new")]);
      assert.equal(JSON.parse((await reopened.get(scope, "1")).payload).value, "old");
      await reopened.commitSnapshot(scope, refresh);
      assert.equal(await reopened.get(scope, "2"), null);
      assert.equal(JSON.parse((await reopened.get(scope, "1")).payload).value, "new");
    } finally { reopened.close() }
  });
});

test("replay-expired full cache resnapshots atomically while preserving a queued write", async () => {
  await fixture(async store => {
    await ready(store);
    await store.applyChanges(scope, "0", "2", [record("1", "1", "old"), record("2", "1", "old")]);
    const queued = mutation("2", "1", "offline");
    await store.enqueue(queued);
    const expired = new EdgeHttpRemoteTransport({ endpoint: "https://example.test/edge", bearerToken: () => "token",
      fetch: async () => new Response(null, { status: 410 }) });
    await assert.rejects(() => expired.readChanges(scope, "2"), error => error instanceof EdgeHttpError && error.status === 410);
    const snapshot = crypto.randomUUID();
    await store.beginSnapshot(scope, snapshot, "3");
    await assert.rejects(store.applySnapshot(scope, snapshot, [record("1", "2", "new"), record("2", "1", "old"), record("3", "1", "excess")]), EdgeCapacityError);
    await store.applySnapshot(scope, snapshot, [record("1", "2", "new"), record("2", "1", "old")]);
    assert.equal(JSON.parse((await store.get(scope, "1")).payload).value, "old");
    assert.equal((await store.checkpoint(scope)).position, "2");
    await store.commitSnapshot(scope, snapshot);
    assert.equal(JSON.parse((await store.get(scope, "1")).payload).value, "new");
    const overlay = await store.get(scope, "2");
    assert.equal(overlay.pendingId, queued.id);
    assert.equal(JSON.parse(overlay.payload).value, "offline");
    assert.deepEqual(await store.checkpoint(scope), { position: "3", ready: true });
    assert.equal((await store.claim(scope)).mutation.id, queued.id);
  }, { maxCacheRecords: 2, maxStagedRecords: 2 });
});

test("HTTP receipt confirmation sends the original mutation and accepts idempotent no-content responses", async () => {
  const write = mutation("confirmed", "0", "value");
  const requests = [];
  const remote = new EdgeHttpRemoteTransport({ endpoint: "https://example.test/edge", bearerToken: () => "token",
    fetch: async (url, options) => { requests.push({ url, options }); return new Response(null, { status: 204 }) } });
  await remote.finalizeMutationReceipt(write);
  await remote.finalizeMutationReceipt(write);
  assert.equal(requests.length, 2);
  assert.equal(requests[0].url, "https://example.test/edge/mutations/confirm?tenant=tenant&scope=readers&epoch=1");
  assert.equal(requests[0].options.method, "POST");
  assert.equal(requests[0].options.credentials, "omit");
  assert.deepEqual(JSON.parse(requests[0].options.body), {
    id: write.id, documentId: write.documentId, expectedRevision: "0", kind: "upsert",
    payload: btoa(write.payload)
  });
  const gone = new EdgeHttpRemoteTransport({ endpoint: "https://example.test/edge", bearerToken: () => "token",
    fetch: async () => new Response(null, { status: 410 }) });
  await assert.rejects(() => gone.applyMutation(write), error => error instanceof EdgeHttpError && error.status === 410);
});

test("ordered identity and HTTP horizon use a bounded, retryable stream prefix", async () => {
  const streamId = newOrderedStreamId();
  const first = orderedMutationId(streamId, "1");
  assert.deepEqual(parseOrderedMutationId(first), { streamId, sequence: "1" });
  assert.equal(parseOrderedMutationId(orderedMutationId(streamId, "1152921504606846975")).sequence, "1152921504606846975");
  assert.throws(() => orderedMutationId(streamId, "1152921504606846976"), RangeError);
  const requests = [];
  const remote = new EdgeHttpRemoteTransport({ endpoint: "https://example.test/edge", bearerToken: () => "token",
    fetch: async (url, options) => { requests.push({ url, options }); return new Response(null, { status: 204 }) } });
  await remote.advanceOrderedReceiptHorizon(scope, first, 1);
  assert.equal(requests[0].url, "https://example.test/edge/mutations/horizon?tenant=tenant&scope=readers&epoch=1");
  assert.deepEqual(JSON.parse(requests[0].options.body), { throughId: first, maxReceipts: 1 });
  await assert.rejects(remote.advanceOrderedReceiptHorizon(scope, crypto.randomUUID()), TypeError);
});

test("ordered allocation, claim order and confirmation outbox survive IndexedDB reopen", async () => {
  await fixture(async (store, options) => {
    await ready(store);
    const other = await IndexedDbEdgeStore.open(options);
    const writes = await Promise.all(Array.from({ length: 8 }, (_, index) => (index % 2 ? store : other).enqueueOrdered({
      scope, documentId: String(index).padStart(3, "0"), expectedRevision: "0", kind: "upsert", payload: JSON.stringify({ index })
    })));
    const parsed = writes.map(write => parseOrderedMutationId(write.id));
    assert.equal(new Set(parsed.map(value => value.streamId)).size, 1);
    assert.deepEqual(parsed.map(value => value.sequence).sort((a, b) => Number(a) - Number(b)), Array.from({ length: 8 }, (_, index) => String(index + 1)));
    const lease = await store.claim(scope);
    assert.equal(parseOrderedMutationId(lease.mutation.id).sequence, "1");
    assert.equal(await other.claim(scope), null);
    await store.acknowledge(lease, { kind: "applied", record: record(lease.mutation.documentId, "1", "server") });
    store.close(); other.close();
    const reopened = await IndexedDbEdgeStore.open(options);
    try {
      const confirmation = await reopened.nextUnconfirmedOrderedReceipt(scope);
      assert.equal(confirmation.id, lease.mutation.id);
      await reopened.markOrderedReceiptConfirmed(confirmation);
      assert.equal(await reopened.nextUnconfirmedOrderedReceipt(scope), null);
      assert.equal(await reopened.confirmedOrderedHorizon(scope), lease.mutation.id);
      reopened.close();
      const again = await IndexedDbEdgeStore.open(options);
      try {
        await again.markOrderedHorizon(scope, lease.mutation.id);
        await again.markOrderedHorizon(scope, lease.mutation.id);
        assert.equal(await again.confirmedOrderedHorizon(scope), null);
        assert.equal(parseOrderedMutationId((await again.claim(scope)).mutation.id).sequence, "2");
      } finally { again.close() }
    } finally { reopened.close() }
  });
});

test("ordered confirmation retry resumes at the horizon after a lost response and restart", async () => {
  await fixture(async (store, options) => {
    await ready(store);
    const write = await store.enqueueOrdered({ scope, documentId: "resume", expectedRevision: "0", kind: "upsert", payload: "{}" });
    await store.acknowledge(await store.claim(scope), { kind: "applied", record: record("resume", "1", "server") });
    let confirms = 0; let horizons = 0;
    const remote = new EdgeHttpRemoteTransport({ endpoint: "https://example.test/edge", bearerToken: () => "token", fetch: async url => {
      if (url.includes("/confirm")) { confirms++; return new Response(null, { status: 204 }) }
      if (url.includes("/horizon")) { horizons++; if (horizons === 1) throw new Error("Lost horizon response."); return new Response(null, { status: 204 }) }
      throw new Error("Unexpected request.");
    } });
    await assert.rejects(flushOrderedReceipts(store, remote, scope), /Lost horizon response/);
    assert.equal(await store.nextUnconfirmedOrderedReceipt(scope), null);
    assert.equal(await store.confirmedOrderedHorizon(scope), write.id);
    store.close(); const reopened = await IndexedDbEdgeStore.open(options);
    try {
      await flushOrderedReceipts(reopened, remote, scope);
      assert.equal(await reopened.confirmedOrderedHorizon(scope), null);
      assert.equal(confirms, 1); assert.equal(horizons, 2);
    } finally { reopened.close() }
  });
});

test("ordered horizon reclaims IndexedDB receipt capacity without reusing a sequence", async () => {
  await fixture(async store => {
    await ready(store);
    const first = await store.enqueueOrdered({ scope, documentId: "first", expectedRevision: "0", kind: "upsert", payload: "{}" });
    await store.acknowledge(await store.claim(scope), { kind: "applied", record: record("first", "1", "server") });
    await store.enqueueOrdered({ scope, documentId: "second", expectedRevision: "0", kind: "upsert", payload: "{}" });
    const second = await store.claim(scope);
    await assert.rejects(store.acknowledge(second, { kind: "applied", record: record("second", "1", "server") }), EdgeCapacityError);
    await store.markOrderedReceiptConfirmed(first);
    await store.markOrderedHorizon(scope, first.id);
    await store.acknowledge(second, { kind: "applied", record: record("second", "1", "server") });
    assert.equal(parseOrderedMutationId(second.mutation.id).sequence, "2");
  }, { maxReceipts: 1 });
});

test("checkpoint and revision conflicts roll back whole batches and tombstones fence resurrection", async () => {
  await fixture(async store => {
    await ready(store); await store.applyChanges(scope, "0", "1", [record("1", "1", "one")]);
    await assert.rejects(store.applyChanges(scope, "1", "2", [record("2", "1", "rollback"), record("1", "1", "wrong")]), EdgeRevisionError);
    assert.equal(await store.get(scope, "2"), null); assert.equal((await store.checkpoint(scope)).position, "1");
    await assert.rejects(store.applyChanges(scope, "0", "3", []), EdgeRevisionError);
    await store.applyChanges(scope, "1", "2", [{ id: "1", revision: "2", payload: "", deleted: true }]);
    await store.applyChanges(scope, "2", "3", [record("1", "1", "stale")]);
    assert.equal((await store.get(scope, "1")).deleted, true); assert.deepEqual((await store.page(scope)).items, []);
  });
});

test("stable mutation queue and atomic acknowledgements survive reopen and duplicate delivery", async () => {
  await fixture(async (store, options) => {
    await ready(store); const write = mutation("1", "0", "offline");
    await store.enqueue(write); await store.enqueue(write);
    assert.equal((await store.get(scope, "1")).pendingId, write.id);
    store.close(); const reopened = await IndexedDbEdgeStore.open(options);
    try {
      const lease = await reopened.claim(scope); assert.equal(lease.mutation.id, write.id);
      const outcome = { kind: "applied", record: record("1", "1", "normalized") };
      await reopened.acknowledge(lease, outcome); await reopened.acknowledge(lease, outcome); await reopened.enqueue(write);
      assert.equal(await reopened.claim(scope), null);
      assert.equal((await reopened.get(scope, "1")).pendingId, null);
      assert.equal(JSON.parse((await reopened.get(scope, "1")).payload).value, "normalized");
      await assert.rejects(reopened.enqueue({ ...write, payload: "{}" }), EdgeIdentityError);
    } finally { reopened.close() }
  });
});

test("expired leases recover with a larger fence and two instances cannot claim one write", async () => {
  await fixture(async (store, options, advance) => {
    await ready(store); await store.enqueue(mutation("1", "0", "offline"));
    const second = await IndexedDbEdgeStore.open(options);
    try {
      const claims = await Promise.all([store.claim(scope, 1000), second.claim(scope, 1000)]);
      const first = claims.find(value => value !== null); assert.equal(claims.filter(value => value !== null).length, 1);
      advance(1001); const recovered = await second.claim(scope, 1000);
      assert.equal(recovered.mutation.id, first.mutation.id); assert.ok(recovered.fence > first.fence);
      const outcome = { kind: "applied", record: record("1", "1", "committed") };
      await assert.rejects(store.acknowledge(first, outcome), EdgeLeaseError);
      await second.acknowledge(recovered, outcome);
    } finally { second.close() }
  });
});

test("explicit conflicts preserve local content and resolve with a new stable identity", async () => {
  await fixture(async store => {
    await ready(store); await store.applyChanges(scope, "0", "1", [record("1", "1", "server")]);
    const local = mutation("1", "1", "local"); await store.enqueue(local);
    await store.acknowledge(await store.claim(scope), { kind: "conflict", record: record("1", "2", "competing") });
    const current = await store.get(scope, "1"); assert.equal(current.pendingStatus, "conflict"); assert.equal(current.revision, "2");
    assert.equal(JSON.parse(current.payload).value, "local"); assert.equal(await store.claim(scope), null);
    const merged = mutation("1", "2", "merged"); await store.resolveConflict(local.id, merged); await store.resolveConflict(local.id, merged);
    const lease = await store.claim(scope); assert.equal(lease.mutation.id, merged.id);
    await store.acknowledge(lease, { kind: "applied", record: record("1", "3", "merged") });
    assert.equal((await store.get(scope, "1")).pendingId, null);
  });
});

test("tenant isolation and explicit epoch rotation invalidate old caches and writes", async () => {
  await fixture(async store => {
    await ready(store); await store.applyChanges(scope, "0", "1", [record("1", "1", "secret")]);
    const other = { ...scope, tenant: "other" }; await ready(store, other); assert.equal(await store.get(other, "1"), null);
    await store.enqueue(mutation("1", "1", "pending")); const epoch = { ...scope, epoch: "2" };
    await assert.rejects(store.activate(epoch), EdgeRevisionError);
    await store.activate(epoch, "discard"); await assert.rejects(store.get(scope, "1"), EdgeScopeError);
    assert.equal(await store.get(epoch, "1"), null); assert.equal((await store.checkpoint(epoch)).ready, false);
  });
});

test("cache, queue and page budgets reject excess atomically", async () => {
  await fixture(async store => {
    await ready(store); await store.enqueue(mutation("pending", "0", "first"));
    await assert.rejects(store.enqueue(mutation("extra", "0", "second")), EdgeCapacityError);
    await assert.rejects(store.applyChanges(scope, "0", "1", [record("1", "1", "one"), record("2", "1", "two"), record("3", "1", "three")]), EdgeCapacityError);
    assert.equal((await store.checkpoint(scope)).position, "0"); assert.equal(await store.get(scope, "1"), null);
  }, { maxPendingRecords: 1, maxCacheRecords: 2 });
  await fixture(async store => {
    await ready(store); await store.applyChanges(scope, "0", "1", [record("1", "1", "x".repeat(80)), record("2", "1", "x".repeat(80))]);
    const first = await store.page(scope); assert.equal(first.items.length, 1); assert.equal(first.nextAfterId, "1");
    const second = await store.page(scope, 100, first.nextAfterId); assert.equal(second.items[0].id, "2"); assert.equal(second.nextAfterId, null);
  }, { maxRecordBytes: 128, maxPageBytes: 128 });
});

test("Int64 revisions and positions retain precision above JavaScript safe integers", async () => {
  await fixture(async store => {
    await ready(store); await store.applyChanges(scope, "0", "9007199254740999", [record("1", "9007199254740997", "large")]);
    assert.equal((await store.get(scope, "1")).revision, "9007199254740997");
    assert.equal((await store.checkpoint(scope)).position, "9007199254740999");
    await assert.rejects(store.applyChanges(scope, "9007199254740999", "9007199254741000", [{ id: "2", revision: 9007199254740997, payload: "{}", deleted: false }]), EdgeRevisionError);
  });
});

test("receipt capacity failure rolls back authoritative cache and queue acknowledgement", async () => {
  await fixture(async store => {
    await ready(store); await store.enqueue(mutation("1", "0", "first"));
    await store.acknowledge(await store.claim(scope), { kind: "applied", record: record("1", "1", "first") });
    await store.enqueue(mutation("2", "0", "pending")); const lease = await store.claim(scope);
    await assert.rejects(store.acknowledge(lease, { kind: "applied", record: record("2", "1", "committed") }), EdgeCapacityError);
    const current = await store.get(scope, "2"); assert.equal(current.revision, "0"); assert.equal(current.pendingId, lease.mutation.id);
    assert.equal(await store.pruneReceipts(Number.MAX_SAFE_INTEGER, 1), 1);
    await store.acknowledge(lease, { kind: "applied", record: record("2", "1", "committed") });
    assert.equal((await store.get(scope, "2")).pendingId, null);
  }, { maxReceipts: 1 });
});

test("bounded concurrent browser workload commits all queued writes without duplicate claims", async () => {
  await fixture(async (store, options) => {
    await ready(store); const second = await IndexedDbEdgeStore.open(options);
    try {
      await Promise.all(Array.from({ length: 64 }, (_, index) => (index % 2 ? store : second).enqueue(mutation(String(index).padStart(3, "0"), "0", "x".repeat(1024)))));
      const ids = new Set();
      while (true) {
        const leases = (await Promise.all([store.claim(scope), second.claim(scope)])).filter(Boolean);
        if (!leases.length) break;
        for (const lease of leases) { assert.ok(!ids.has(lease.mutation.id)); ids.add(lease.mutation.id); await store.acknowledge(lease, { kind: "applied", record: record(lease.mutation.documentId, "1", "committed") }) }
      }
      assert.equal(ids.size, 64); assert.equal((await store.page(scope)).items.length, 64);
    } finally { second.close() }
  });
});

test("version-one IndexedDB schema upgrades without dropping cached data", async () => {
  const databaseName = `edge-legacy-${crypto.randomUUID()}`;
  const opening = indexedDB.open(databaseName, 1);
  opening.onupgradeneeded = () => {
    const db = opening.result;
    db.createObjectStore("scopes", { keyPath: ["tenant", "id"] }).put({ ...scope, position: "12", ready: true, snapshotId: "legacy-snapshot", snapshotPosition: "13" });
    for (const name of ["records", "staging"]) {
      const store = db.createObjectStore(name, { keyPath: name === "records" ? ["scope.tenant", "scope.id", "scope.epoch", "id"] : ["scope.tenant", "scope.id", "scope.epoch", "snapshotId", "id"] });
      store.createIndex("scope", ["scope.tenant", "scope.id", "scope.epoch"]); store.createIndex("base", ["scope.tenant", "scope.id"]);
      if (name === "records") store.put({ scope, ...record("1", "5", "preserved"), fingerprint: "legacy" });
      else store.put({ scope, ...record("2", "6", "staged"), fingerprint: "legacy-staged", snapshotId: "legacy-snapshot" });
    }
    const pending = db.createObjectStore("mutations", { keyPath: ["scope.tenant", "scope.id", "scope.epoch", "id"] });
    pending.createIndex("scope", ["scope.tenant", "scope.id", "scope.epoch"]); pending.createIndex("base", ["scope.tenant", "scope.id"]);
    pending.createIndex("document", ["scope.tenant", "scope.id", "scope.epoch", "documentId"], { unique: true });
    pending.createIndex("claim", ["scope.tenant", "scope.id", "scope.epoch", "status", "sequence"]);
    pending.createIndex("expiry", ["scope.tenant", "scope.id", "scope.epoch", "status", "leaseUntil"]);
    const cacheBytes = Buffer.byteLength(record("1", "5", "preserved").payload) + 1 + Buffer.byteLength(record("2", "6", "staged").payload) + 1;
    db.createObjectStore("metadata", { keyPath: "key" }).put({ key: "totals", cacheRows: 2, cacheBytes, pendingRows: 0, pendingBytes: 0, receiptRows: 0, scopes: 1, sequence: 0 });
  };
  const original = await req(opening); original.close();
  const upgraded = await IndexedDbEdgeStore.open({ databaseName });
  try {
    assert.equal(JSON.parse((await upgraded.get(scope, "1")).payload).value, "preserved");
    assert.deepEqual(await upgraded.checkpoint(scope), { position: "12", ready: true });
    await upgraded.commitSnapshot(scope, "legacy-snapshot");
    assert.equal(await upgraded.get(scope, "1"), null);
    assert.equal(JSON.parse((await upgraded.get(scope, "2")).payload).value, "staged");
    assert.deepEqual(await upgraded.checkpoint(scope), { position: "13", ready: true });
  }
  finally { upgraded.close(); await req(indexedDB.deleteDatabase(databaseName)) }
});
