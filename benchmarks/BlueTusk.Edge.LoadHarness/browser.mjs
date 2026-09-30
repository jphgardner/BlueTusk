import { createServer } from "node:http";
import { readFile, writeFile, readdir, stat } from "node:fs/promises";
import { join } from "node:path";
import { performance } from "node:perf_hooks";
import { chromium } from "playwright";

const endpoint = process.env.BLUETUSK_EDGE_LOAD_ENDPOINT;
const seconds = Number(process.env.BLUETUSK_EDGE_LOAD_SECONDS);
const profile = process.env.BLUETUSK_EDGE_LOAD_PROFILE;
const ready = process.env.BLUETUSK_EDGE_LOAD_READY;
const startFile = process.env.BLUETUSK_EDGE_LOAD_START;
const reportPath = process.env.BLUETUSK_EDGE_LOAD_REPORT;
const checkpointFile = process.env.BLUETUSK_EDGE_LOAD_CHECKPOINT;
const assetDirectory = process.env.BLUETUSK_EDGE_LOAD_ASSETS;
if (!endpoint || !Number.isInteger(seconds) || seconds < 30 || !profile || !ready || !startFile || !reportPath || !checkpointFile || !assetDirectory)
  throw new Error("The Edge browser capacity fixture configuration is incomplete.");

const assets = new Map([
  ["/edge.js", await readFile(join(assetDirectory, "index.js"))],
  ["/http.js", await readFile(join(assetDirectory, "http.js"))],
  ["/ordered.js", await readFile(join(assetDirectory, "ordered.js"))]
]);
const server = createServer((request, response) => {
  response.setHeader("Cache-Control", "no-store");
  if (assets.has(request.url)) { response.setHeader("Content-Type", "text/javascript"); response.end(assets.get(request.url)); }
  else { response.setHeader("Content-Type", "text/html"); response.end("<!doctype html><title>BlueTusk Edge capacity fixture</title>"); }
});
await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
const channel = process.env.BLUETUSK_EDGE_BROWSER_CHANNEL ?? (process.platform === "win32" ? "msedge" : "chromium");
const scope = { tenant: "tenant-07", id: "orders", epoch: "1" };
let context;
let page;
let clockOffset = 0;
const pending = new Map();
const enqueueTimes = [], ackTimes = [];
const applyBeforeRestart = [], horizonBeforeRestart = [];
const recoveries = [];
let offered = 0, skipped = 0, scheduleSkipped = 0, pendingKeySkipped = 0;
let acknowledged = 0, peakPending = 0, peakOutbox = 0, maximumPhysicalBytes = 0;
let firstMutation = null, lostMutation = null, lostAt = 0, lostRecovered = false;
let firstOfflineRecovered = false, secondOfflineRecovered = false, hostRecovered = false;
let offlineActive = false, lossArmed = false, restarted = false;
let lastSample = 0;
const offlineLength = seconds >= 1800 ? 30 : Math.min(5, seconds / 12);
const offlineOne = seconds / 3, lossAt = seconds / 2, offlineTwo = seconds * 2 / 3, hostAt = seconds * .75;
const overlaps = (offeredAt, acknowledgedAt, start, end) => offeredAt <= end && acknowledgedAt >= start;
const faultAffected = (offeredAt, acknowledgedAt) =>
  overlaps(offeredAt, acknowledgedAt, offlineOne, offlineOne + offlineLength) ||
  overlaps(offeredAt, acknowledgedAt, offlineTwo, offlineTwo + offlineLength) ||
  overlaps(offeredAt, acknowledgedAt, hostAt - 2, hostAt + 30) ||
  (lostMutation !== null && overlaps(offeredAt, acknowledgedAt, lostAt - 10, lostAt + 10));

