import assert from "node:assert/strict";
import test from "node:test";
import { VueLiveQuery } from "../dist/index.js";

test("Vue adapter batches state and releases the query", async () => {
  const listeners = new Set();
  let stopped = 0;
  let started = 0;
  const query = {
    state: { phase: "idle", rows: [], lastSequence: 0, error: null },
    subscribe(listener) {
      listeners.add(listener);
      listener(this.state);
      return () => listeners.delete(listener);
    },
    start() { started++; },
    stop() { stopped++; }
  };
  const adapter = new VueLiveQuery(query);
  adapter.start();
  for (const listener of listeners) {
    listener({ phase: "live", rows: [{ id: 1 }], lastSequence: 1, error: null });
    listener({ phase: "live", rows: [{ id: 2 }], lastSequence: 2, error: null });
  }

  await Promise.resolve();
  assert.equal(adapter.state.value.lastSequence, 2);
  assert.deepEqual(adapter.rows.value, [{ id: 2 }]);
  adapter.destroy();
  adapter.destroy();
  adapter.start();
  assert.equal(started, 1);
  assert.equal(stopped, 1);
  assert.equal(listeners.size, 0);
});
