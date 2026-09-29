export interface EdgeScope { readonly tenant: string; readonly id: string; readonly epoch: string }
export * from "./http.js";
export interface EdgeRecord { readonly id: string; readonly revision: string; readonly payload: string; readonly deleted: boolean }
export interface EdgeMutation { readonly scope: EdgeScope; readonly id: string; readonly documentId: string; readonly expectedRevision: string; readonly kind: "upsert" | "delete"; readonly payload: string }
export interface EdgeLease { readonly mutation: EdgeMutation; readonly fence: number; readonly expiresAt: number }
export interface EdgeOutcome { readonly kind: "applied" | "conflict"; readonly record: EdgeRecord | null }
export interface EdgeCachedRecord extends EdgeRecord { readonly pendingId: string | null; readonly pendingStatus: "pending" | "leased" | "conflict" | null }
export interface EdgePage { readonly items: readonly EdgeCachedRecord[]; readonly nextAfterId: string | null }
export interface EdgeCheckpoint { readonly position: string; readonly ready: boolean }
export interface EdgeOptions {
  readonly databaseName: string;
  readonly factory?: IDBFactory;
  readonly now?: () => number;
  readonly maxRecordBytes?: number;
  readonly maxCacheRecords?: number;
  readonly maxCacheBytes?: number;
  /** Separate snapshot staging reserve; allow up to twice the active payload during cutover. */
  readonly maxStagedRecords?: number;
  readonly maxStagedBytes?: number;
  readonly maxPendingRecords?: number;
  readonly maxPendingBytes?: number;
  readonly maxReceipts?: number;
  readonly maxPageRecords?: number;
  readonly maxPageBytes?: number;
  readonly maxBatchRecords?: number;
  readonly maxScopes?: number;
}
export class EdgeScopeError extends Error { constructor() { super("The offline scope epoch is inactive or missing.") } }
export class EdgeCapacityError extends Error { constructor() { super("The bounded offline cache, queue, scope or result capacity is full.") } }
export class EdgeRevisionError extends Error { constructor() { super("The offline document revision or checkpoint is incompatible.") } }
export class EdgeIdentityError extends Error { constructor() { super("The stable mutation identity or acknowledgement was reused inconsistently.") } }
export class EdgeLeaseError extends Error { constructor() { super("The mutation lease expired or its fence was replaced.") } }

type Status = "pending" | "leased" | "conflict";
interface ScopeState extends EdgeScope { position: string; ready: boolean; snapshotId: string | null; snapshotPosition: string | null }
interface StoredRecord extends EdgeRecord { scope: EdgeScope; fingerprint: string }
interface StagedRecord extends StoredRecord { snapshotId: string }
interface Pending extends EdgeMutation { status: Status; sequence: number; fingerprint: string; fence: number; leaseUntil: number }
interface Receipt { scope: EdgeScope; id: string; fingerprint: string; outcomeFingerprint: string; time: number; resolvedBy: string | null }
interface Totals { key: "totals"; cacheRows: number; cacheBytes: number; stagedRows: number; stagedBytes: number; pendingRows: number; pendingBytes: number; receiptRows: number; scopes: number; sequence: number }
const stores = ["scopes", "records", "staging", "mutations", "receipts", "metadata"] as const;
const encoder = new TextEncoder();
const maxInt64 = 9223372036854775807n;
function integer(value: string, positive = false): bigint {
  if (typeof value !== "string" || !/^(0|[1-9][0-9]*)$/.test(value)) throw new EdgeRevisionError();
  const parsed = BigInt(value);
  if (parsed > maxInt64 || positive && parsed === 0n) throw new EdgeRevisionError();
  return parsed;
}
function key(value: string, maxBytes: number): void {
  if (!value.trim() || value.includes("\0") || encoder.encode(value).byteLength > maxBytes) throw new TypeError("Invalid bounded Edge key.");
}
function scopeCopy(scope: EdgeScope): EdgeScope {
  key(scope.tenant, 256); key(scope.id, 256); integer(scope.epoch, true);
  return { tenant: scope.tenant, id: scope.id, epoch: scope.epoch };
}
function recordCopy(record: EdgeRecord): EdgeRecord {
  key(record.id, 512); integer(record.revision, true);
  if (typeof record.deleted !== "boolean") throw new EdgeRevisionError();
  if (record.deleted) { if (record.payload !== "") throw new EdgeRevisionError() }
  else jsonObject(record.payload);
  return { id: record.id, revision: record.revision, payload: record.payload, deleted: record.deleted };
}
function jsonObject(value: string): void {
  const parsed: unknown = JSON.parse(value);
  if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) throw new TypeError("Edge payloads must be JSON objects.");
}
function scopeKey(scope: EdgeScope): IDBValidKey[] { return [scope.tenant, scope.id, scope.epoch] }
function identity(scope: EdgeScope, id: string): IDBValidKey[] { return [...scopeKey(scope), id] }
function request<T>(operation: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => { operation.onsuccess = () => resolve(operation.result); operation.onerror = () => reject(operation.error) });
}
async function hash(value: string): Promise<string> {
  const bytes = await crypto.subtle.digest("SHA-256", encoder.encode(value));
  return Array.from(new Uint8Array(bytes), value => value.toString(16).padStart(2, "0")).join("");
}
function charge(record: EdgeRecord): number { return encoder.encode(record.payload).byteLength + encoder.encode(record.id).byteLength }
function blankTotals(): Totals { return { key: "totals", cacheRows: 0, cacheBytes: 0, stagedRows: 0, stagedBytes: 0, pendingRows: 0, pendingBytes: 0, receiptRows: 0, scopes: 0, sequence: 0 } }

