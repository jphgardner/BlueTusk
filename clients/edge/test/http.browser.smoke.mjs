import assert from "node:assert/strict";
import { createServer } from "node:http";
import { readFile, mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve, sep, basename } from "node:path";
import { chromium } from "playwright";

const endpoint = process.env.BLUETUSK_EDGE_HTTP_ENDPOINT;
if (!endpoint) throw new Error("Run the BlueTusk.Edge.BrowserHttpSmoke host, or set its disposable test endpoint.");
const assets = new Map([
  ["/edge.js", await readFile(new URL("../dist/index.js", import.meta.url))],
  ["/http.js", await readFile(new URL("../dist/http.js", import.meta.url))],
  ["/ordered.js", await readFile(new URL("../dist/ordered.js", import.meta.url))]
]);
const server = createServer((request, response) => {
  response.setHeader("Cache-Control", "no-store");
  if (assets.has(request.url)) { response.setHeader("Content-Type", "text/javascript"); response.end(assets.get(request.url)) }
  else { response.setHeader("Content-Type", "text/html"); response.end("<!doctype html><title>BlueTusk durable browser HTTP verification</title>") }
});
await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
const url = `http://127.0.0.1:${server.address().port}`;
const profile = process.env.BLUETUSK_EDGE_HTTP_BROWSER_PROFILE ?? await mkdtemp(join(tmpdir(), "bluetusk-edge-http-browser-"));
if (!resolve(profile).startsWith(resolve(tmpdir()) + sep) || !basename(resolve(profile)).startsWith("bluetusk-edge-http-browser-")) throw new Error("The smoke profile must be an owned dedicated temporary directory.");
const channel = process.env.BLUETUSK_EDGE_BROWSER_CHANNEL ?? (process.platform === "win32" ? "msedge" : "chromium");
let context;
try {
  context = await chromium.launchPersistentContext(profile, { channel, headless: true });
  let page = await context.newPage(); await page.goto(url);
  const first = await page.evaluate(async endpoint => {
    const { IndexedDbEdgeStore, EdgeHttpRemoteTransport, synchronizeEdge } = await import("/edge.js");
    const scope = { tenant: "tenant", id: "orders", epoch: "1" };
    const store = await IndexedDbEdgeStore.open({ databaseName: "http-browser" }); await store.activate(scope);
    const remote = new EdgeHttpRemoteTransport({ endpoint, bearerToken: () => "edge-smoke-reader", fetch: (url, options) => {
      if (options.method === "POST" && String(url).includes("/mutations?")) options.headers["x-bluetusk-test-drop"] = "until-reconnect";
      return fetch(url, options);
    } });
    await synchronizeEdge(store, remote, scope);
    const payload = '{ "unicode" : "🦣 café", "count" : 1 }';
    const mutation = { scope, id: crypto.randomUUID(), documentId: "1", expectedRevision: "0", kind: "upsert", payload };
    await store.enqueue(mutation);
    let disconnected = false; try { await synchronizeEdge(store, remote, scope) } catch { disconnected = true }
    if (!disconnected) throw new Error("The real committed response was not disconnected.");
    const cached = await store.get(scope, "1"); store.close();
    return { mutationId: mutation.id, payload: cached.payload, status: cached.pendingStatus };
  }, endpoint);
  assert.equal(first.payload, '{ "unicode" : "🦣 café", "count" : 1 }'); assert.equal(first.status, "leased");
  await context.setOffline(true);
  const offline = await page.evaluate(async () => {
    const { IndexedDbEdgeStore } = await import("/edge.js");
    const store = await IndexedDbEdgeStore.open({ databaseName: "http-browser" });
    const record = await store.get({ tenant: "tenant", id: "orders", epoch: "1" }, "1"); store.close(); return record.pendingId;
  });
  assert.equal(offline, first.mutationId);
  await context.close(); context = undefined;
  context = await chromium.launchPersistentContext(profile, { channel, headless: true });
  page = await context.newPage(); await page.goto(url);
  // A browser fetch rejection hides the network reason; keep the failed request and
  // CORS console error visible in CI when the deliberately severed write is replayed.
  page.on("requestfailed", request => {
    if (request.url().startsWith(endpoint)) process.stderr.write(`Edge browser request failed: ${request.method()} ${new URL(request.url()).pathname}: ${request.failure()}\n`);
  });
  page.on("console", message => {
    if (message.type() === "error") process.stderr.write(`Edge browser console: ${message.text()}\n`);
  });
  page.on("pageerror", error => process.stderr.write(`Edge browser page error: ${error}\n`));
  const resumed = await page.evaluate(async ({ endpoint, mutationId }) => {
    const { IndexedDbEdgeStore, EdgeHttpRemoteTransport, EdgeHttpError, synchronizeEdge } = await import("/edge.js");
    const scope = { tenant: "tenant", id: "orders", epoch: "1" };
    const store = await IndexedDbEdgeStore.open({ databaseName: "http-browser", now: () => Date.now() + 120_000 });
    const queued = await store.get(scope, "1"); if (queued.pendingId !== mutationId) throw new Error("Restart changed durable write identity.");
    const remote = new EdgeHttpRemoteTransport({ endpoint, bearerToken: () => "edge-smoke-reader", maxBatchRecords: 8 });
    await synchronizeEdge(store, remote, scope);
    const committed = await store.get(scope, "1");
    if (committed.pendingId !== null || committed.payload !== queued.payload || (await store.checkpoint(scope)).position !== "1") throw new Error("Restart/replay did not atomically acknowledge the original single effect.");
    let denied = false;
    try { await remote.beginSnapshot({ ...scope, tenant: "other" }) } catch (error) { denied = error instanceof EdgeHttpError && error.status === 403 }
    if (!denied) throw new Error("Browser transport crossed tenant authorization.");
    let unauthenticated = false;
    try { await new EdgeHttpRemoteTransport({ endpoint, bearerToken: () => "wrong" }).beginSnapshot(scope) }
    catch (error) { unauthenticated = error instanceof EdgeHttpError && error.status === 401 }
    if (!unauthenticated) throw new Error("Browser transport bypassed authentication.");
    const offlineId = crypto.randomUUID();
    await store.enqueue({ scope, id: offlineId, documentId: "1", expectedRevision: committed.revision, kind: "upsert", payload: '{"count":2}' });
    await remote.applyMutation({ scope, id: crypto.randomUUID(), documentId: "1", expectedRevision: committed.revision, kind: "upsert", payload: '{"count":3}' });
    await synchronizeEdge(store, remote, scope);
    const conflict = await store.get(scope, "1");
    if (conflict.pendingStatus !== "conflict" || conflict.payload !== '{"count":2}' || BigInt(conflict.revision) <= BigInt(committed.revision)) throw new Error("HTTP conflict lost the local value or authoritative revision.");
    await store.resolveConflict(offlineId, { scope, id: crypto.randomUUID(), documentId: "1", expectedRevision: conflict.revision, kind: "upsert", payload: '{"count":4}' });
    await synchronizeEdge(store, remote, scope);
    const resolved = await store.get(scope, "1"); if (resolved.pendingId !== null || resolved.payload !== '{"count":4}') throw new Error("Explicit conflict resolution did not commit.");
    await store.enqueue({ scope, id: crypto.randomUUID(), documentId: "1", expectedRevision: resolved.revision, kind: "delete", payload: "" });
    await synchronizeEdge(store, remote, scope);
    const deleted = await store.get(scope, "1"); const checkpoint = await store.checkpoint(scope); store.close();
    return { deleted: deleted.deleted, pendingId: deleted.pendingId, position: checkpoint.position };
  }, { endpoint, mutationId: first.mutationId });
  assert.deepEqual(resumed, { deleted: true, pendingId: null, position: "4" });
  process.stdout.write(`Real ${channel}/IndexedDB/PostgreSQL HTTP: commit/disconnect, offline reads, browser restart, stable write dedupe, authentication/tenant denial, conflict resolution, deletion and bounded feed passed.\n`);
} finally {
  if (context) await context.close();
  await new Promise(resolve => server.close(resolve));
  const resolved = resolve(profile); const allowed = resolve(tmpdir()) + sep;
  if (!resolved.startsWith(allowed) || !basename(resolved).startsWith("bluetusk-edge-http-browser-")) throw new Error("Refusing cleanup outside the owned temporary browser directory.");
  await rm(resolved, { recursive: true, force: true });
}
