import assert from "node:assert/strict";
import test from "node:test";
import { createEnvironmentInjector, Injector } from "@angular/core";
import { BlueTuskLiveClient } from "@bluetusk/live";
import { AngularLiveQuery, BlueTuskLiveAngular, BLUE_TUSK_LIVE_CLIENT, provideBlueTuskLive } from "../dist/index.js";

function fixture() {
  const listeners = new Set();
  const query = {
    state: { phase: "idle", rows: [], lastSequence: 0, error: null }, started: 0, stopped: 0,
    subscribe(listener) { listeners.add(listener); listener(this.state); return () => listeners.delete(listener); },
    start() { this.started++; }, stop() { this.stopped++; },
    emit(state) { this.state = state; for (const listener of listeners) listener(state); }
  };
  return { query, listeners, adapter: new AngularLiveQuery(query) };
}

test("Angular signals batch state, preserve readonly projections and release ownership exactly once", async () => {
  const { query, listeners, adapter } = fixture();
  adapter.start();
  const oldRows = adapter.rows();
  query.emit({ phase: "live", rows: [{ id: 1 }], lastSequence: 1, error: null });
  query.emit({ phase: "live", rows: [{ id: 2 }], lastSequence: 2, error: null });
  assert.equal(adapter.state().lastSequence, 0);
  await Promise.resolve();
  assert.equal(adapter.state().lastSequence, 2);
  assert.deepEqual(adapter.rows(), [{ id: 2 }]);
  assert.equal(adapter.phase(), "live");
  assert.equal(adapter.error(), null);
  assert.equal(adapter.state.set, undefined);
  assert.deepEqual(oldRows, []);
  adapter.destroy(); adapter.destroy(); adapter.start();
  assert.equal(query.started, 1);
  assert.equal(query.stopped, 1);
  assert.equal(listeners.size, 0);
});

test("destroy cancels an already queued Angular state publication", async () => {
  const { query, adapter } = fixture();
  query.emit({ phase: "faulted", rows: [{ id: 1 }], lastSequence: 1, error: new Error("failure") });
  adapter.destroy();
  await Promise.resolve();
  assert.equal(adapter.state().lastSequence, 0);
  assert.equal(adapter.error(), null);
});

test("Angular dependency injection resolves the configured client and query service", () => {
  const client = new BlueTuskLiveClient({ endpoint: "/live" });
  const injector = createEnvironmentInjector([provideBlueTuskLive(client)], Injector.NULL);
  try {
    assert.equal(injector.get(BLUE_TUSK_LIVE_CLIENT), client);
    const service = injector.get(BlueTuskLiveAngular);
    const query = service.createQuery({ query: "orders", parameters: {} });
    assert.equal(query.phase(), "idle");
    query.destroy();
  } finally { injector.destroy(); }
  assert.throws(() => provideBlueTuskLive(null), TypeError);
});

test("real Angular adapter receives a reduced 100,000-row burst and cancels its stream", { timeout: 3000 }, async () => {
  const rows = Array.from({ length: 100000 }, (_, id) => ({ id, value: 0 }));
  const messages = [{ kind: "Event", sequence: 1, resumeToken: "one", event: {
    sequence: 1, kind: "InitialResult", rows, order: rows.map((row) => row.id)
  } }, ...Array.from({ length: 128 }, (_, index) => ({ kind: "Event", sequence: index + 2,
    resumeToken: `token-${index + 2}`, event: { sequence: index + 2, kind: "RowUpdated", key: 99999,
      row: { id: 99999, value: index + 2 }, currentIndex: 99999, previousIndex: 99999 }
  }))];
  let canceled = 0;
  const body = new ReadableStream({ start(controller) {
    controller.enqueue(new TextEncoder().encode(messages.map((message) => `event: change\ndata: ${JSON.stringify(message)}\n\n`).join("")));
  }, cancel() { canceled++; } });
  const client = new BlueTuskLiveClient({ endpoint: "/live", fetch: async () => new Response(body) });
  const adapter = new BlueTuskLiveAngular(client).createQuery({ query: "large-results", parameters: {} });
  try {
    adapter.start();
    for (let attempt = 0; attempt < 10 && adapter.state().lastSequence !== 129; attempt++) {
      await new Promise((resolve) => setImmediate(resolve));
    }
    assert.equal(adapter.state().lastSequence, 129);
    assert.equal(adapter.phase(), "live");
    assert.equal(adapter.rows().length, 100000);
    assert.equal(adapter.rows()[99999].value, 129);
  } finally { adapter.destroy(); }
  await Promise.resolve();
  assert.equal(canceled, 1);
});
