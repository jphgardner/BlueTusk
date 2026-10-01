import type { EdgeScope, EdgeRecord, EdgeMutation, EdgeOutcome, EdgeAcknowledgement, IndexedDbEdgeStore } from "./index.js";
import { parseOrderedMutationId } from "./ordered.js";

export interface EdgeHttpOptions {
  readonly endpoint: string;
  readonly bearerToken: () => string | Promise<string>;
  readonly fetch?: typeof fetch;
  readonly timeoutMilliseconds?: number;
  readonly maxRecordBytes?: number;
  readonly maxRequestBytes?: number;
  readonly maxResponseBytes?: number;
  readonly maxBatchRecords?: number;
}
export interface EdgeRemoteSnapshot { readonly id: string; readonly position: string }
export interface EdgeRemoteChanges { readonly fromPosition: string; readonly toPosition: string; readonly records: readonly EdgeRecord[] }
export class EdgeHttpError extends Error { constructor(readonly status: number) { super(`Edge HTTP ${status}; persisted writes remain available for recovery.`) } }
const encoder = new TextEncoder();
const decoder = new TextDecoder("utf-8", { fatal: true });
function integer(value: unknown): string {
  if (typeof value !== "string" || !/^(0|[1-9][0-9]*)$/.test(value) || BigInt(value) > 9223372036854775807n) throw new TypeError("A canonical Int64 decimal string is required.");
  return value;
}
function uuid(value: unknown): string {
  if (typeof value !== "string" || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value) || /^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(value)) throw new TypeError("A stable UUID is required by the server transport.");
  return value;
}
function object(value: unknown): Record<string, unknown> { if (!value || typeof value !== "object" || Array.isArray(value)) throw new TypeError("Invalid Edge wire object."); return value as Record<string, unknown> }
function text(value: unknown): string { if (typeof value !== "string") throw new TypeError("Invalid Edge wire string."); return value }
function encodePayload(value: string, maximum: number): string {
  const bytes = encoder.encode(value); if (bytes.length > maximum) throw new RangeError("Edge payload byte limit exceeded.");
  let binary = ""; for (let at = 0; at < bytes.length; at += 4096) binary += String.fromCharCode(...bytes.subarray(at, at + 4096));
  return btoa(binary);
}
function decodePayload(value: unknown, maximum: number): string {
  const encoded = text(value); if (encoded.length > Math.ceil(maximum / 3) * 4) throw new RangeError("Edge payload byte limit exceeded.");
  const binary = atob(encoded); if (binary.length > maximum) throw new RangeError("Edge payload byte limit exceeded.");
  return decoder.decode(Uint8Array.from(binary, character => character.charCodeAt(0)));
}

