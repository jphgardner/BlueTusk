import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, realpathSync, writeFileSync } from "node:fs";
import { Session } from "node:inspector";
import { cpus, release } from "node:os";
import { dirname, isAbsolute, relative, resolve, sep } from "node:path";
import { performance, PerformanceObserver } from "node:perf_hooks";
import { fileURLToPath, pathToFileURL } from "node:url";

const script = fileURLToPath(import.meta.url);
const repository = resolve(dirname(script), "..");
const candidateModule = resolve(repository, "clients/live/dist/index.js");
const sha256 = (bytes) => createHash("sha256").update(bytes).digest("hex");
const tick = () => new Promise((complete) => setImmediate(complete));
const mean = (values) => values.reduce((sum, value) => sum + value, 0) / values.length;
const percentile = (values, p) => [...values].sort((a, b) => a - b)[Math.ceil(values.length * p) - 1];
const git = (...args) => execFileSync("git", ["-C", repository, ...args], { maxBuffer: 16 * 1024 * 1024 });

function fixture(rowCount, scenario, count) {
  let model = Array.from({ length: rowCount }, (_, id) => ({ id, value: 0 }));
  const initial = transport("InitialResult", 1, { rows: [...model], order: model.map((row) => row.id) });
  const events = [];
  let nextKey = rowCount;
  for (let index = 0; index < (scenario === "reset" ? 1 : count); index++) {
    const sequence = index + 2;
    if (scenario === "reset") {
      model = [...model].reverse().map((row) => ({ ...row, value: 1 }));
      events.push(transport("ResultReset", sequence, { rows: model, order: model.map((row) => row.id) }));
    } else if (scenario === "churn" && index % 2 === 0) {
      const previousIndex = Math.floor(model.length / 2);
      const [row] = model.splice(previousIndex, 1);
      events.push(transport("RowRemoved", sequence, { key: row.id, previousIndex }));
    } else if (scenario === "churn") {
      const currentIndex = Math.floor(model.length / 2);
      const row = { id: nextKey++, value: sequence };
      model.splice(currentIndex, 0, row);
      events.push(transport("RowAdded", sequence, { key: row.id, row, currentIndex }));
    } else {
      const previousIndex = model.length - 1;
      const currentIndex = scenario === "rerank" ? 0 : previousIndex;
      const row = { ...model[previousIndex], value: sequence };
      model.splice(previousIndex, 1); model.splice(currentIndex, 0, row);
      events.push(transport("RowUpdated", sequence, { key: row.id, row, previousIndex, currentIndex }));
    }
  }
  const encode = (messages) => new TextEncoder().encode(messages.map((message) => `event: change\ndata: ${JSON.stringify(message)}\n\n`).join(""));
  return { initial: encode([initial]), burst: encode(events), lastSequence: events.length + 1,
    eventCount: events.length, resultSha256: sha256(JSON.stringify(model)) };
}

function transport(kind, sequence, fields) {
  return { kind: "Event", sequence, resumeToken: `token-${sequence}`, event: {
    sequence, kind, key: null, row: null, previousIndex: null, currentIndex: null,
    rows: null, order: null, resetReason: null, ...fields
  } };
}

function profileBytes(node) {
  return node.selfSize + node.children.reduce((sum, child) => sum + profileBytes(child), 0);
}

