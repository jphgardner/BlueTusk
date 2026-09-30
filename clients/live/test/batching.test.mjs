import assert from "node:assert/strict";
import test from "node:test";
import { BlueTuskLiveClient, LiveProtocolError, LiveResultStore, parseServerSentEvents } from "../dist/index.js";
import { deferred, event, frame, message, observeUntil, response, tick } from "./support.mjs";

const options = { endpoint: "/live", initialRetryDelayMs: 0, maximumRetryDelayMs: 0, retryJitter: 0 };
const request = { query: "orders", parameters: {} };
const initial = (sequence = 1) => message("InitialResult", sequence, { rows: [{ id: 1, value: 0 }], order: [1] });
const update = (sequence) => message("RowUpdated", sequence, { key: 1, row: { id: 1, value: sequence }, previousIndex: 0, currentIndex: 0 });

test("2,000 seeded valid changes match an independent ordered-array model across batches", () => {
  const store = new LiveResultStore();
  let model = [];
  let seed = 417;
  let key = 0;
  const random = (maximum) => { seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0; return seed % maximum; };
  const historical = [];
  let changes = [];
  for (let index = 0; index < 2000; index++) {
    const choice = random(5);
    if (model.length === 0 || choice === 0) {
      const currentIndex = random(model.length + 1);
      const row = { id: ++key, value: index };
      model.splice(currentIndex, 0, row);
      changes.push(event("RowAdded", { key: row.id, row, currentIndex }));
    } else if (choice === 1) {
      const previousIndex = random(model.length);
      const currentIndex = random(model.length);
      const row = { ...model[previousIndex], value: index };
      model.splice(previousIndex, 1); model.splice(currentIndex, 0, row);
      changes.push(event("RowUpdated", { key: row.id, row, previousIndex: index % 2 ? previousIndex : 9999, currentIndex }));
    } else if (choice === 2) {
      const previousIndex = random(model.length);
      const [row] = model.splice(previousIndex, 1);
      changes.push(event("RowRemoved", { key: row.id, previousIndex: index % 2 ? previousIndex : null }));
    } else if (choice === 3) {
      model.reverse();
      changes.push(event("ResultReordered", { order: model.map((row) => row.id) }));
    } else {
      model = [{ id: ++key, value: index }, { id: ++key, value: -index }];
      changes.push(event("ResultReset", { rows: [...model], order: model.map((row) => row.id) }));
    }
    if (changes.length === 32 || index === 1999) {
      const rows = store.applyBatch(changes);
      assert.deepEqual(rows, model, `change=${index}`);
      historical.push([rows, [...model]]);
      changes = [];
    }
  }
  for (const [rows, expected] of historical) assert.deepEqual(rows, expected);
});

test("batch reduction matches individual reductions and keeps historical arrays intact", () => {
  const batched = new LiveResultStore();
  const individual = new LiveResultStore();
  const before = batched.apply(initial().event);
  individual.apply(initial().event);
  const changes = [
    event("RowAdded", { key: 2, row: { id: 2 }, currentIndex: 1 }),
    update(2).event,
    event("ResultReordered", { order: [2, 1] }),
    event("RowRemoved", { key: 1, previousIndex: 1 }),
    event("ResultReset", { rows: [{ id: 9 }], order: [9] }),
    event("RowAdded", { key: 10, row: { id: 10 }, currentIndex: 1 })
  ];
  for (const change of changes) individual.apply(change);
  assert.deepEqual(batched.applyBatch(changes), individual.rows);
  assert.deepEqual(before, [{ id: 1, value: 0 }]);
  assert.equal(batched.rows, batched.rows);
});

test("invalid indexed updates never mutate a previously valid result", () => {
  const store = new LiveResultStore();
  const before = store.apply(initial().event);
  assert.throws(() => store.apply(event("RowUpdated", { key: 1, row: { id: 1, value: "bad" }, currentIndex: 9 })), LiveProtocolError);
  assert.deepEqual(store.rows, before);
});

