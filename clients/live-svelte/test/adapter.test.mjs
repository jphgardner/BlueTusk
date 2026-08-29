import assert from "node:assert/strict";
import test from "node:test";
import { SvelteLiveQuery } from "../dist/index.js";

test("Svelte adapter batches state and releases the query", async () => {
  const listeners = new Set();
  let stopped = 0;
  const query = {
    state: { phase: "idle", rows: [], lastSequence: 0, error: null },
    subscribe(listener) {
      listeners.add(listener);
      listener(this.state);
      return () => listeners.delete(listener);
    },
    start() {},
    stop() { stopped++; }
  };
  const adapter = new SvelteLiveQuery(query);
  let observed;
  const unsubscribe = adapter.state.subscribe((state) => { observed = state; });
  for (const listener of listeners) {
    listener({ phase: "live", rows: [{ id: 1 }], lastSequence: 1, error: null });
    listener({ phase: "live", rows: [{ id: 2 }], lastSequence: 2, error: null });
  }

  await Promise.resolve();
  assert.equal(observed.lastSequence, 2);
  assert.deepEqual(observed.rows, [{ id: 2 }]);
  unsubscribe();
  adapter.destroy();
  adapter.destroy();
  assert.equal(stopped, 1);
  assert.equal(listeners.size, 0);
});