async function openBrowser(synchronizeInitial = true) {
  context = await chromium.launchPersistentContext(profile, { channel, headless: true });
  page = await context.newPage(); await page.goto(origin);
  await page.evaluate(async ({ endpoint, clockOffset, synchronizeInitial }) => {
    const { IndexedDbEdgeStore, EdgeHttpRemoteTransport, synchronizeEdge } = await import("/edge.js");
    const scope = { tenant: "tenant-07", id: "orders", epoch: "1" };
    const local = await IndexedDbEdgeStore.open({ databaseName: "edge-capacity", now: () => Date.now() + clockOffset,
      maxRecordBytes: 8192, maxCacheRecords: 512, maxCacheBytes: 4 * 1024 * 1024,
      maxStagedRecords: 512, maxStagedBytes: 4 * 1024 * 1024, maxPendingRecords: 512,
      maxPendingBytes: 4 * 1024 * 1024, maxReceipts: 512 });
    await local.activate(scope);
    const metrics = { apply: [], horizon: [] };
    const remote = new EdgeHttpRemoteTransport({ endpoint, bearerToken: () => "edge-capacity-tenant-07",
      fetch: async (url, options) => {
        const started = performance.now();
        const response = await fetch(url, options);
        const path = new URL(url).pathname;
        if (response.ok && options.method === "POST" && path.endsWith("/mutations")) {
          metrics.apply.push(performance.now() - started);
          if (window.edgeDropNextResponse) { window.edgeDropNextResponse = false; throw new TypeError("Failed to fetch"); }
        }
        if (response.ok && options.method === "POST" && path.endsWith("/mutations/horizon"))
          metrics.horizon.push(performance.now() - started);
        return response;
      } });
    window.edgeCapacity = { local, remote, scope, metrics, synchronizeEdge };
    if (synchronizeInitial) await synchronizeEdge(local, remote, scope, { maxPushes: 16, maxChangeBatches: 16 });
  }, { endpoint, clockOffset, synchronizeInitial });
}

function summary(values) {
  if (!values.length) throw new Error("A required browser latency sample set is empty.");
  values.sort((a, b) => a - b);
  const at = q => values[Math.max(0, Math.ceil(values.length * q) - 1)];
  return { Samples: values.length, P50Milliseconds: at(.5), P95Milliseconds: at(.95),
    P99Milliseconds: at(.99), MaximumMilliseconds: values.at(-1) };
}

async function state() {
  return await page.evaluate(async () => {
    const scope = { tenant: "tenant-07", id: "orders", epoch: "1" };
    const opening = indexedDB.open("edge-capacity", 3);
    const db = await new Promise((resolve, reject) => { opening.onsuccess = () => resolve(opening.result); opening.onerror = () => reject(opening.error); });
    try {
      const tx = db.transaction(["metadata", "orderedStreams"], "readonly");
      const get = request => new Promise((resolve, reject) => { request.onsuccess = () => resolve(request.result); request.onerror = () => reject(request.error); });
      const totalsRequest = get(tx.objectStore("metadata").get("totals"));
      const streamRequest = get(tx.objectStore("orderedStreams").get([scope.tenant, scope.id, scope.epoch]));
      const [totals, stream] = await Promise.all([totalsRequest, streamRequest]);
      const checkpoint = await window.edgeCapacity.local.checkpoint(scope);
      return { pending: totals.pendingRows, outbox: totals.orderedOutboxRows, receipts: totals.receiptRows,
        checkpoint: checkpoint.position, streamId: stream.streamId, nextSequence: stream.nextSequence, horizon: stream.horizon };
    } finally { db.close(); }
  });
}

async function physicalBytes(path) {
  let total = 0;
  for (const entry of await readdir(path, { withFileTypes: true })) {
    const full = join(path, entry.name);
    if (entry.isDirectory()) total += await physicalBytes(full);
    else if (entry.isFile()) {
      try { total += (await stat(full)).size; }
      catch (error) { if (error.code !== "ENOENT") throw error; }
    }
  }
  return total;
}

async function sample(elapsed) {
  if (elapsed - lastSample < 5 && lastSample !== 0) return;
  lastSample = elapsed;
  const current = await state();
  peakPending = Math.max(peakPending, current.pending);
  peakOutbox = Math.max(peakOutbox, current.outbox);
  maximumPhysicalBytes = Math.max(maximumPhysicalBytes, await physicalBytes(profile));
  await writeFile(checkpointFile, current.checkpoint);
}

async function synchronize(elapsed, pendingKeys) {
  return await page.evaluate(async ({ pendingKeys }) => {
    const { local, remote, scope, synchronizeEdge } = window.edgeCapacity;
    try { await synchronizeEdge(local, remote, scope, { maxPushes: 16, maxChangeBatches: 16 }); }
    catch (error) {
      if (error instanceof TypeError && error.message === "Failed to fetch") return { failure: "fetch", acknowledged: [] };
      throw error;
    }
    const acknowledged = [];
    for (const key of pendingKeys) if ((await local.get(scope, key)).pendingId === null) acknowledged.push(key);
    return { failure: null, acknowledged };
  }, { pendingKeys });
}