test("batch errors leave the successful prefix available without applying the bad event", () => {
  const store = new LiveResultStore();
  store.apply(initial().event);
  assert.throws(() => store.applyBatch([update(2).event,
    event("RowUpdated", { key: 1, row: { id: 1, value: "bad" }, currentIndex: 10 })]), LiveProtocolError);
  assert.deepEqual(store.rows, [{ id: 1, value: 2 }]);
});

for (const index of [NaN, Infinity, 0.5, -1]) {
  test(`invalid insert index ${index} is rejected without changing the result`, () => {
    const store = new LiveResultStore();
    store.apply(initial().event);
    assert.throws(() => store.apply(event("RowAdded", { key: 2, row: { id: 2 }, currentIndex: index })), LiveProtocolError);
    assert.deepEqual(store.rows, [{ id: 1, value: 0 }]);
  });
}

test("a 301-event burst publishes five bounded snapshots before token persistence", { timeout: 2000 }, async () => {
  const messages = [initial(), ...Array.from({ length: 300 }, (_, i) => update(i + 2))];
  const snapshots = [];
  const tokens = [];
  let calls = 0;
  let query;
  const client = new BlueTuskLiveClient({ ...options, fetch: async () => ++calls === 1 ? response(messages) : new Response(null, { status: 401 }),
    onResumeToken: (token) => tokens.push([token, query.state.lastSequence, query.state.rows[0].value]) });
  query = client.createQuery(request);
  query.subscribe((state) => { if (state.phase === "live" && state.lastSequence > 0) snapshots.push(state); });
  await observeUntil(query, (state) => state.lastSequence === 301);
  assert.deepEqual(snapshots.map((state) => state.lastSequence), [64, 128, 192, 256, 301]);
  assert.deepEqual(tokens, snapshots.map((state) => [`token-${state.lastSequence}`, state.lastSequence, state.rows[0].value]));
  assert.equal(snapshots[0].rows[0].value, 64);
  assert.equal(snapshots.at(-1).rows[0].value, 301);
});

test("per-event compatibility mode preserves every intermediate snapshot", { timeout: 2000 }, async () => {
  const snapshots = [];
  const client = new BlueTuskLiveClient({ ...options, maximumBatchEvents: 1,
    fetch: async () => response([initial(), update(2), update(3)]) });
  const query = client.createQuery(request);
  query.subscribe((state) => { if (state.phase === "live" && state.lastSequence > 0) snapshots.push(state); });
  await observeUntil(query, (state) => state.lastSequence === 3);
  assert.deepEqual(snapshots.map((state) => state.lastSequence), [1, 2, 3]);
  assert.deepEqual(snapshots.map((state) => state.rows[0].value), [0, 2, 3]);
});

test("a sequence gap faults at the successful prefix and never advances its token", { timeout: 2000 }, async () => {
  const tokens = [];
  const query = new BlueTuskLiveClient({ ...options, fetch: async () => response([initial(), update(3)]),
    onResumeToken: (token) => tokens.push(token) }).createQuery(request);
  const state = await observeUntil(query, (state) => state.phase === "faulted");
  assert.ok(state.error instanceof LiveProtocolError);
  assert.equal(state.lastSequence, 1);
  assert.deepEqual(state.rows, [{ id: 1, value: 0 }]);
  assert.deepEqual(tokens, ["token-1"]);
});

test("duplicates inside a batch are ignored against the latest applied sequence", { timeout: 2000 }, async () => {
  const query = new BlueTuskLiveClient({ ...options, fetch: async () => response([
    initial(), update(2), message("RowAdded", 2, { key: 1, row: { id: 1 }, currentIndex: 0 }), update(3)
  ]) }).createQuery(request);
  const state = await observeUntil(query, (state) => state.lastSequence === 3);
  assert.deepEqual(state.rows, [{ id: 1, value: 3 }]);
});