/** Authenticated bearer-only client. Requests never send cookies; every response and record has a byte limit. */
export class EdgeHttpRemoteTransport {
  private readonly endpoint: string;
  private readonly limits;
  constructor(private readonly options: EdgeHttpOptions) {
    const endpoint = new URL(options.endpoint);
    if (!/^https?:$/.test(endpoint.protocol) || endpoint.username || endpoint.password || endpoint.search || endpoint.hash) throw new TypeError("A trusted HTTP(S) endpoint is required.");
    this.endpoint = endpoint.href.replace(/\/$/, "");
    this.limits = { record: options.maxRecordBytes ?? 512 * 1024, request: options.maxRequestBytes ?? 1024 * 1024, response: options.maxResponseBytes ?? 16 * 1024 * 1024,
      rows: options.maxBatchRecords ?? 512, timeout: options.timeoutMilliseconds ?? 30_000 };
    for (const value of Object.values(this.limits)) if (!Number.isSafeInteger(value) || value <= 0) throw new RangeError("Edge HTTP limits must be positive integers.");
    if (this.limits.record > 64 * 1024 * 1024 || this.limits.request > 256 * 1024 * 1024 || this.limits.response > 256 * 1024 * 1024 || this.limits.rows > 10_000 || this.limits.timeout > 300_000 || this.limits.request < Math.ceil(this.limits.record / 3) * 4 + 4096 || this.limits.response < Math.ceil(this.limits.record / 3) * 4 + 4096) throw new RangeError("Edge HTTP limits exceed supported bounds.");
  }
  async beginSnapshot(scope: EdgeScope, signal?: AbortSignal): Promise<EdgeRemoteSnapshot> {
    const value = object(await this.request("POST", "snapshots" + this.query(scope), undefined, signal));
    return { id: uuid(value.id), position: integer(value.position) };
  }
  async *readSnapshot(scope: EdgeScope, snapshot: EdgeRemoteSnapshot, signal?: AbortSignal): AsyncIterable<readonly EdgeRecord[]> {
    let after: string | null = null;
    try {
      let count = 0;
      do {
        if (++count > 100_000) throw new RangeError("Remote snapshot page limit exceeded.");
        const value = object(await this.request("GET", `snapshots/${uuid(snapshot.id)}${this.query(scope)}&limit=${this.limits.rows}${after === null ? "" : "&after=" + encodeURIComponent(after)}`, undefined, signal));
        const records = this.records(value.records);
        const next = value.nextAfterId === null ? null : text(value.nextAfterId);
        if (next !== null && (!records.length || next === after)) throw new TypeError("Remote snapshot cursor did not advance.");
        yield records; after = next;
      } while (after !== null);
    } finally { await this.request("DELETE", `snapshots/${uuid(snapshot.id)}${this.query(scope)}`) }
  }
  async readChanges(scope: EdgeScope, position: string, maximum = this.limits.rows, signal?: AbortSignal): Promise<EdgeRemoteChanges | null> {
    if (!Number.isSafeInteger(maximum) || maximum <= 0 || maximum > this.limits.rows) throw new RangeError("Change row limit exceeded.");
    const result = await this.request("GET", `changes${this.query(scope)}&after=${integer(position)}&limit=${maximum}`, undefined, signal);
    if (result === null) return null;
    const value = object(result); return { fromPosition: integer(value.fromPosition), toPosition: integer(value.toPosition), records: this.records(value.records) };
  }
  async applyMutation(mutation: EdgeMutation, signal?: AbortSignal): Promise<EdgeOutcome> {
    const value = object(await this.request("POST", "mutations" + this.query(mutation.scope), {
      id: uuid(mutation.id), documentId: mutation.documentId, expectedRevision: integer(mutation.expectedRevision), kind: mutation.kind,
      payload: encodePayload(mutation.payload, this.limits.record)
    }, signal));
    if (value.kind !== "applied" && value.kind !== "conflict") throw new TypeError("Invalid mutation outcome.");
    return { kind: value.kind, record: value.record === null ? null : this.record(value.record) };
  }
  /** Call only after the outcome is durable locally; retry a lost confirmation response with the same mutation. */
  async finalizeMutationReceipt(mutation: EdgeMutation, signal?: AbortSignal): Promise<void> {
    const result = await this.request("POST", "mutations/confirm" + this.query(mutation.scope), {
      id: uuid(mutation.id), documentId: mutation.documentId, expectedRevision: integer(mutation.expectedRevision), kind: mutation.kind,
      payload: encodePayload(mutation.payload, this.limits.record)
    }, signal);
    if (result !== null) throw new TypeError("Mutation confirmation must return no content.");
  }
  /** Advance only after every outcome in the prefix was durably acknowledged and confirmed. */
  async advanceOrderedReceiptHorizon(scope: EdgeScope, throughId: string, maxReceipts = 1000, signal?: AbortSignal): Promise<void> {
    const parsed = parseOrderedMutationId(throughId);
    if (!parsed || parsed.sequence === "0" || !Number.isSafeInteger(maxReceipts) || maxReceipts < 1 || maxReceipts > 10_000) throw new TypeError("An ordered identity and bounded receipt count are required.");
    const result = await this.request("POST", "mutations/horizon" + this.query(scope), { throughId, maxReceipts }, signal);
    if (result !== null) throw new TypeError("Ordered receipt horizon must return no content.");
  }
  private record(value: unknown): EdgeRecord {
    const row = object(value); if (typeof row.deleted !== "boolean") throw new TypeError("Invalid deletion state.");
    return { id: text(row.id), revision: integer(row.revision), payload: decodePayload(row.payload, this.limits.record), deleted: row.deleted };
  }
  private records(value: unknown): readonly EdgeRecord[] {
    if (!Array.isArray(value) || value.length > this.limits.rows) throw new RangeError("Remote record count exceeded.");
    return value.map(row => this.record(row));
  }
  private query(scope: EdgeScope): string {
    return `?tenant=${encodeURIComponent(scope.tenant)}&scope=${encodeURIComponent(scope.id)}&epoch=${integer(scope.epoch)}`;
  }
  private async request(method: string, path: string, body?: unknown, signal?: AbortSignal): Promise<unknown> {
    const controller = new AbortController(); const cancel = () => controller.abort(signal?.reason);
    if (signal?.aborted) cancel(); else signal?.addEventListener("abort", cancel, { once: true });
    const timer = setTimeout(() => controller.abort(new Error("Edge request timed out.")), this.limits.timeout);
    try {
      const token = await this.options.bearerToken(); if (!token || /[\r\n]/.test(token)) throw new TypeError("A current bearer token is required.");
      const payload = body === undefined ? undefined : JSON.stringify(body);
      if (payload !== undefined && encoder.encode(payload).length > this.limits.request) throw new RangeError("HTTP request byte limit exceeded.");
      const response = await (this.options.fetch ?? globalThis.fetch)(this.endpoint + "/" + path, {
        method, credentials: "omit", headers: { Authorization: "Bearer " + token, "Content-Type": "application/json" },
        ...(payload === undefined ? {} : { body: payload }), signal: controller.signal
      });
      if (!response.ok) { await response.body?.cancel(); throw new EdgeHttpError(response.status) }
      if (response.status === 204) return null;
      if (Number(response.headers.get("content-length")) > this.limits.response) throw new RangeError("HTTP response byte limit exceeded.");
      const reader = response.body?.getReader(); if (!reader) throw new TypeError("Edge response body is missing.");
      let bytes = 0; const chunks: Uint8Array[] = [];
      try {
        while (true) { const block = await reader.read(); if (block.done) break; bytes += block.value.length; if (bytes > this.limits.response) throw new RangeError("HTTP response byte limit exceeded."); chunks.push(block.value) }
      } finally { await reader.cancel(); reader.releaseLock() }
      const joined = new Uint8Array(bytes); let at = 0; for (const chunk of chunks) { joined.set(chunk, at); at += chunk.length }
      return JSON.parse(decoder.decode(joined)) as unknown;
    } finally { clearTimeout(timer); signal?.removeEventListener("abort", cancel) }
  }
}