/** Browser persistence adapter. Int64 revisions, epochs and checkpoints use decimal strings without precision loss. */
export class IndexedDbEdgeStore {
  private readonly db: IDBDatabase;
  private readonly now: () => number;
  private readonly limits: Required<Omit<EdgeOptions, "factory" | "now" | "databaseName">>;
  private constructor(db: IDBDatabase, options: EdgeOptions) {
    this.db = db; this.now = options.now ?? Date.now;
    this.limits = { maxRecordBytes: options.maxRecordBytes ?? 512 * 1024, maxCacheRecords: options.maxCacheRecords ?? 100_000,
      maxCacheBytes: options.maxCacheBytes ?? 256 * 1024 * 1024, maxStagedRecords: options.maxStagedRecords ?? 100_000,
      maxStagedBytes: options.maxStagedBytes ?? 256 * 1024 * 1024, maxPendingRecords: options.maxPendingRecords ?? 10_000,
      maxPendingBytes: options.maxPendingBytes ?? 32 * 1024 * 1024, maxReceipts: options.maxReceipts ?? 100_000,
      maxPageRecords: options.maxPageRecords ?? 1000, maxPageBytes: options.maxPageBytes ?? 8 * 1024 * 1024,
      maxBatchRecords: options.maxBatchRecords ?? 512, maxScopes: options.maxScopes ?? 256 };
    for (const value of Object.values(this.limits)) if (!Number.isSafeInteger(value) || value <= 0) throw new TypeError("Edge limits must be positive safe integers.");
    if (this.limits.maxRecordBytes > this.limits.maxCacheBytes || this.limits.maxRecordBytes > this.limits.maxStagedBytes || this.limits.maxRecordBytes > this.limits.maxPendingBytes || this.limits.maxRecordBytes > this.limits.maxPageBytes) throw new EdgeCapacityError();
  }
  static async open(options: EdgeOptions): Promise<IndexedDbEdgeStore> {
    key(options.databaseName, 512);
    const factory = options.factory ?? globalThis.indexedDB;
    if (!factory) throw new Error("IndexedDB is unavailable.");
    const opening = factory.open(options.databaseName, 2);
    opening.onupgradeneeded = () => {
      const db = opening.result;
      if (!db.objectStoreNames.contains("scopes")) db.createObjectStore("scopes", { keyPath: ["tenant", "id"] });
      for (const name of ["records", "staging"] as const) {
        if (!db.objectStoreNames.contains(name)) {
          const path = name === "staging" ? ["scope.tenant", "scope.id", "scope.epoch", "snapshotId", "id"] : ["scope.tenant", "scope.id", "scope.epoch", "id"];
          const store = db.createObjectStore(name, { keyPath: path });
          store.createIndex("scope", ["scope.tenant", "scope.id", "scope.epoch"]);
          store.createIndex("base", ["scope.tenant", "scope.id"]);
        }
      }
      if (!db.objectStoreNames.contains("mutations")) {
        const store = db.createObjectStore("mutations", { keyPath: ["scope.tenant", "scope.id", "scope.epoch", "id"] });
        store.createIndex("scope", ["scope.tenant", "scope.id", "scope.epoch"]);
        store.createIndex("base", ["scope.tenant", "scope.id"]);
        store.createIndex("document", ["scope.tenant", "scope.id", "scope.epoch", "documentId"], { unique: true });
        store.createIndex("claim", ["scope.tenant", "scope.id", "scope.epoch", "status", "sequence"]);
        store.createIndex("expiry", ["scope.tenant", "scope.id", "scope.epoch", "status", "leaseUntil"]);
      }
      if (!db.objectStoreNames.contains("receipts")) {
        const store = db.createObjectStore("receipts", { keyPath: ["scope.tenant", "scope.id", "scope.epoch", "id"] });
        store.createIndex("base", ["scope.tenant", "scope.id"]);
        store.createIndex("time", "time");
      }
      if (!db.objectStoreNames.contains("metadata")) db.createObjectStore("metadata", { keyPath: "key" }).put(blankTotals());
    };
    const db = await request(opening);
    db.onversionchange = () => db.close();
    try { const store = new IndexedDbEdgeStore(db, options); await store.migrateTotals(); return store } catch (error) { db.close(); throw error }
  }
  close(): void { this.db.close() }
  private async transact<T>(mode: IDBTransactionMode, action: (tx: IDBTransaction) => Promise<T>): Promise<T> {
    const tx = this.db.transaction(stores, mode, { durability: "strict" });
    const complete = new Promise<void>((resolve, reject) => { tx.oncomplete = () => resolve(); tx.onabort = () => reject(tx.error ?? new Error("Edge transaction aborted.")); tx.onerror = () => {} });
    // Handle rejection immediately: IndexedDB can abort before the action's request promise resumes.
    void complete.catch(() => {});
    try { const value = await action(tx); await complete; return value }
    catch (error) { try { tx.abort() } catch { /* Already terminal. */ } await complete.catch(() => {}); throw error }
  }
  private async active(tx: IDBTransaction, scope: EdgeScope): Promise<ScopeState> {
    const state = await request<ScopeState | undefined>(tx.objectStore("scopes").get([scope.tenant, scope.id]));
    if (!state || state.epoch !== scope.epoch) throw new EdgeScopeError();
    return state;
  }
  private async totals(tx: IDBTransaction): Promise<Totals> { return await request<Totals | undefined>(tx.objectStore("metadata").get("totals")) ?? blankTotals() }
  private async migrateTotals(): Promise<void> {
    await this.transact("readwrite", async tx => {
      const totals = await this.totals(tx);
      if (Number.isSafeInteger(totals.stagedRows) && Number.isSafeInteger(totals.stagedBytes)) return;
      totals.stagedRows = 0; totals.stagedBytes = 0;
      const cursorRequest = tx.objectStore("staging").openCursor();
      for (let cursor = await request(cursorRequest); cursor; ) {
        totals.stagedRows++; totals.stagedBytes += charge(cursor.value as StagedRecord);
        const next = request(cursorRequest); cursor.continue(); cursor = await next;
      }
      tx.objectStore("metadata").put(totals);
    });
  }
  private budget(totals: Totals): void {
    if (totals.cacheRows - totals.stagedRows > this.limits.maxCacheRecords || totals.cacheBytes - totals.stagedBytes > this.limits.maxCacheBytes ||
      totals.stagedRows > this.limits.maxStagedRecords || totals.stagedBytes > this.limits.maxStagedBytes || totals.pendingRows > this.limits.maxPendingRecords ||
      totals.pendingBytes > this.limits.maxPendingBytes || totals.receiptRows > this.limits.maxReceipts || totals.scopes > this.limits.maxScopes) throw new EdgeCapacityError();
  }
  async activate(scopeInput: EdgeScope, policy: "reject" | "discard" = "reject"): Promise<void> {
    const scope = scopeCopy(scopeInput);
    if (policy !== "reject" && policy !== "discard") throw new TypeError("Invalid epoch policy.");
    await this.transact("readwrite", async tx => {
      const previous = await request<ScopeState | undefined>(tx.objectStore("scopes").get([scope.tenant, scope.id]));
      if (previous && integer(previous.epoch) > integer(scope.epoch)) throw new EdgeScopeError();
      if (previous?.epoch === scope.epoch) return;
      const totals = await this.totals(tx);
      if (previous) {
        const range = IDBKeyRange.only([scope.tenant, scope.id]);
        const pending = await request<Pending[]>(tx.objectStore("mutations").index("base").getAll(range));
        if (pending.length && policy === "reject") throw new EdgeRevisionError();
        for (const name of ["records", "staging", "mutations", "receipts"] as const) {
          const values = await request<Array<StoredRecord | StagedRecord | Pending | Receipt>>(tx.objectStore(name).index("base").getAll(range));
          for (const value of values) {
            if (name === "records" || name === "staging") {
              totals.cacheRows--; totals.cacheBytes -= charge(value as StoredRecord);
              if (name === "staging") { totals.stagedRows--; totals.stagedBytes -= charge(value as StagedRecord) }
            }
            else if (name === "mutations") { totals.pendingRows--; totals.pendingBytes -= encoder.encode((value as Pending).payload).byteLength }
            else totals.receiptRows--;
            const id = name === "staging" ? [...scopeKey(value.scope), (value as StagedRecord).snapshotId, value.id] : identity(value.scope, value.id);
            tx.objectStore(name).delete(id);
          }
        }
      } else totals.scopes++;
      this.budget(totals);
      tx.objectStore("scopes").put({ ...scope, position: "0", ready: false, snapshotId: null, snapshotPosition: null } satisfies ScopeState);
      tx.objectStore("metadata").put(totals);
    });
  }
  async checkpoint(scopeInput: EdgeScope): Promise<EdgeCheckpoint> {
    const scope = scopeCopy(scopeInput);
    return await this.transact("readonly", async tx => { const state = await this.active(tx, scope); return { position: state.position, ready: state.ready } });
  }
  async get(scopeInput: EdgeScope, id: string): Promise<EdgeCachedRecord | null> {
    const scope = scopeCopy(scopeInput); key(id, 512);
    return await this.transact("readonly", async tx => { await this.active(tx, scope); return await this.cached(tx, scope, id) });
  }
  private async cached(tx: IDBTransaction, scope: EdgeScope, id: string): Promise<EdgeCachedRecord | null> {
    const record = await request<StoredRecord | undefined>(tx.objectStore("records").get(identity(scope, id)));
    const pending = await request<Pending | undefined>(tx.objectStore("mutations").index("document").get(identity(scope, id)));
    if (!record && !pending) return null;
    return { id, revision: record?.revision ?? "0", payload: pending?.payload ?? record!.payload, deleted: pending ? pending.kind === "delete" : record!.deleted, pendingId: pending?.id ?? null, pendingStatus: pending?.status ?? null };
  }
  async page(scopeInput: EdgeScope, count = 100, afterId: string | null = null): Promise<EdgePage> {
    const scope = scopeCopy(scopeInput);
    if (!Number.isSafeInteger(count) || count < 1 || count > this.limits.maxPageRecords) throw new EdgeCapacityError();
    if (afterId !== null) key(afterId, 512);
    return await this.transact("readonly", async tx => {
      await this.active(tx, scope);
      // Merge two ordered cursors instead of loading all cached keys or pending payloads into memory.
      const range = IDBKeyRange.bound([...scopeKey(scope), afterId ?? ""], [...scopeKey(scope), []], afterId !== null, true);
      const recordRequest = tx.objectStore("records").openCursor(range);
      const pendingRequest = tx.objectStore("mutations").index("document").openCursor(range);
      let [recordCursor, pendingCursor] = await Promise.all([request(recordRequest), request(pendingRequest)]);
      const items: EdgeCachedRecord[] = []; let bytes = 0; let more = false;
      while (recordCursor || pendingCursor) {
        const record = recordCursor?.value as StoredRecord | undefined;
        const pending = pendingCursor?.value as Pending | undefined;
        const takeRecord = !!record && (!pending || record.id <= pending.documentId);
        const takePending = !!pending && (!record || pending.documentId <= record.id);
        const id = takeRecord ? record!.id : pending!.documentId;
        const value: EdgeCachedRecord = { id, revision: takeRecord ? record!.revision : "0", payload: takePending ? pending!.payload : record!.payload,
          deleted: takePending ? pending!.kind === "delete" : record!.deleted, pendingId: takePending ? pending!.id : null, pendingStatus: takePending ? pending!.status : null };
        if (value.deleted) {
          if (takeRecord) { const next = request(recordRequest); recordCursor!.continue(); recordCursor = await next }
          if (takePending) { const next = request(pendingRequest); pendingCursor!.continue(); pendingCursor = await next }
          continue;
        }
        const cost = charge(value);
        if (items.length === count || bytes + cost > this.limits.maxPageBytes) { more = true; break }
        items.push(value); bytes += cost;
        if (takeRecord) { const next = request(recordRequest); recordCursor!.continue(); recordCursor = await next }
        if (takePending) { const next = request(pendingRequest); pendingCursor!.continue(); pendingCursor = await next }
      }
      if (more && !items.length) throw new EdgeCapacityError();
      return { items, nextAfterId: more ? items[items.length - 1]!.id : null };
    });
  }
  private batch(records: readonly EdgeRecord[]): EdgeRecord[] {
    if (records.length > this.limits.maxBatchRecords) throw new EdgeCapacityError();
    const ids = new Set<string>(); const values = records.map(recordCopy); let bytes = 0;
    for (const value of values) { if (ids.has(value.id) || encoder.encode(value.payload).byteLength > this.limits.maxRecordBytes) throw new EdgeCapacityError(); ids.add(value.id); bytes += charge(value) }
    if (bytes > this.limits.maxCacheBytes) throw new EdgeCapacityError();
    return values;
  }
  private async writeRecord(tx: IDBTransaction, scope: EdgeScope, record: EdgeRecord, fingerprint: string, totals: Totals, snapshotId: string | null): Promise<void> {
    const store = tx.objectStore(snapshotId === null ? "records" : "staging");
    const id = snapshotId === null ? identity(scope, record.id) : [...scopeKey(scope), snapshotId, record.id];
    const previous = await request<StoredRecord | undefined>(store.get(id));
    if (previous) {
      if (snapshotId !== null && previous.revision !== record.revision || previous.revision === record.revision && previous.fingerprint !== fingerprint) throw new EdgeRevisionError();
      if (integer(previous.revision) >= integer(record.revision)) return;
      totals.cacheBytes -= charge(previous);
      if (snapshotId !== null) totals.stagedBytes -= charge(previous);
    } else { totals.cacheRows++; if (snapshotId !== null) totals.stagedRows++ }
    totals.cacheBytes += charge(record);
    if (snapshotId !== null) totals.stagedBytes += charge(record);
    this.budget(totals);
    if (snapshotId === null) store.put({ ...record, scope, fingerprint } satisfies StoredRecord);
    else store.put({ ...record, scope, fingerprint, snapshotId } satisfies StagedRecord);
  }
  async beginSnapshot(scopeInput: EdgeScope, id: string, position: string): Promise<void> {
    const scope = scopeCopy(scopeInput); key(id, 512); integer(position);
    await this.transact("readwrite", async tx => {
      const state = await this.active(tx, scope);
      if (integer(position) < integer(state.position) || state.snapshotId === id && state.snapshotPosition !== position) throw new EdgeRevisionError();
      const totals = await this.totals(tx);
      const rows = await request<StagedRecord[]>(tx.objectStore("staging").index("scope").getAll(IDBKeyRange.only(scopeKey(scope))));
      for (const row of rows) if (row.snapshotId !== id) { tx.objectStore("staging").delete([...scopeKey(scope), row.snapshotId, row.id]); totals.cacheRows--; totals.cacheBytes -= charge(row); totals.stagedRows--; totals.stagedBytes -= charge(row) }
      state.snapshotId = id; state.snapshotPosition = position;
      tx.objectStore("scopes").put(state); tx.objectStore("metadata").put(totals);
    });
  }
  async applySnapshot(scopeInput: EdgeScope, id: string, records: readonly EdgeRecord[]): Promise<void> {
    const scope = scopeCopy(scopeInput); const values = this.batch(records);
    const fingerprints = await Promise.all(values.map(value => hash(JSON.stringify([value.deleted, value.payload]))));
    await this.transact("readwrite", async tx => {
      if ((await this.active(tx, scope)).snapshotId !== id) throw new EdgeScopeError();
      const totals = await this.totals(tx);
      for (let index = 0; index < values.length; index++) await this.writeRecord(tx, scope, values[index]!, fingerprints[index]!, totals, id);
      tx.objectStore("metadata").put(totals);
    });
  }
  async commitSnapshot(scopeInput: EdgeScope, id: string): Promise<void> {
    const scope = scopeCopy(scopeInput);
    await this.transact("readwrite", async tx => {
      const state = await this.active(tx, scope);
      if (state.snapshotId !== id || state.snapshotPosition === null || integer(state.position) > integer(state.snapshotPosition)) throw new EdgeScopeError();
      const totals = await this.totals(tx);
      const previous = await request<StoredRecord[]>(tx.objectStore("records").index("scope").getAll(IDBKeyRange.only(scopeKey(scope))));
      for (const row of previous) { tx.objectStore("records").delete(identity(scope, row.id)); totals.cacheRows--; totals.cacheBytes -= charge(row) }
      const rows = await request<StagedRecord[]>(tx.objectStore("staging").index("scope").getAll(IDBKeyRange.only(scopeKey(scope))));
      for (const row of rows) {
        if (row.snapshotId !== id) throw new EdgeScopeError();
        tx.objectStore("records").put({ scope, id: row.id, revision: row.revision, payload: row.payload, deleted: row.deleted, fingerprint: row.fingerprint } satisfies StoredRecord);
        tx.objectStore("staging").delete([...scopeKey(scope), id, row.id]);
        totals.stagedRows--; totals.stagedBytes -= charge(row);
      }
      this.budget(totals);
      state.position = state.snapshotPosition; state.snapshotPosition = null; state.snapshotId = null; state.ready = true;
      tx.objectStore("scopes").put(state); tx.objectStore("metadata").put(totals);
    });
  }
  async applyChanges(scopeInput: EdgeScope, from: string, to: string, records: readonly EdgeRecord[]): Promise<void> {
    const scope = scopeCopy(scopeInput); if (integer(to) <= integer(from)) throw new EdgeRevisionError();
    const values = this.batch(records); const fingerprints = await Promise.all(values.map(value => hash(JSON.stringify([value.deleted, value.payload]))));
    await this.transact("readwrite", async tx => {
      const state = await this.active(tx, scope);
      if (!state.ready || state.position !== from || state.snapshotId !== null) throw new EdgeRevisionError();
      const totals = await this.totals(tx);
      for (let index = 0; index < values.length; index++) await this.writeRecord(tx, scope, values[index]!, fingerprints[index]!, totals, null);
      state.position = to; tx.objectStore("scopes").put(state); tx.objectStore("metadata").put(totals);
    });
  }
  private async prepared(input: EdgeMutation): Promise<EdgeMutation & { fingerprint: string }> {
    const scope = scopeCopy(input.scope); key(input.id, 512); key(input.documentId, 512); integer(input.expectedRevision);
    if (input.kind === "upsert") jsonObject(input.payload); else if (input.kind !== "delete" || input.payload !== "") throw new EdgeRevisionError();
    if (encoder.encode(input.payload).byteLength > this.limits.maxRecordBytes) throw new EdgeCapacityError();
    const mutation: EdgeMutation = { scope, id: input.id, documentId: input.documentId, expectedRevision: input.expectedRevision, kind: input.kind, payload: input.payload };
    return { ...mutation, fingerprint: await hash(JSON.stringify([scope.tenant, scope.id, scope.epoch, mutation.documentId, mutation.expectedRevision, mutation.kind, mutation.payload])) };
  }
  async enqueue(input: EdgeMutation): Promise<void> {
    const mutation = await this.prepared(input);
    await this.transact("readwrite", async tx => {
      if (!(await this.active(tx, mutation.scope)).ready) throw new EdgeScopeError();
      const totals = await this.totals(tx); await this.enqueuePrepared(tx, mutation, totals); tx.objectStore("metadata").put(totals);
    });
  }
  private async enqueuePrepared(tx: IDBTransaction, mutation: EdgeMutation & { fingerprint: string }, totals: Totals): Promise<void> {
    const id = identity(mutation.scope, mutation.id);
    const existing = await request<Pending | undefined>(tx.objectStore("mutations").get(id)) ?? await request<Receipt | undefined>(tx.objectStore("receipts").get(id));
    if (existing) { if (existing.fingerprint !== mutation.fingerprint) throw new EdgeIdentityError(); return }
    const record = await request<StoredRecord | undefined>(tx.objectStore("records").get(identity(mutation.scope, mutation.documentId)));
    if ((record?.revision ?? "0") !== mutation.expectedRevision || await request(tx.objectStore("mutations").index("document").get(identity(mutation.scope, mutation.documentId)))) throw new EdgeRevisionError();
    totals.pendingRows++; totals.pendingBytes += encoder.encode(mutation.payload).byteLength;
    if (!Number.isSafeInteger(++totals.sequence)) throw new EdgeCapacityError();
    this.budget(totals);
    tx.objectStore("mutations").put({ ...mutation, sequence: totals.sequence, status: "pending", fence: 0, leaseUntil: 0 } satisfies Pending);
  }
  async claim(scopeInput: EdgeScope, leaseMilliseconds = 60_000): Promise<EdgeLease | null> {
    const scope = scopeCopy(scopeInput);
    if (!Number.isSafeInteger(leaseMilliseconds) || leaseMilliseconds < 1000 || leaseMilliseconds > 3_600_000) throw new TypeError("Invalid bounded lease duration.");
    return await this.transact("readwrite", async tx => {
      if ((await this.active(tx, scope)).snapshotId !== null) throw new EdgeScopeError();
      const store = tx.objectStore("mutations"); const now = this.now();
      const pending = await request<Pending[]>(store.index("claim").getAll(IDBKeyRange.bound([...scopeKey(scope), "pending", 0], [...scopeKey(scope), "pending", Number.MAX_SAFE_INTEGER]), 1));
      const expired = await request<Pending[]>(store.index("expiry").getAll(IDBKeyRange.bound([...scopeKey(scope), "leased", 0], [...scopeKey(scope), "leased", now]), 1));
      const candidate = [...pending, ...expired].sort((a, b) => a.sequence - b.sequence)[0];
      if (!candidate) return null;
      candidate.status = "leased"; candidate.leaseUntil = now + leaseMilliseconds;
      if (!Number.isSafeInteger(++candidate.fence)) throw new EdgeCapacityError();
      store.put(candidate);
      return { mutation: { scope: candidate.scope, id: candidate.id, documentId: candidate.documentId, expectedRevision: candidate.expectedRevision, kind: candidate.kind, payload: candidate.payload }, fence: candidate.fence, expiresAt: candidate.leaseUntil };
    });
  }
  async acknowledge(lease: EdgeLease, input: EdgeOutcome): Promise<void> {
    const mutation = await this.prepared(lease.mutation); const record = input.record === null ? null : recordCopy(input.record);
    if (input.kind !== "applied" && input.kind !== "conflict" || record && record.id !== mutation.documentId || input.kind === "applied" && (!record || integer(record.revision) <= integer(mutation.expectedRevision) || record.deleted !== (mutation.kind === "delete"))) throw new EdgeRevisionError();
    if (record && encoder.encode(record.payload).byteLength > this.limits.maxRecordBytes) throw new EdgeCapacityError();
    const recordFingerprint = record ? await hash(JSON.stringify([record.deleted, record.payload])) : null;
    const outcomeFingerprint = await hash(JSON.stringify([input.kind, record?.revision ?? "0", recordFingerprint]));
    await this.transact("readwrite", async tx => {
      if ((await this.active(tx, mutation.scope)).snapshotId !== null) throw new EdgeScopeError();
      const id = identity(mutation.scope, mutation.id); const receipts = tx.objectStore("receipts");
      const receipt = await request<Receipt | undefined>(receipts.get(id));
      if (receipt) { if (receipt.fingerprint !== mutation.fingerprint || receipt.outcomeFingerprint !== outcomeFingerprint) throw new EdgeIdentityError(); return }
      const pending = await request<Pending | undefined>(tx.objectStore("mutations").get(id));
      if (!pending || pending.fingerprint !== mutation.fingerprint || pending.status !== "leased" || pending.fence !== lease.fence || pending.leaseUntil <= this.now()) throw new EdgeLeaseError();
      const totals = await this.totals(tx);
      if (record) await this.writeRecord(tx, mutation.scope, record, recordFingerprint!, totals, null);
      else {
        const cached = await request<StoredRecord | undefined>(tx.objectStore("records").get(identity(mutation.scope, mutation.documentId)));
        if (cached && integer(cached.revision) <= integer(mutation.expectedRevision)) { tx.objectStore("records").delete(identity(mutation.scope, mutation.documentId)); totals.cacheRows--; totals.cacheBytes -= charge(cached) }
      }
      if (input.kind === "applied") { tx.objectStore("mutations").delete(id); totals.pendingRows--; totals.pendingBytes -= encoder.encode(pending.payload).byteLength }
      else { pending.status = "conflict"; pending.leaseUntil = 0; tx.objectStore("mutations").put(pending) }
      totals.receiptRows++; this.budget(totals);
      receipts.put({ scope: mutation.scope, id: mutation.id, fingerprint: mutation.fingerprint, outcomeFingerprint, time: this.now(), resolvedBy: null } satisfies Receipt);
      tx.objectStore("metadata").put(totals);
    });
  }
  async resolveConflict(conflictedId: string, replacementInput: EdgeMutation): Promise<void> {
    const replacement = await this.prepared(replacementInput);
    if (replacement.id === conflictedId) throw new EdgeIdentityError();
    await this.transact("readwrite", async tx => {
      await this.active(tx, replacement.scope);
      const oldId = identity(replacement.scope, conflictedId); const receipt = await request<Receipt | undefined>(tx.objectStore("receipts").get(oldId));
      const totals = await this.totals(tx);
      if (receipt?.resolvedBy) { if (receipt.resolvedBy !== replacement.id) throw new EdgeIdentityError(); await this.enqueuePrepared(tx, replacement, totals); return }
      const pending = await request<Pending | undefined>(tx.objectStore("mutations").get(oldId));
      if (!pending || pending.status !== "conflict" || pending.documentId !== replacement.documentId || !receipt) throw new EdgeRevisionError();
      tx.objectStore("mutations").delete(oldId); totals.pendingRows--; totals.pendingBytes -= encoder.encode(pending.payload).byteLength;
      await this.enqueuePrepared(tx, replacement, totals);
      receipt.resolvedBy = replacement.id; tx.objectStore("receipts").put(receipt); tx.objectStore("metadata").put(totals);
    });
  }
  async pruneReceipts(recordedBefore: number, maxRecords = 100): Promise<number> {
    if (!Number.isFinite(recordedBefore) || !Number.isSafeInteger(maxRecords) || maxRecords < 1 || maxRecords > 10_000) throw new TypeError("Invalid bounded receipt retention request.");
    return await this.transact("readwrite", async tx => {
      const rows = await request<Receipt[]>(tx.objectStore("receipts").index("time").getAll(IDBKeyRange.upperBound(recordedBefore, true), maxRecords));
      const totals = await this.totals(tx); let removed = 0;
      for (const row of rows) if (!await request(tx.objectStore("mutations").get(identity(row.scope, row.id)))) { tx.objectStore("receipts").delete(identity(row.scope, row.id)); totals.receiptRows--; removed++ }
      tx.objectStore("metadata").put(totals); return removed;
    });
  }
}
