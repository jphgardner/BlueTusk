import assert from "node:assert/strict";
import { createServer } from "node:http";
import { readFile, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve, sep } from "node:path";
import { chromium, firefox, webkit } from "playwright";

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
const browserType = { chromium, msedge: chromium, firefox, webkit }[channel];
if (!browserType) throw new Error(`Unsupported Edge browser channel: ${channel}`);
const launchOptions = { headless: true, ...(channel === "msedge" ? { channel } : {}) };
let context;
try {
  context = await browserType.launchPersistentContext(profile, launchOptions);
  let page = await context.newPage(); await page.goto(url);
  const userAgent = await page.evaluate(() => navigator.userAgent);
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
  context = await browserType.launchPersistentContext(profile, launchOptions);
  page = await context.newPage(); await page.goto(url);
  const resumed = await page.evaluate(async () => {
    const { IndexedDbEdgeStore, EdgeIdentityError } = await import("/edge.js");
    const scope = { tenant: "tenant", id: "readers", epoch: "1" };
    const store = await IndexedDbEdgeStore.open({ databaseName: "real-browser", now: () => 1_800_000_001_001 });
    const lease = await store.claim(scope, 1000);
    if (!lease || lease.mutation.id !== "write-1" || lease.fence !== 2) throw new Error("Browser restart failed to recover the durable mutation identity/fence.");
    await store.acknowledge(lease, { kind: "applied", record: { id: "2", revision: "1", payload: "{\"value\":\"committed\"}", deleted: false } });
    await store.acknowledge(lease, { kind: "applied", record: { id: "2", revision: "1", payload: "{\"value\":\"committed\"}", deleted: false } });
    if (await store.claim(scope) !== null) throw new Error("Acknowledged mutation remained queued.");
    for (const id of ["3", "4"]) await store.enqueueOrdered({ scope, documentId: id, expectedRevision: "0", kind: "upsert", payload: "{}" });
    const ordered = await store.claimOrderedBatch(scope, 2);
    if (ordered?.length !== 2) throw new Error("Real IndexedDB did not claim the ordered prefix.");
    await store.acknowledgeBatch(ordered.map(item => ({ lease: item, outcome: { kind: "applied", record: {
      id: item.mutation.documentId, revision: "1", payload: "{}", deleted: false
    } } })));
    if ((await store.get(scope, "3")).pendingId !== null || (await store.get(scope, "4")).pendingId !== null ||
        (await store.nextUnconfirmedOrderedReceipt(scope)).id !== ordered[0].mutation.id)
      throw new Error("Real IndexedDB did not commit the ordered batch and confirmation outbox atomically.");
    const receipts = await store.nextUnconfirmedOrderedReceiptBatch(scope, 2);
    if (receipts.length !== 2) throw new Error("Real IndexedDB did not return the bounded confirmation batch.");
    let rejected = false;
    try { await store.markOrderedReceiptConfirmedBatch([receipts[0], { ...receipts[1], documentId: "substitution" }]) }
    catch (error) { rejected = error instanceof EdgeIdentityError }
    if (!rejected || await store.confirmedOrderedHorizon(scope) !== null ||
        (await store.nextUnconfirmedOrderedReceiptBatch(scope, 2)).length !== 2)
      throw new Error("Real IndexedDB failed to roll back the invalid confirmation suffix.");
    await store.markOrderedReceiptConfirmedBatch(receipts);
    const checkpoint = await store.checkpoint(scope); const committed = await store.get(scope, "2");
    store.close(); return { checkpoint, pendingId: committed.pendingId, through: receipts[1].id };
  });
  assert.equal(resumed.checkpoint.position, "9007199254740999"); assert.equal(resumed.pendingId, null);
  await context.close(); context = undefined;
  context = await browserType.launchPersistentContext(profile, launchOptions);
  page = await context.newPage(); await page.goto(url);
  const confirmed = await page.evaluate(async through => {
    const { IndexedDbEdgeStore, EdgeScopeError } = await import("/edge.js");
    const scope = { tenant: "tenant", id: "readers", epoch: "1" };
    const store = await IndexedDbEdgeStore.open({ databaseName: "real-browser" });
    if ((await store.nextUnconfirmedOrderedReceiptBatch(scope, 2)).length || await store.confirmedOrderedHorizon(scope) !== through)
      throw new Error("Browser restart lost the atomically confirmed prefix.");
    await store.markOrderedHorizon(scope, through);
    if (await store.confirmedOrderedHorizon(scope) !== null) throw new Error("Browser restart could not reclaim confirmed receipts.");
    await store.activate({ ...scope, epoch: "2" }, "discard");
    let denied = false; try { await store.get(scope, "1") } catch (error) { denied = error instanceof EdgeScopeError }
    store.close(); return { denied };
  }, resumed.through);
  assert.equal(confirmed.denied, true);
  if (process.env.BLUETUSK_EDGE_BROWSER_RESULT) {
    await writeFile(process.env.BLUETUSK_EDGE_BROWSER_RESULT, JSON.stringify({
      formatVersion: 1, channel, engine: browserType.name(), platform: process.platform,
      userAgent, passed: true, productionQualified: false,
      checks: ["indexeddb-snapshot-rollback", "persistent-profile-restart", "lease-fence",
        "atomic-acknowledgement", "ordered-batch-acknowledgement", "confirmation-batch-rollback",
        "confirmation-batch-browser-restart", "int64-precision", "epoch-isolation"]
    }, null, 2) + "\n", { flag: "wx" });
  }
  process.stdout.write(`Real ${channel} IndexedDB: snapshot/rollback, browser restart, lease fencing, atomic ordered acknowledgement and confirmation batches, Int64 precision and epoch isolation passed.\n`);
} finally {
  if (context) await context.close();
  await new Promise(resolve => server.close(resolve));
  const resolved = resolve(profile); const allowed = resolve(tmpdir()) + sep;
  if (!resolved.startsWith(allowed) || !resolved.includes("bluetusk-edge-browser-")) throw new Error("Refusing browser profile cleanup outside the verified temporary directory.");
  await rm(resolved, { recursive: true, force: true });
}