test("reset control flushes its prefix, clears the token, and accepts a fresh lower-sequence snapshot", { timeout: 2000 }, async () => {
  const requests = [];
  const tokens = [];
  const query = new BlueTuskLiveClient({ ...options, fetch: async (_url, init) => {
    requests.push(JSON.parse(init.body));
    if (requests.length === 1) return response([initial(5), update(6), { kind: "ResetRequired", sequence: null, resumeToken: null, event: null }]);
    return requests.length === 2 ? response([initial(1)]) : new Response(null, { status: 401 });
  }, onResumeToken: (token) => tokens.push(token) }).createQuery(request);
  const state = await observeUntil(query, (state) => tokens.includes(null) && state.phase === "live" && state.lastSequence === 1);
  assert.deepEqual(state.rows, [{ id: 1, value: 0 }]);
  assert.deepEqual(tokens, ["token-6", null, "token-1"]);
  assert.equal("resumeToken" in requests[1], false);
});

test("a tokenless initial connection rejects deltas without an authoritative snapshot", { timeout: 2000 }, async () => {
  let calls = 0;
  const query = new BlueTuskLiveClient({ ...options, fetch: async () => ++calls === 1
    ? response([message("RowAdded", 1, { key: 1, row: { id: 1 }, currentIndex: 0 })])
    : new Response(null, { status: 401 }) }).createQuery(request);
  const state = await observeUntil(query, (state) => state.phase === "faulted");
  assert.ok(state.error instanceof LiveProtocolError);
  assert.equal(state.lastSequence, 0);
  assert.deepEqual(state.rows, []);
});

test("an old fetch completing after stop/restart cannot overwrite the new query", { timeout: 2000 }, async () => {
  const old = deferred();
  let calls = 0;
  const query = new BlueTuskLiveClient({ ...options, fetch: async () => ++calls === 1 ? old.promise : response([initial()]) }).createQuery(request);
  query.start();
  query.stop();
  await observeUntil(query, (state) => state.lastSequence === 1);
  old.resolve(response([initial(100)]));
  await tick();
  assert.equal(query.state.lastSequence, 1);
  assert.equal(query.state.phase, "stopped");
  assert.equal(calls, 2);
});

test("aborting a pending SSE read cancels its reader", { timeout: 1000 }, async () => {
  let canceled = 0;
  let controller;
  const stream = new ReadableStream({ start(value) { controller = value; }, cancel() { canceled++; } });
  const abort = new AbortController();
  const iterator = parseServerSentEvents(stream, abort.signal);
  const pending = iterator.next();
  await tick();
  abort.abort();
  await tick();
  // Release the fixture even when testing an implementation that fails to
  // cancel. This is cleanup, not acceptance: the assertion still requires it.
  if (canceled === 0) controller.close();
  assert.equal((await pending).done, true);
  assert.equal(canceled, 1);
  assert.equal(stream.locked, false);
});

test("SSE CRLF and UTF-8 remain correct at every byte split", async () => {
  const bytes = new TextEncoder().encode('event: change\r\ndata: {"value":"☃"}\r\n\r\n');
  for (let split = 1; split < bytes.length; split++) {
    const stream = new ReadableStream({ start(controller) {
      controller.enqueue(bytes.slice(0, split)); controller.enqueue(bytes.slice(split)); controller.close();
    } });
    const frames = [];
    for await (const item of parseServerSentEvents(stream)) frames.push(item);
    assert.deepEqual(frames, [{ event: "change", data: '{"value":"☃"}' }], `split=${split}`);
  }
});

test("invalid JSON is a terminal protocol failure, not a reconnect loop", { timeout: 2000 }, async () => {
  let calls = 0;
  const query = new BlueTuskLiveClient({ ...options, fetch: async () => ++calls === 1
    ? new Response('event: change\ndata: {\n\n') : new Response(null, { status: 401 }) }).createQuery(request);
  const state = await observeUntil(query, (state) => state.phase === "faulted");
  assert.ok(state.error instanceof LiveProtocolError);
  assert.equal(calls, 1);
});

test("resume callback failure retains the committed snapshot and faults without retrying", { timeout: 2000 }, async () => {
  let calls = 0;
  const failure = new Error("application storage failed");
  const query = new BlueTuskLiveClient({ ...options, fetch: async () => ++calls === 1 ? response([initial(), update(2)]) : new Response(null, { status: 401 }),
    onResumeToken() { throw failure; } }).createQuery(request);
  const state = await observeUntil(query, (state) => state.phase === "faulted");
  assert.equal(state.lastSequence, 2);
  assert.deepEqual(state.rows, [{ id: 1, value: 2 }]);
  assert.equal(calls, 1);
  assert.equal(state.error.cause, failure);
});