async function runBurst(module, data, sampling) {
  let controller;
  let canceled = 0;
  let tokens = 0;
  let publications = 0;
  let rowReferenceSlots = 0;
  let rows = null;
  let resolveReady;
  let rejectReady;
  let resolveDone;
  let rejectDone;
  const ready = new Promise((complete, fail) => { resolveReady = complete; rejectReady = fail; });
  const done = new Promise((complete, fail) => { resolveDone = complete; rejectDone = fail; });
  // A transport may fail before initialization; observe both promises without
  // leaving an unhandled rejection while the other boundary is awaited.
  void done.catch(() => {});
  const body = new ReadableStream({ start(value) { controller = value; value.enqueue(data.initial); }, cancel() { canceled++; } });
  const query = new module.BlueTuskLiveClient({ endpoint: "/local-diagnostic", fetch: async () => new Response(body),
    maximumBatchEvents: 64, onResumeToken() { tokens++; } }).createQuery({ query: "diagnostic", parameters: {} });
  let started = 0;
  let completed = 0;
  let cpu;
  const unsubscribe = query.subscribe((state) => {
    if (state.phase === "faulted") { rejectReady(state.error); rejectDone(state.error); return; }
    if (state.phase !== "live") return;
    if (state.lastSequence === 1) resolveReady();
    if (state.lastSequence > 1 && state.rows !== rows) {
      publications++; rowReferenceSlots += state.rows.length; rows = state.rows;
    }
    if (state.lastSequence === data.lastSequence) {
      completed = performance.now();
      cpu = process.cpuUsage(cpu);
      query.stop(); resolveDone(state);
    }
  });
  const session = sampling ? new Session() : null;
  const post = (method, params) => new Promise((complete, fail) => session.post(method, params, (error, value) => error ? fail(error) : complete(value)));
  let profile;
  const gc = [];
  const observer = new PerformanceObserver((entries) => {
    for (const entry of entries.getEntries()) {
      gc.push({ startTime: entry.startTime, duration: entry.duration, kind: entry.detail.kind });
    }
  });
  const timeout = setTimeout(() => {
    query.stop();
    const error = new Error("Browser diagnostic exceeded its 30-second observation limit.");
    rejectReady(error); rejectDone(error);
  }, 30000);
  try {
    query.start();
    await ready;
    await tick();
    if (sampling) {
      globalThis.gc();
      session.connect();
      await post("HeapProfiler.startSampling", { samplingInterval: 32768,
        includeObjectsCollectedByMajorGC: true, includeObjectsCollectedByMinorGC: true });
      observer.observe({ entryTypes: ["gc"] });
    }
    tokens = 0;
    cpu = process.cpuUsage(); started = performance.now();
    controller.enqueue(data.burst);
    const state = await done;
    await tick();
    if (sampling) profile = (await post("HeapProfiler.stopSampling")).profile;
    assert.equal(sha256(JSON.stringify(state.rows)), data.resultSha256);
    assert.equal(state.lastSequence, data.lastSequence);
    assert.equal(canceled, 1);
    return { elapsedMs: completed - started, cpuMicroseconds: cpu.user + cpu.system,
      publications, rowReferenceSlots, resumeCallbacks: tokens, committedSequence: state.lastSequence,
      resultSha256: data.resultSha256, sampledAllocatedBytes: profile === undefined ? null : profileBytes(profile.head),
      sampledAllocationSamples: profile?.samples.length ?? null,
      peakProcessRssKiB: process.resourceUsage().maxRSS,
      gc: gc.filter((entry) => entry.startTime >= started && entry.startTime <= completed), profile };
  } finally {
    clearTimeout(timeout); unsubscribe(); query.stop(); observer.disconnect(); session?.disconnect();
  }
}

async function worker(modulePath, rowCount, scenario, count) {
  assert.ok(typeof globalThis.gc === "function", "Start workers with --expose-gc.");
  const module = await import(pathToFileURL(modulePath).href);
  const data = fixture(rowCount, scenario, count);
  for (let warmup = 0; warmup < 2; warmup++) await runBurst(module, data, false);
  const result = await runBurst(module, data, true);
  return { ...result, rowCount, scenario, eventCount: data.eventCount, inputSha256: sha256(data.burst),
    moduleSha256: sha256(readFileSync(modulePath)), node: process.version, warmups: 2,
    allocationMethod: "V8 statistical heap sampling, 32768-byte interval; includes minor/major collected objects",
    measurementBoundary: "already initialized query; enqueue encoded burst through final snapshot notification" };
}

function capture(modulePath, rowCount, scenario, count) {
  return JSON.parse(execFileSync(process.execPath, ["--expose-gc", script, "--worker", modulePath,
    String(rowCount), scenario, String(count)], { maxBuffer: 32 * 1024 * 1024, timeout: 45000 }).toString());
}

