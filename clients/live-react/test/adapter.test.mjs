import assert from "node:assert/strict";
import test, { after } from "node:test";
import { act, createElement, StrictMode } from "react";
import { renderToString } from "react-dom/server";
import { JSDOM } from "jsdom";
import { BlueTuskLiveClient } from "@bluetusk/live";
import { useBlueTuskLiveQuery } from "../dist/index.js";

const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "https://client.example.test" });
const original = new Map();
for (const [name, value] of Object.entries({ window: dom.window, document: dom.window.document,
  HTMLElement: dom.window.HTMLElement, IS_REACT_ACT_ENVIRONMENT: true })) {
  original.set(name, Object.getOwnPropertyDescriptor(globalThis, name));
  Object.defineProperty(globalThis, name, { configurable: true, writable: true, value });
}
const { createRoot } = await import("react-dom/client");
after(() => {
  dom.window.close();
  for (const [name, descriptor] of original) {
    if (descriptor === undefined) delete globalThis[name];
    else Object.defineProperty(globalThis, name, descriptor);
  }
});

function fixture() {
  const created = [];
  const client = { createQuery(request) {
    const listeners = new Set();
    const query = {
      request, state: { phase: "idle", rows: [], lastSequence: 0, error: null },
      started: 0, stopped: 0, subscriptions: 0, listeners,
      subscribe(listener) { this.subscriptions++; listeners.add(listener); listener(this.state); return () => listeners.delete(listener); },
      start() { this.started++; }, stop() { this.stopped++; },
      emit(state) { this.state = state; for (const listener of listeners) listener(state); }
    };
    created.push(query);
    return query;
  } };
  return { client, created };
}

function View({ client, request, observed, marker = "" }) {
  const state = useBlueTuskLiveQuery(client, request);
  observed.push(state);
  return createElement("span", null, `${marker}:${state.phase}:${state.lastSequence}:${state.rows[0]?.value ?? ""}`);
}

async function withRoot(work) {
  const container = document.createElement("div"); document.body.appendChild(container);
  const root = createRoot(container);
  try { await work(root, container); }
  finally { await act(async () => root.unmount()); container.remove(); }
}

test("React batches external-store updates and preserves the mounted subscription on rerender", async () => {
  const { client, created } = fixture();
  const request = { query: "orders", parameters: {} };
  const observed = [];
  await withRoot(async (root, container) => {
    await act(async () => root.render(createElement(View, { client, request, observed })));
    const query = created[0];
    assert.equal(query.started, 1);
    await act(async () => {
      for (let sequence = 1; sequence <= 3; sequence++) query.emit({ phase: "live", rows: [{ value: sequence }], lastSequence: sequence, error: null });
    });
    assert.equal(container.textContent, ":live:3:3");
    assert.deepEqual(observed.filter((state) => state.phase === "live").map((state) => state.lastSequence), [3]);
    await act(async () => root.render(createElement(View, { client, request, observed, marker: "rerender" })));
    assert.equal(created.length, 1);
    assert.equal(query.subscriptions, 1);
    assert.equal(container.textContent, "rerender:live:3:3");
  });
  assert.equal(created[0].stopped, 1);
  assert.equal(created[0].listeners.size, 0);
});

test("React request replacement releases the old query and ignores its queued notification", async () => {
  const { client, created } = fixture();
  const observed = [];
  await withRoot(async (root, container) => {
    await act(async () => root.render(createElement(View, { client, request: { query: "a", parameters: {} }, observed })));
    const old = created[0];
    await act(async () => {
      old.emit({ phase: "live", rows: [{ value: "old" }], lastSequence: 1, error: null });
      root.render(createElement(View, { client, request: { query: "b", parameters: {} }, observed }));
    });
    assert.equal(old.stopped, 1);
    assert.equal(old.listeners.size, 0);
    assert.equal(created.at(-1).started, 1);
    assert.equal(container.textContent, ":idle:0:");
  });
  assert.ok(created.every((query) => query.listeners.size === 0));
});

test("React Strict Mode lifecycle has no leaked subscriptions or updates after unmount", async () => {
  const { client, created } = fixture();
  const observed = [];
  const request = { query: "orders", parameters: {} };
  await withRoot(async (root) => {
    await act(async () => root.render(createElement(StrictMode, null, createElement(View, { client, request, observed }))));
    const active = created.find((query) => query.started > 0);
    assert.equal(active.started, 2);
    assert.equal(active.stopped, 1);
    assert.equal(active.listeners.size, 1);
  });
  const count = observed.length;
  await act(async () => { for (const query of created) query.emit({ phase: "live", rows: [], lastSequence: 100, error: null }); });
  assert.equal(observed.length, count);
  assert.ok(created.every((query) => query.listeners.size === 0));
  assert.ok(created.filter((query) => query.started > 0).every((query) => query.stopped === 2));
});

test("React server rendering never starts a browser transport", () => {
  const { client, created } = fixture();
  const markup = renderToString(createElement(View, { client, request: { query: "orders", parameters: {} }, observed: [] }));
  assert.ok(markup.includes(":idle:0:"));
  assert.equal(created.length, 1);
  assert.equal(created[0].started, 0);
  assert.equal(created[0].listeners.size, 0);
});

test("real React hook renders the final reduced SSE burst and cancels transport on unmount", async () => {
  let canceled = 0;
  const messages = Array.from({ length: 130 }, (_, index) => ({ kind: "Event", sequence: index + 1, resumeToken: `token-${index + 1}`,
    event: index === 0 ? { kind: "InitialResult", sequence: 1, rows: [{ value: 1 }], order: [1] }
      : { kind: "RowUpdated", sequence: index + 1, key: 1, row: { value: index + 1 }, previousIndex: 0, currentIndex: 0 }
  }));
  const body = new ReadableStream({ start(controller) {
    controller.enqueue(new TextEncoder().encode(messages.map((message) => `event: change\ndata: ${JSON.stringify(message)}\n\n`).join("")));
  }, cancel() { canceled++; } });
  const client = new BlueTuskLiveClient({ endpoint: "/live", fetch: async () => new Response(body) });
  const observed = [];
  const request = { query: "orders", parameters: {} };
  await withRoot(async (root, container) => {
    await act(async () => root.render(createElement(View, { client, request, observed })));
    assert.equal(container.textContent, ":live:130:130");
    assert.ok(observed.filter((state) => state.lastSequence > 0).length <= 3);
  });
  assert.equal(canceled, 1);
});