for (const value of [0, -1, 0.5, NaN, Infinity, 1025]) {
  test(`invalid maximumBatchEvents ${value} is refused`, () => {
    assert.throws(() => new BlueTuskLiveClient({ ...options, maximumBatchEvents: value }), RangeError);
  });
}

test("sparse traffic publishes immediately without waiting for a full batch or another read", { timeout: 1000 }, async () => {
  let canceled = 0;
  const body = new ReadableStream({ start(controller) { controller.enqueue(new TextEncoder().encode(frame(initial()))); }, cancel() { canceled++; } });
  const query = new BlueTuskLiveClient({ ...options, fetch: async () => new Response(body) }).createQuery(request);
  const state = await observeUntil(query, (state) => state.lastSequence === 1);
  assert.deepEqual(state.rows, [{ id: 1, value: 0 }]);
  await tick();
  assert.equal(canceled, 1);
});

test("resume callbacks may stop/restart without stale state or token overwrite", { timeout: 2000 }, async () => {
  let calls = 0;
  let restarted = false;
  let query;
  const tokens = [];
  const client = new BlueTuskLiveClient({ ...options, maximumBatchEvents: 1,
    fetch: async () => ++calls === 1 ? response([initial(), update(100)]) : response([update(2)]),
    onResumeToken(token) {
      tokens.push([token, query.state.lastSequence]);
      if (!restarted) { restarted = true; query.stop(); query.start(); }
    }
  });
  query = client.createQuery(request);
  await observeUntil(query, (state) => state.lastSequence === 2);
  assert.deepEqual(tokens, [["token-1", 1], ["token-2", 2]]);
  assert.deepEqual(query.state.rows, [{ id: 1, value: 2 }]);
  assert.equal(calls, 2);
});

test("retry delay removes its abort listener after normal completion", async () => {
  const { getEventListeners } = await import("node:events");
  const abort = new AbortController();
  const client = new BlueTuskLiveClient({ endpoint: "/live", initialRetryDelayMs: 1, maximumRetryDelayMs: 1, retryJitter: 0 });
  for (let attempt = 0; attempt < 20; attempt++) await client.delay(attempt, abort.signal);
  assert.equal(getEventListeners(abort.signal, "abort").length, 0);
});

for (const field of ["initialRetryDelayMs", "maximumRetryDelayMs", "retryJitter"]) {
  test(`nonfinite ${field} is refused`, () => assert.throws(() => new BlueTuskLiveClient({ ...options, [field]: NaN }), RangeError));
}

for (const [name, kind, fields] of [
  ["object rows", "ResultReset", { rows: { length: 1 }, order: [1] }],
  ["object order", "ResultReset", { rows: [{ id: 1 }], order: { length: 1 } }],
  ["string order", "ResultReset", { rows: [{ id: 1 }], order: "1" }],
  ["object key", "ResultReset", { rows: [{ id: 1 }], order: [{}] }],
  ["string reorder", "ResultReordered", { order: "1" }]
]) {
  test(`invalid ${name} faults without replacing the valid prefix`, { timeout: 2000 }, async () => {
    let calls = 0;
    const tokens = [];
    const query = new BlueTuskLiveClient({ ...options,
      fetch: async () => ++calls === 1 ? response([initial(), message(kind, 2, fields)]) : new Response(null, { status: 401 }),
      onResumeToken: (token) => tokens.push(token)
    }).createQuery(request);
    const state = await observeUntil(query, (state) => state.phase === "faulted");
    assert.ok(state.error instanceof LiveProtocolError);
    assert.equal(calls, 1);
    assert.equal(state.lastSequence, 1);
    assert.deepEqual(state.rows, [{ id: 1, value: 0 }]);
    assert.deepEqual(tokens, ["token-1"]);
  });
}