/** Run one bounded reconnect pass. The host serializes passes for a local scope and handles explicit resnapshot/epoch policy. */
export async function synchronizeEdge(store: IndexedDbEdgeStore, remote: EdgeHttpRemoteTransport, scope: EdgeScope,
  options: { maxPushes?: number; maxChangeBatches?: number; signal?: AbortSignal } = {}): Promise<void> {
  const pushes = options.maxPushes ?? 32; const batches = options.maxChangeBatches ?? 32;
  if (!Number.isSafeInteger(pushes) || pushes <= 0 || pushes > 1000 || !Number.isSafeInteger(batches) || batches <= 0 || batches > 1000) throw new RangeError("Synchronization pass limits exceeded.");
  if (!(await store.checkpoint(scope)).ready) {
    const snapshot = await remote.beginSnapshot(scope, options.signal); await store.beginSnapshot(scope, snapshot.id, snapshot.position);
    for await (const records of remote.readSnapshot(scope, snapshot, options.signal)) await store.applySnapshot(scope, snapshot.id, records);
    await store.commitSnapshot(scope, snapshot.id);
  }
  await flushOrderedReceipts(store, remote, scope, pushes, options.signal);
  const claimed = await store.claimOrderedBatch(scope, pushes);
  if (claimed !== null) {
    const acknowledged: EdgeAcknowledgement[] = [];
    try {
      for (const lease of claimed) {
        options.signal?.throwIfAborted();
        acknowledged.push({ lease, outcome: await remote.applyMutation(lease.mutation, options.signal) });
      }
    } finally {
      let committed = false;
      try { if (acknowledged.length) await store.acknowledgeBatch(acknowledged); committed = true }
      finally {
        const unprocessed = committed ? claimed.slice(acknowledged.length) : claimed;
        if (unprocessed.length) await store.releaseOrderedBatch(unprocessed);
      }
    }
  } else {
    for (let i = 0; i < pushes; i++) { options.signal?.throwIfAborted(); const lease = await store.claim(scope); if (!lease) break; await store.acknowledge(lease, await remote.applyMutation(lease.mutation, options.signal)) }
  }
  await flushOrderedReceipts(store, remote, scope, pushes, options.signal);
  for (let i = 0; i < batches; i++) { options.signal?.throwIfAborted(); const checkpoint = await store.checkpoint(scope); const changes = await remote.readChanges(scope, checkpoint.position, undefined, options.signal); if (!changes) break; await store.applyChanges(scope, changes.fromPosition, changes.toPosition, changes.records) }
}

/** Drain a bounded durable confirmation outbox. Retrying after a lost response reuses the original mutation and horizon. */
export async function flushOrderedReceipts(store: IndexedDbEdgeStore, remote: EdgeHttpRemoteTransport, scope: EdgeScope,
  maximum = 32, signal?: AbortSignal): Promise<void> {
  if (!Number.isSafeInteger(maximum) || maximum < 1 || maximum > 1000) throw new RangeError("Ordered receipt flush limit exceeded.");
  signal?.throwIfAborted();
  const pending = await store.nextUnconfirmedOrderedReceiptBatch(scope, maximum);
  const confirmed: EdgeMutation[] = [];
  try {
    for (const mutation of pending) {
      signal?.throwIfAborted();
      await remote.finalizeMutationReceipt(mutation, signal);
      confirmed.push(mutation);
    }
  } finally {
    // A remote failure or cancellation cannot discard the successful prefix.
    // A failed local commit leaves the original durable outbox replayable.
    if (confirmed.length) await store.markOrderedReceiptConfirmedBatch(confirmed);
  }
  const through = await store.confirmedOrderedHorizon(scope, maximum);
  if (through !== null) {
    await remote.advanceOrderedReceiptHorizon(scope, through, maximum, signal);
    await store.markOrderedHorizon(scope, through);
  }
}