try {
  await openBrowser();
  await writeFile(ready, "ready");
  let startEpoch;
  while (startEpoch === undefined) {
    try { startEpoch = Number(await readFile(startFile, "utf8")); }
    catch (error) { if (error.code !== "ENOENT") throw error; await new Promise(resolve => setTimeout(resolve, 100)); }
  }
  if (!Number.isSafeInteger(startEpoch)) throw new Error("Invalid capacity start signal.");
  const before = startEpoch - Date.now(); if (before > 0) await new Promise(resolve => setTimeout(resolve, before));
  const began = performance.now();
  const plannedSlots = seconds * 1000 / 200;
  let next = 0;
  while (next < plannedSlots && (performance.now() - began) / 1000 < seconds) {
    const elapsed = (performance.now() - began) / 1000;
    const due = next * .2;
    if (due > elapsed) { await new Promise(resolve => setTimeout(resolve, Math.min(20, (due - elapsed) * 1000))); continue; }
    const current = Math.floor(elapsed / .2);
    if (current > next) { skipped += current - next; scheduleSkipped += current - next; next = current; }
    const key = "doc-" + String(next % 256).padStart(3, "0");
    const injectedOffline = (elapsed >= offlineOne && elapsed < offlineOne + offlineLength) ||
      (elapsed >= offlineTwo && elapsed < offlineTwo + offlineLength);
    if (injectedOffline && !offlineActive) { await context.setOffline(true); offlineActive = true; }
    if (!injectedOffline && offlineActive) { await context.setOffline(false); offlineActive = false; }
    const payload = await page.evaluate(() => {
      const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
      const random = crypto.getRandomValues(new Uint8Array(4088));
      let text = '{"v":"'; for (const value of random) text += alphabet[value & 63]; return text + '"}';
    });
    const offeredResult = await page.evaluate(async ({ key, payload }) => {
      const { local, scope } = window.edgeCapacity;
      const cached = await local.get(scope, key);
      if (cached.pendingId !== null) return { skipped: true };
      const began = performance.now();
      const mutation = await local.enqueueOrdered({ scope, documentId: key, expectedRevision: cached.revision,
        kind: "upsert", payload });
      return { skipped: false, mutation, enqueueMilliseconds: performance.now() - began };
    }, { key, payload });
    if (offeredResult.skipped) { skipped++; pendingKeySkipped++; }
    else {
      offered++; enqueueTimes.push(offeredResult.enqueueMilliseconds);
      pending.set(key, { id: offeredResult.mutation.id, began: performance.now(), offeredAt: elapsed,
        mutation: offeredResult.mutation });
      if (!firstMutation) firstMutation = offeredResult.mutation;
      peakPending = Math.max(peakPending, pending.size);
    }
    next++;
    if (injectedOffline) { await sample(elapsed); continue; }
    // Batch five offered writes into each bounded reconnect pass. The 200 ms
    // offer schedule remains independent of redundant empty remote polls.
    if (next % 5 !== 0) { await sample(elapsed); continue; }
    if (elapsed >= lossAt && !lossArmed) {
      lossArmed = true;
      await page.evaluate(() => { window.edgeDropNextResponse = true; });
    }
    let synced;
    try { synced = await synchronize(elapsed, [...pending.keys()]); }
    catch (error) {
      if (elapsed >= hostAt - 2 && elapsed <= hostAt + 30 && (error instanceof Error)) { await sample(elapsed); continue; }
      throw error;
    }
    if (synced.failure === "fetch") {
      if (elapsed >= hostAt - 2 && elapsed <= hostAt + 30) { await sample(elapsed); continue; }
      if (!lossArmed || restarted) throw new Error("Unexpected browser transport failure.");
      lostAt = (performance.now() - began) / 1000;
      const leased = await page.evaluate(async keys => {
        for (const candidate of keys) {
          const row = await window.edgeCapacity.local.get(window.edgeCapacity.scope, candidate);
          if (row.pendingStatus === "leased") return row.pendingId;
        }
        return null;
      }, [...pending.keys()]);
      const lostKey = [...pending.entries()].find(([, row]) => row.id === leased)?.[0];
      if (!leased || lostKey === undefined) throw new Error("Lost response did not leave an original durable identity.");
      lostMutation = leased;
      const metrics = await page.evaluate(() => window.edgeCapacity.metrics);
      applyBeforeRestart.push(...metrics.apply); horizonBeforeRestart.push(...metrics.horizon);
      await context.close(); context = undefined; page = undefined;
      clockOffset = 120_000; await openBrowser(false); restarted = true;
      const persisted = await page.evaluate(async key => (await window.edgeCapacity.local.get(window.edgeCapacity.scope, key)).pendingId, lostKey);
      if (persisted !== lostMutation) throw new Error("Browser restart changed the leased mutation identity.");
      continue;
    }
    for (const key of synced.acknowledged) {
      const row = pending.get(key); if (!row) throw new Error("Acknowledged browser key was not offered.");
      pending.delete(key); acknowledged++;
      if (row.id === lostMutation) { recoveries.push((performance.now() - began) / 1000 - lostAt); lostRecovered = true; }
      if (elapsed > 10 && !faultAffected(row.offeredAt, elapsed))
        ackTimes.push(performance.now() - row.began);
    }
    if (!firstOfflineRecovered && elapsed >= offlineOne + offlineLength)
      { recoveries.push(elapsed - (offlineOne + offlineLength)); firstOfflineRecovered = true; }
    if (!secondOfflineRecovered && elapsed >= offlineTwo + offlineLength)
      { recoveries.push(elapsed - (offlineTwo + offlineLength)); secondOfflineRecovered = true; }
    if (!hostRecovered && elapsed >= hostAt + 2)
      { recoveries.push(elapsed - hostAt); hostRecovered = true; }
    await sample(elapsed);
  }
  skipped += plannedSlots - next;
  scheduleSkipped += plannedSlots - next;
  const drainStart = performance.now();
  while (true) {
    const current = await state();
    if (!pending.size && !current.pending && !current.outbox) break;
    if ((performance.now() - drainStart) / 1000 > 120) throw new Error("Browser drain exceeded 120 seconds.");
    const synced = await synchronize((performance.now() - began) / 1000, [...pending.keys()]);
    if (synced.failure) throw new Error("Browser transport remained disconnected during drain.");
    for (const key of synced.acknowledged) {
      const row = pending.get(key); if (!row) throw new Error("Drain acknowledged an unknown browser write.");
      pending.delete(key); acknowledged++;
      if (row.id === lostMutation) { recoveries.push((performance.now() - began) / 1000 - lostAt); lostRecovered = true; }
    }
    await new Promise(resolve => setTimeout(resolve, 20));
  }
  await sample((performance.now() - began) / 1000);
  const final = await state();
  const verified = await page.evaluate(async first => {
    const { local, remote, scope } = window.edgeCapacity;
    const { EdgeHttpError } = await import("/edge.js");
    let fenced = false;
    try { await remote.applyMutation(first); } catch (error) { fenced = error instanceof EdgeHttpError && error.status === 410; }
    const snapshot = await remote.beginSnapshot(scope); let count = 0, exact = true;
    for await (const records of remote.readSnapshot(scope, snapshot)) for (const record of records) {
      const cached = await local.get(scope, record.id); count++;
      if (!cached || cached.pendingId !== null || cached.revision !== record.revision || cached.payload !== record.payload) exact = false;
    }
    return { fenced, exact: exact && count === 256, metrics: window.edgeCapacity.metrics };
  }, firstMutation);
  const result = {
    Index: 7, Kind: "IndexedDB", OrderedStreamId: final.streamId, Offered: offered, Skipped: skipped,
    ScheduleSkipped: scheduleSkipped, PendingKeySkipped: pendingKeySkipped,
    Acknowledged: acknowledged, ExpectedConflicts: 0, UnexpectedConflicts: 0, PeakPending: peakPending,
    PeakOutbox: peakOutbox, FinalPending: final.pending, FinalOutbox: final.outbox,
    FinalLocalReceipts: final.receipts, FinalCheckpoint: Number(final.checkpoint),
    FinalOrderedSequence: Number(BigInt(final.nextSequence) - 1n), FinalHorizon: Number(final.horizon),
    MaximumPhysicalBytes: maximumPhysicalBytes, Enqueue: summary(enqueueTimes),
    HttpApply: summary([...applyBeforeRestart, ...verified.metrics.apply]), DurableAck: summary(ackTimes),
    Horizon: summary([...horizonBeforeRestart, ...verified.metrics.horizon]), FaultRecoverySeconds: recoveries,
    LostResponseRecovered: lostRecovered, ReclaimedRetryFenced: verified.fenced, ExactFinalCache: verified.exact
  };
  await writeFile(reportPath, JSON.stringify(result, null, 2));
  process.stdout.write(`IndexedDB browser client: ${acknowledged} durable ordered writes, ${scheduleSkipped} schedule skips, ${pendingKeySkipped} pending-key skips, horizon ${final.horizon}, profile peak ${maximumPhysicalBytes} bytes.\n`);
} finally {
  if (context) await context.close();
  await new Promise(resolve => server.close(resolve));
}