if (process.argv[2] === "--worker") {
  const [, , , modulePath, rowCount, scenario, count] = process.argv;
  process.stdout.write(JSON.stringify(await worker(modulePath, Number(rowCount), scenario, Number(count))));
} else if (process.argv[2] === "--self-test") {
  for (const scenario of ["update", "churn", "rerank", "reset"]) {
    const result = capture(candidateModule, 10, scenario, 16);
    assert.equal(result.publications, 1);
    assert.equal(result.resumeCallbacks, 1);
    assert.equal(result.rowReferenceSlots, 10);
    assert.ok(result.elapsedMs > 0 && Number.isFinite(result.sampledAllocatedBytes));
  }
  console.log("Verified four browser diagnostic fixtures, authoritative final-state hashes and V8 sampling. Not release qualification.");
} else {
  const args = new Map();
  for (let index = 2; index < process.argv.length; index += 2) args.set(process.argv[index], process.argv[index + 1]);
  const referenceCommit = args.get("--reference-commit");
  assert.match(referenceCommit ?? "", /^[0-9a-f]{40}$/);
  const referenceModule = realpathSync(resolve(repository, args.get("--reference-module") ?? ""));
  const artifacts = resolve(repository, "artifacts");
  assert.ok(referenceModule.startsWith(artifacts + sep), "Use an exported, compiled reference beneath artifacts.");
  const referenceSource = resolve(dirname(referenceModule), "../src/index.ts");
  assert.equal(sha256(readFileSync(referenceSource)), sha256(git("show", `${referenceCommit}:clients/live/src/index.ts`)), "Reference source must match its declared Git commit.");
  const output = resolve(repository, args.get("--output") ?? "");
  const relativeOutput = relative(artifacts, output);
  assert.ok(relativeOutput !== "" && !relativeOutput.startsWith("..") && !isAbsolute(relativeOutput), "Use a new directory beneath artifacts.");
  assert.ok(!existsSync(output), "Existing captures cannot be overwritten.");
  let parent = dirname(output);
  while (!existsSync(parent)) parent = dirname(parent);
  assert.equal(realpathSync(parent), parent, "Output must not traverse links.");
  const samples = Number(args.get("--samples") ?? 7);
  const count = Number(args.get("--events") ?? 128);
  assert.ok(Number.isSafeInteger(samples) && samples >= 3 && samples <= 31);
  assert.ok(Number.isSafeInteger(count) && count >= 2 && count <= 1024 && count % 2 === 0);
  mkdirSync(output, { recursive: true });
  const inventory = [];
  const retain = (name, value) => {
    const bytes = typeof value === "string" ? value : JSON.stringify(value, null, 2);
    writeFileSync(resolve(output, name), bytes);
    inventory.push({ path: name, bytes: Buffer.byteLength(bytes), sha256: sha256(bytes) });
  };
  const commit = git("rev-parse", "HEAD").toString().trim();
  const clean = git("status", "--porcelain", "--untracked-files=normal").toString().trim() === "";
  retain("environment.json", { node: process.version, v8: process.versions.v8, os: process.platform,
    architecture: process.arch, kernel: release(), cpu: cpus()[0].model, logicalCpus: cpus().length,
    sourceCommit: commit, sourceClean: clean, candidateModuleSha256: sha256(readFileSync(candidateModule)),
    candidateSourceSha256: sha256(readFileSync(resolve(repository, "clients/live/src/index.ts"))),
    referenceCommit, referenceModuleSha256: sha256(readFileSync(referenceModule)),
    packageLockSha256: sha256(readFileSync(resolve(repository, "package-lock.json"))),
    harnessSha256: sha256(readFileSync(script)) });
  const comparisons = [];
  for (const rowCount of [10, 1000, 100000]) for (const scenario of ["update", "churn", "rerank", "reset"]) {
    const values = { candidate: [], reference: [] };
    for (let sample = 0; sample < samples; sample++) {
      for (const provider of sample % 2 === 0 ? ["reference", "candidate"] : ["candidate", "reference"]) {
        const value = capture(provider === "candidate" ? candidateModule : referenceModule, rowCount, scenario, count);
        retain(`${rowCount}-${scenario}-${sample}-${provider}.json`, value);
        values[provider].push(value);
      }
      assert.equal(values.candidate[sample].inputSha256, values.reference[sample].inputSha256);
      assert.equal(values.candidate[sample].resultSha256, values.reference[sample].resultSha256);
    }
    const summarize = (provider) => ({ meanMs: mean(values[provider].map((value) => value.elapsedMs)),
      p95Ms: percentile(values[provider].map((value) => value.elapsedMs), 0.95),
      p99Ms: percentile(values[provider].map((value) => value.elapsedMs), 0.99),
      meanSampledAllocatedBytes: mean(values[provider].map((value) => value.sampledAllocatedBytes)),
      totalAllocationSamples: values[provider].reduce((sum, value) => sum + value.sampledAllocationSamples, 0),
      meanCpuMicroseconds: mean(values[provider].map((value) => value.cpuMicroseconds)),
      peakProcessRssKiB: Math.max(...values[provider].map((value) => value.peakProcessRssKiB)),
      publications: values[provider][0].publications, rowReferenceSlots: values[provider][0].rowReferenceSlots });
    comparisons.push({ rowCount, scenario, samples, eventCount: values.candidate[0].eventCount,
      candidate: summarize("candidate"), reference: summarize("reference") });
    console.log(`Captured ${rowCount} rows / ${scenario}: ${samples} matched pairs.`);
  }
  retain("summary.json", { schemaVersion: 1, kind: "browser-local-diagnostic", releaseQualification: false,
    sourceCommit: commit, sourceClean: clean, referenceCommit, comparisons });
  let report = "# Browser reducer development comparison\n\nLocal Node/V8 diagnostic, not a release-performance verdict or browser end-to-end latency test.\n\n";
  report += `Candidate source: ${commit}${clean ? " (clean)" : " (dirty; use retained source/module hashes)"}. Reference: ${referenceCommit}.\n\n`;
  report += `${samples} independent fresh-process pairs per case, two warm-ups per process; alternating execution order. Initialization and fixture construction are outside the timed burst.\n\n`;
  report += "Allocation is statistical V8 sampling, not exact allocated bytes or a net-heap delta. Ratios are unresolved when either side has fewer than 32 total V8 allocation samples; this diagnostic threshold is not a confidence test. CPU counters may report zero for very short bursts, not zero CPU cost. Whole-process peak RSS includes warm-up. P95/P99 are empirical burst-duration estimates; this capture is too small to certify tail confidence intervals. Profiles, GC entries, raw samples and environment/module/lockfile hashes are retained.\n\n";
  report += "| Rows | Burst | Mean before ms | Mean after ms | Time ratio | Sampled allocation ratio | Snapshots before → after |\n| --- | --- | ---: | ---: | ---: | ---: | ---: |\n";
  for (const item of comparisons) {
    const ratio = item.reference.meanSampledAllocatedBytes === 0 ||
      Math.min(item.reference.totalAllocationSamples, item.candidate.totalAllocationSamples) < 32
      ? "unresolved" : (item.candidate.meanSampledAllocatedBytes / item.reference.meanSampledAllocatedBytes).toFixed(3);
    report += `| ${item.rowCount} | ${item.scenario} (${item.eventCount}) | ${item.reference.meanMs.toFixed(3)} | ${item.candidate.meanMs.toFixed(3)} | ${(item.candidate.meanMs / item.reference.meanMs).toFixed(3)} | ${ratio} | ${item.reference.publications} → ${item.candidate.publications} |\n`;
  }
  report += "\nNo comparison with SignalR, no Windows/Linux qualification, no slow-client/fan-out/churn endurance claim and no statistical workload-leader decision is implied. Full release gates remain mandatory.\n";
  retain("report.md", report);
  writeFileSync(resolve(output, "manifest.json"), JSON.stringify({ schemaVersion: 1,
    kind: "browser-local-diagnostic", releaseQualification: false, sourceCommit: commit,
    sourceClean: clean, referenceCommit, artifacts: inventory }, null, 2));
  console.log(`Retained ${inventory.length} hashed diagnostic artifacts in ${output}.`);
}
