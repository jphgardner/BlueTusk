import assert from "node:assert/strict";
import { createServer } from "node:http";
import { readFile, mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve, sep } from "node:path";
import { chromium } from "playwright";

const asset = await readFile(new URL("../dist/index.js", import.meta.url));
const httpAsset = await readFile(new URL("../dist/http.js", import.meta.url));
const orderedAsset = await readFile(new URL("../dist/ordered.js", import.meta.url));
const server = createServer((request, response) => {
  response.setHeader("Cache-Control", "no-store");
  if (request.url === "/edge.js") { response.setHeader("Content-Type", "text/javascript"); response.end(asset) }
  else if (request.url === "/http.js") { response.setHeader("Content-Type", "text/javascript"); response.end(httpAsset) }
  else if (request.url === "/ordered.js") { response.setHeader("Content-Type", "text/javascript"); response.end(orderedAsset) }
  else { response.setHeader("Content-Type", "text/html"); response.end("<!doctype html><title>BlueTusk Edge IndexedDB verification</title>") }
});
await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
const url = `http://127.0.0.1:${server.address().port}`;
const profile = await mkdtemp(join(tmpdir(), "bluetusk-edge-browser-"));
const channel = process.env.BLUETUSK_EDGE_BROWSER_CHANNEL ?? (process.platform === "win32" ? "msedge" : "chromium");
let context;
try {
  context = await chromium.launchPersistentContext(profile, { channel, headless: true });
  let page = await context.newPage(); await page.goto(url);
  const first = await page.evaluate(async () => {
    const { IndexedDbEdgeStore, EdgeRevisionError } = await import("/edge.js");
    const scope = { tenant: "tenant", id: "readers", epoch: "1" };
    const store = await IndexedDbEdgeStore.open({ databaseName: "real-browser", now: () => 1_800_000_000_000 });
    await store.activate(scope); await store.beginSnapshot(scope, "snapshot-1", "9007199254740999");
    await store.applySnapshot(scope, "snapshot-1", [{ id: "1", revision: "9007199254740997", payload: "{\"value\":\"cached\"}", deleted: false }]);
    if (await store.get(scope, "1") !== null) throw new Error("Staged snapshot became visible.");
    await store.commitSnapshot(scope, "snapshot-1");
    const mutation = { scope, id: "write-1", documentId: "2", expectedRevision: "0", kind: "upsert", payload: "{\"value\":\"offline\"}" };
    await store.enqueue(mutation); await store.enqueue(mutation);
    const lease = await store.claim(scope, 1000);
    let rolledBack = false;
    try { await store.applyChanges(scope, "9007199254740999", "9007199254741000", [{ id: "3", revision: "1", payload: "{}", deleted: false }, { id: "1", revision: "9007199254740997", payload: "{\"different\":true}", deleted: false }]) }
    catch (error) { rolledBack = error instanceof EdgeRevisionError }
    if (!rolledBack || await store.get(scope, "3") !== null) throw new Error("Real IndexedDB did not roll back the conflicting batch.");
    const cached = await store.get(scope, "1"); store.close();
    return { revision: cached.revision, leaseFence: lease.fence, mutationId: lease.mutation.id };
  });
  assert.equal(first.revision, "9007199254740997"); assert.equal(first.leaseFence, 1);
  await context.close(); context = undefined;
  context = await chromium.launchPersistentContext(profile, { channel, headless: true });
  page = await context.newPage(); await page.goto(url);
  const resumed = await page.evaluate(async () => {
    const { IndexedDbEdgeStore, EdgeScopeError } = await import("/edge.js");
    const scope = { tenant: "tenant", id: "readers", epoch: "1" };
    const store = await IndexedDbEdgeStore.open({ databaseName: "real-browser", now: () => 1_800_000_001_001 });
    const lease = await store.claim(scope, 1000);
    if (!lease || lease.mutation.id !== "write-1" || lease.fence !== 2) throw new Error("Browser restart failed to recover the durable mutation identity/fence.");
    await store.acknowledge(lease, { kind: "applied", record: { id: "2", revision: "1", payload: "{\"value\":\"committed\"}", deleted: false } });
    await store.acknowledge(lease, { kind: "applied", record: { id: "2", revision: "1", payload: "{\"value\":\"committed\"}", deleted: false } });
    if (await store.claim(scope) !== null) throw new Error("Acknowledged mutation remained queued.");
    const checkpoint = await store.checkpoint(scope); const committed = await store.get(scope, "2");
    await store.activate({ ...scope, epoch: "2" }, "discard");
    let denied = false; try { await store.get(scope, "1") } catch (error) { denied = error instanceof EdgeScopeError }
    store.close(); return { checkpoint, pendingId: committed.pendingId, denied };
  });
  assert.equal(resumed.checkpoint.position, "9007199254740999"); assert.equal(resumed.pendingId, null); assert.equal(resumed.denied, true);
  process.stdout.write(`Real ${channel} IndexedDB: snapshot/rollback, browser restart, lease fencing, atomic acknowledgement, Int64 precision and epoch isolation passed.\n`);
} finally {
  if (context) await context.close();
  await new Promise(resolve => server.close(resolve));
  const resolved = resolve(profile); const allowed = resolve(tmpdir()) + sep;
  if (!resolved.startsWith(allowed) || !resolved.includes("bluetusk-edge-browser-")) throw new Error("Refusing browser profile cleanup outside the verified temporary directory.");
  await rm(resolved, { recursive: true, force: true });
}
