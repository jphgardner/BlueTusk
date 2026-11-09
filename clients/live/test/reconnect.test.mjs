import assert from "node:assert/strict";
import { readFile, realpath } from "node:fs/promises";
import { join, sep } from "node:path";
import test from "node:test";
import { pathToFileURL } from "node:url";
import { deferred, message, response, tick } from "./support.mjs";

let clientModule = new URL("../dist/index.js", import.meta.url);
const installedRoot = process.env.BLUETUSK_LIVE_TEST_PACKAGE_ROOT;
if (installedRoot) {
  const root = await realpath(installedRoot);
  const metadata = JSON.parse(await readFile(join(root, "package.json"), "utf8"));
  assert.equal(metadata.name, "@bluetusk/live");
  assert.ok(process.env.BLUETUSK_LIVE_TEST_PACKAGE_VERSION, "Installed-package checks require an exact expected version.");
  assert.equal(metadata.version, process.env.BLUETUSK_LIVE_TEST_PACKAGE_VERSION);
  const entry = await realpath(join(root, "dist/index.js"));
  assert.ok(entry.startsWith(root + sep), "The installed entry must remain inside its package.");
  clientModule = pathToFileURL(entry);
} else {
  assert.equal(process.env.BLUETUSK_LIVE_TEST_PACKAGE_VERSION, undefined, "Set the installed package root with its expected version.");
}
const { BlueTuskLiveClient } = await import(clientModule.href);

const reset = { kind: "ResetRequired", sequence: null, resumeToken: null, event: null };
const request = { query: "orders", parameters: {}, resumeToken: "expired" };

for (const behaviour of ["503", "empty", "reset", "expired-again"]) {
  test(`repeated ${behaviour} responses increase reconnect backoff`, { timeout: 2000 }, async () => {
    const done = deferred();
    const attempts = [];
    let calls = 0;
    const client = new BlueTuskLiveClient({ endpoint: "/live", fetch: async () => {
      calls++;
      switch (behaviour) {
        case "503": return new Response(null, { status: 503 });
        case "empty": return new Response("");
        case "reset": return response([reset]);
        default:
          // Each fresh stream issues a token which the next request rejects.
          return calls % 2 === 1 ? new Response(null, { status: 409 }) : response([
            message("InitialResult", calls, { rows: [], order: [] })
          ]);
      }
    } });
    const query = client.createQuery(request);
    client.delay = async (attempt) => {
      attempts.push(attempt);
      if (attempts.length === 5) { query.stop(); done.resolve(); }
    };
    query.start();
    await done.promise;
    await tick();
    assert.deepEqual(attempts, [0, 1, 2, 3, 4]);
    assert.equal(calls, behaviour === "reset" || behaviour === "expired-again" ? 6 : 5);
    assert.equal(query.state.phase, "stopped");
  });
}

test("only sustained streaming resets the failure count and immediate reset allowance", { timeout: 2000 }, async (t) => {
  let now = 0;
  t.mock.method(performance, "now", () => now);
  const done = deferred();
  const attempts = [];
  let calls = 0;
  const client = new BlueTuskLiveClient({ endpoint: "/live", fetch: async () => {
    calls++;
    if (calls <= 2) return new Response(null, { status: 503 });
    if (calls === 3) {
      return new Response(new ReadableStream({ pull(controller) { now += 30_001; controller.close(); } }, { highWaterMark: 0 }));
    }
    return response([reset]);
  } });
  const query = client.createQuery(request);
  client.delay = async (attempt) => {
    attempts.push(attempt);
    if (attempts.length === 4) { query.stop(); done.resolve(); }
  };
  query.start();
  await done.promise;
  assert.deepEqual(attempts, [0, 1, 0, 1]);
  assert.equal(calls, 5);
});

test("stop interrupts a pending delay without another request", { timeout: 2000 }, async () => {
  const waiting = deferred();
  let calls = 0;
  const client = new BlueTuskLiveClient({ endpoint: "/live", initialRetryDelayMs: 60_000, maximumRetryDelayMs: 60_000,
    fetch: async () => { calls++; return new Response(""); } });
  const originalDelay = client.delay.bind(client);
  client.delay = (attempt, signal) => { waiting.resolve(); return originalDelay(attempt, signal); };
  const query = client.createQuery(request);
  query.start();
  await waiting.promise;
  query.stop();
  await tick();
  assert.equal(calls, 1);
  assert.equal(query.state.phase, "stopped");
});

test("jittered delays remain capped and exponential growth is bounded", async (t) => {
  const delays = [];
  t.mock.method(globalThis, "setTimeout", (complete, milliseconds) => {
    delays.push(milliseconds);
    queueMicrotask(complete);
    return 0;
  });
  const client = new BlueTuskLiveClient({ endpoint: "/live", initialRetryDelayMs: 10, maximumRetryDelayMs: 50,
    retryJitter: 0.2, random: () => 1 });
  for (const attempt of [0, 1, 2, 3, 1000]) await client.delay(attempt, new AbortController().signal);
  assert.deepEqual(delays, [12, 24, 48, 50, 50]);
});

for (const value of [0, -1, NaN, Infinity]) {
  test(`invalid healthy streaming interval ${value} is rejected`, () => {
    assert.throws(() => new BlueTuskLiveClient({ endpoint: "/live", healthyStreamDurationMs: value }), RangeError);
  });
}
