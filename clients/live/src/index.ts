export type LiveKey = string | number;

export type LiveEventKind =
  | "InitialResult"
  | "RowAdded"
  | "RowUpdated"
  | "RowRemoved"
  | "ResultReordered"
  | "ResultReset";

export type LiveResetReason =
  | "ReplayExpired"
  | "DiffLimitExceeded"
  | "QueryShapeChanged"
  | "SchemaChanged"
  | "ServerRestart";

export interface LiveResultEvent<TRow, TKey extends LiveKey> {
  readonly sequence: number;
  readonly kind: LiveEventKind;
  readonly key: TKey | null;
  readonly row: TRow | null;
  readonly previousIndex: number | null;
  readonly currentIndex: number | null;
  readonly rows: readonly TRow[] | null;
  readonly order: readonly TKey[] | null;
  readonly resetReason: LiveResetReason | null;
}

export type LiveSubscriberMessageKind = "Event" | "ResetRequired";

export interface LiveTransportMessage<TRow, TKey extends LiveKey> {
  readonly kind: LiveSubscriberMessageKind;
  readonly sequence: number | null;
  readonly resumeToken: string | null;
  readonly event: LiveResultEvent<TRow, TKey> | null;
}

export interface LiveSubscriptionRequest<TParameters extends object> {
  readonly query: string;
  readonly parameters: TParameters;
  readonly resumeToken?: string;
}

export type LiveClientParameterType =
  | "string"
  | "boolean"
  | "byte"
  | "sbyte"
  | "int16"
  | "uint16"
  | "int32"
  | "uint32"
  | "int64"
  | "uint64"
  | "single"
  | "double"
  | "decimal"
  | "guid"
  | "date"
  | "time"
  | "timestamp"
  | "timestamptz";

export interface LiveClientParameter {
  readonly type: LiveClientParameterType;
  readonly allowNull?: boolean;
  readonly value: unknown;
}

export type LiveClientParameters = Readonly<Record<string, LiveClientParameter>>;

export type LiveClientFilterOperator =
  | "Equal"
  | "NotEqual"
  | "LessThan"
  | "LessThanOrEqual"
  | "GreaterThan"
  | "GreaterThanOrEqual"
  | "StartsWith"
  | "Contains"
  | "IsNull"
  | "IsNotNull";

export interface LiveClientFilter {
  readonly column: string;
  readonly operator: LiveClientFilterOperator;
  readonly parameter?: string;
}

export type LiveClientSortDirection = "Ascending" | "Descending";

export interface LiveClientOrdering {
  readonly column: string;
  readonly direction: LiveClientSortDirection;
}

export interface LiveClientLinqDocument {
  readonly schema: string;
  readonly table: string;
  readonly columns: readonly string[];
  readonly filters: readonly LiveClientFilter[];
  readonly orderings: readonly LiveClientOrdering[];
}

export interface LiveSqlClientQueryDocument {
  readonly language: "sql";
  readonly sql: string;
  readonly keyColumns: readonly string[];
  readonly maximumResultCount: number;
  readonly parameters: LiveClientParameters;
}

export interface LiveLinqClientQueryDocument {
  readonly language: "linq";
  readonly linq: LiveClientLinqDocument;
  readonly keyColumns: readonly string[];
  readonly maximumResultCount: number;
  readonly parameters: LiveClientParameters;
}

export type LiveClientQueryDocument =
  | LiveSqlClientQueryDocument
  | LiveLinqClientQueryDocument;

export interface LiveClientRow {
  readonly values: Readonly<Record<string, unknown>>;
  readonly fingerprint: string;
}

export type LiveConnectionPhase =
  | "idle"
  | "connecting"
  | "live"
  | "reconnecting"
  | "stopped"
  | "faulted";

export interface LiveQueryState<TRow> {
  readonly phase: LiveConnectionPhase;
  readonly rows: readonly TRow[];
  readonly lastSequence: number;
  readonly error: Error | null;
}

export interface LiveClientOptions {
  readonly endpoint: string;
  readonly fetch?: typeof globalThis.fetch;
  readonly headers?: Readonly<Record<string, string>>;
  readonly credentials?: RequestCredentials;
  readonly initialRetryDelayMs?: number;
  readonly maximumRetryDelayMs?: number;
  readonly retryJitter?: number;
  readonly random?: () => number;
  readonly onResumeToken?: (token: string | null) => void;
}

export class LiveProtocolError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "LiveProtocolError";
  }
}

export class LiveHttpError extends Error {
  readonly status: number;
  readonly retryable: boolean;

  constructor(status: number, retryable: boolean) {
    super(`BlueTusk Live endpoint returned HTTP ${status}.`);
    this.name = "LiveHttpError";
    this.status = status;
    this.retryable = retryable;
  }
}

class LiveResetRequiredError extends Error {
  constructor() {
    super("The server requires an authoritative result reset.");
    this.name = "LiveResetRequiredError";
  }
}

export class LiveResultStore<TRow, TKey extends LiveKey> {
  readonly #rows = new Map<TKey, TRow>();
  #order: TKey[] = [];

  get rows(): readonly TRow[] {
    return this.#order.map((key) => {
      const row = this.#rows.get(key);
      if (row === undefined) {
        throw new LiveProtocolError(`Live result order references missing key '${String(key)}'.`);
      }

      return row;
    });
  }

  apply(event: LiveResultEvent<TRow, TKey>): readonly TRow[] {
    switch (event.kind) {
      case "InitialResult":
      case "ResultReset":
        this.#replace(event);
        break;
      case "RowAdded":
        this.#add(event);
        break;
      case "RowUpdated":
        this.#update(event);
        break;
      case "RowRemoved":
        this.#remove(event);
        break;
      case "ResultReordered":
        this.#reorder(event);
        break;
      default:
        throw new LiveProtocolError(`Unknown Live event kind '${String(event.kind)}'.`);
    }

    return this.rows;
  }

  #replace(event: LiveResultEvent<TRow, TKey>): void {
    if (event.rows === null || event.order === null || event.rows.length !== event.order.length) {
      throw new LiveProtocolError(`${event.kind} must contain equally sized rows and order arrays.`);
    }

    const replacement = new Map<TKey, TRow>();
    event.order.forEach((key, index) => {
      if (replacement.has(key)) {
        throw new LiveProtocolError(`Live result contains duplicate key '${String(key)}'.`);
      }

      replacement.set(key, event.rows![index]!);
    });
    this.#rows.clear();
    replacement.forEach((row, key) => this.#rows.set(key, row));
    this.#order = [...event.order];
  }

  #add(event: LiveResultEvent<TRow, TKey>): void {
    if (event.key === null || event.row === null || event.currentIndex === null) {
      throw new LiveProtocolError("RowAdded must contain key, row, and currentIndex.");
    }

    if (this.#rows.has(event.key) || event.currentIndex < 0 || event.currentIndex > this.#order.length) {
      throw new LiveProtocolError("RowAdded conflicts with the current keyed result.");
    }

    this.#rows.set(event.key, event.row);
    this.#order.splice(event.currentIndex, 0, event.key);
  }

  #update(event: LiveResultEvent<TRow, TKey>): void {
    if (event.key === null || event.row === null || !this.#rows.has(event.key)) {
      throw new LiveProtocolError("RowUpdated must reference an existing key and contain a row.");
    }

    this.#rows.set(event.key, event.row);
    if (event.currentIndex !== null) {
      const previous = this.#order.indexOf(event.key);
      if (previous < 0 || event.currentIndex < 0 || event.currentIndex >= this.#order.length) {
        throw new LiveProtocolError("RowUpdated contains an invalid result index.");
      }

      this.#order.splice(previous, 1);
      this.#order.splice(event.currentIndex, 0, event.key);
    }
  }

  #remove(event: LiveResultEvent<TRow, TKey>): void {
    if (event.key === null || !this.#rows.delete(event.key)) {
      throw new LiveProtocolError("RowRemoved must reference an existing key.");
    }

    const index = this.#order.indexOf(event.key);
    if (index < 0) {
      throw new LiveProtocolError("RowRemoved key is absent from the result order.");
    }

    this.#order.splice(index, 1);
  }

  #reorder(event: LiveResultEvent<TRow, TKey>): void {
    if (event.order === null || event.order.length !== this.#rows.size) {
      throw new LiveProtocolError("ResultReordered must contain every current key exactly once.");
    }

    const seen = new Set<TKey>();
    for (const key of event.order) {
      if (!this.#rows.has(key) || seen.has(key)) {
        throw new LiveProtocolError("ResultReordered contains a missing or duplicate key.");
      }

      seen.add(key);
    }

    this.#order = [...event.order];
  }
}

type StateListener<TRow> = (state: LiveQueryState<TRow>) => void;

export class LiveQuery<TRow, TKey extends LiveKey, TParameters extends object> {
  readonly #client: BlueTuskLiveClient;
  readonly #request: LiveSubscriptionRequest<TParameters>;
  readonly #store = new LiveResultStore<TRow, TKey>();
  readonly #listeners = new Set<StateListener<TRow>>();
  #state: LiveQueryState<TRow> = {
    phase: "idle",
    rows: [],
    lastSequence: 0,
    error: null
  };
  #abort: AbortController | null = null;
  #resumeToken: string | undefined;

  constructor(client: BlueTuskLiveClient, request: LiveSubscriptionRequest<TParameters>) {
    this.#client = client;
    this.#request = request;
    this.#resumeToken = request.resumeToken;
  }

  get state(): LiveQueryState<TRow> {
    return this.#state;
  }

  subscribe(listener: StateListener<TRow>): () => void {
    this.#listeners.add(listener);
    listener(this.#state);
    return () => this.#listeners.delete(listener);
  }

  start(): void {
    if (this.#abort !== null) {
      return;
    }

    this.#abort = new AbortController();
    void this.#run(this.#abort.signal);
  }

  stop(): void {
    const abort = this.#abort;
    if (abort === null) {
      return;
    }

    this.#abort = null;
    abort.abort();
    this.#setState({ ...this.#state, phase: "stopped", error: null });
  }

  async #run(signal: AbortSignal): Promise<void> {
    let attempt = 0;
    this.#setPhase("connecting");
    while (!signal.aborted) {
      try {
        const response = await this.#client.open(
          {
            query: this.#request.query,
            parameters: this.#request.parameters,
            ...(this.#resumeToken === undefined ? {} : { resumeToken: this.#resumeToken })
          },
          signal
        );

        if (response.status === 409 && this.#resumeToken !== undefined) {
          this.#resumeToken = undefined;
          this.#client.persistResumeToken(null);
          attempt = 0;
          this.#setPhase("reconnecting");
          continue;
        }

        if (!response.ok) {
          throw new LiveHttpError(
            response.status,
            response.status === 429 || response.status === 503 || response.status >= 500
          );
        }

        if (response.body === null) {
          throw new LiveProtocolError("BlueTusk Live response has no streaming body.");
        }

        attempt = 0;
        this.#setPhase("live");
        for await (const frame of parseServerSentEvents(response.body, signal)) {
          if (frame.event !== "change" && frame.event !== "reset") {
            continue;
          }

          const message = parseTransportMessage<TRow, TKey>(frame.data);
          if (message.kind === "ResetRequired") {
            this.#resumeToken = undefined;
            this.#client.persistResumeToken(null);
            throw new LiveResetRequiredError();
          }

          if (message.event === null || message.sequence === null || message.resumeToken === null) {
            throw new LiveProtocolError("Live event message is missing event, sequence, or resume token.");
          }

          if (message.event.sequence !== message.sequence) {
            throw new LiveProtocolError("Live envelope and event sequences do not match.");
          }

          if (message.sequence <= this.#state.lastSequence) {
            continue;
          }

          const authoritative = message.event.kind === "InitialResult" ||
            message.event.kind === "ResultReset";
          if (!authoritative &&
              this.#state.lastSequence !== 0 &&
              message.sequence !== this.#state.lastSequence + 1) {
            throw new LiveProtocolError(
              `Live sequence jumped from ${this.#state.lastSequence} to ${message.sequence}.`
            );
          }

          const rows = this.#store.apply(message.event);
          this.#resumeToken = message.resumeToken;
          this.#client.persistResumeToken(message.resumeToken);
          this.#setState({
            phase: "live",
            rows,
            lastSequence: message.sequence,
            error: null
          });
        }

        if (!signal.aborted) {
          throw new LiveHttpError(0, true);
        }
      } catch (error) {
        if (signal.aborted) {
          return;
        }

        const failure = error instanceof Error ? error : new Error(String(error));
        if (failure instanceof LiveProtocolError ||
            (failure instanceof LiveHttpError && !failure.retryable)) {
          this.#setState({ ...this.#state, phase: "faulted", error: failure });
          this.#abort = null;
          return;
        }

        this.#setState({
          ...this.#state,
          phase: "reconnecting",
          error: failure instanceof LiveResetRequiredError ? null : failure
        });
        if (!(failure instanceof LiveResetRequiredError)) {
          await this.#client.delay(attempt++, signal);
        }
      }
    }
  }

  #setPhase(phase: LiveConnectionPhase): void {
    this.#setState({ ...this.#state, phase, error: null });
  }

  #setState(state: LiveQueryState<TRow>): void {
    this.#state = state;
    for (const listener of this.#listeners) {
      listener(state);
    }
  }
}

export class BlueTuskLiveClient {
  readonly #options: Required<Pick<
    LiveClientOptions,
    "endpoint" | "initialRetryDelayMs" | "maximumRetryDelayMs" | "retryJitter" | "random"
  >> & LiveClientOptions;
  readonly #fetch: typeof globalThis.fetch;

  constructor(options: LiveClientOptions) {
    if (options.endpoint.trim().length === 0) {
      throw new TypeError("A BlueTusk Live endpoint is required.");
    }

    const initialRetryDelayMs = options.initialRetryDelayMs ?? 250;
    const maximumRetryDelayMs = options.maximumRetryDelayMs ?? 15_000;
    const retryJitter = options.retryJitter ?? 0.2;
    if (initialRetryDelayMs < 0 ||
        maximumRetryDelayMs < initialRetryDelayMs ||
        retryJitter < 0 ||
        retryJitter > 1) {
      throw new RangeError("BlueTusk Live retry settings are invalid.");
    }

    this.#fetch = options.fetch ?? globalThis.fetch;
    if (this.#fetch === undefined) {
      throw new TypeError("No fetch implementation is available.");
    }

    this.#options = {
      ...options,
      endpoint: options.endpoint,
      initialRetryDelayMs,
      maximumRetryDelayMs,
      retryJitter,
      random: options.random ?? Math.random
    };
  }

  createQuery<TRow, TKey extends LiveKey, TParameters extends object>(
    request: LiveSubscriptionRequest<TParameters>
  ): LiveQuery<TRow, TKey, TParameters> {
    if (request.query.trim().length === 0) {
      throw new TypeError("A trusted Live query registration name is required.");
    }

    return new LiveQuery<TRow, TKey, TParameters>(this, request);
  }

  createClientQuery(
    capability: string,
    document: LiveClientQueryDocument,
    resumeToken?: string
  ): LiveQuery<LiveClientRow, string, LiveClientQueryDocument> {
    if (capability.trim().length === 0) {
      throw new TypeError("A client-query capability name is required.");
    }

    return this.createQuery({
      query: capability,
      parameters: document,
      ...(resumeToken === undefined ? {} : { resumeToken })
    });
  }

  async open<TParameters extends object>(
    request: LiveSubscriptionRequest<TParameters>,
    signal: AbortSignal
  ): Promise<Response> {
    return await this.#fetch(this.#options.endpoint, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "accept": "text/event-stream",
        ...this.#options.headers
      },
      credentials: this.#options.credentials ?? "same-origin",
      body: JSON.stringify(request),
      signal
    });
  }

  persistResumeToken(token: string | null): void {
    this.#options.onResumeToken?.(token);
  }

  async delay(attempt: number, signal: AbortSignal): Promise<void> {
    const base = Math.min(
      this.#options.maximumRetryDelayMs,
      this.#options.initialRetryDelayMs * (2 ** Math.min(attempt, 20))
    );
    const jitter = 1 + ((this.#options.random() * 2) - 1) * this.#options.retryJitter;
    const milliseconds = Math.max(0, Math.round(base * jitter));
    await abortableDelay(milliseconds, signal);
  }
}

interface SseFrame {
  readonly event: string;
  readonly data: string;
}

export async function* parseServerSentEvents(
  stream: ReadableStream<Uint8Array>,
  signal?: AbortSignal
): AsyncGenerator<SseFrame> {
  const reader = stream.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  try {
    while (true) {
      if (signal?.aborted === true) {
        return;
      }

      const result = await reader.read();
      if (result.done) {
        buffer += decoder.decode();
        break;
      }

      buffer += decoder.decode(result.value, { stream: true });
      buffer = buffer.replaceAll("\r\n", "\n").replaceAll("\r", "\n");
      let boundary: number;
      while ((boundary = buffer.indexOf("\n\n")) >= 0) {
        const block = buffer.slice(0, boundary);
        buffer = buffer.slice(boundary + 2);
        const frame = parseSseBlock(block);
        if (frame !== null) {
          yield frame;
        }
      }
    }

    const tail = parseSseBlock(buffer);
    if (tail !== null) {
      yield tail;
    }
  } finally {
    try {
      await reader.cancel();
    } catch {
      // The fetch implementation may already have aborted the stream.
    }
    reader.releaseLock();
  }
}

export function parseTransportMessage<TRow, TKey extends LiveKey>(
  json: string
): LiveTransportMessage<TRow, TKey> {
  const value: unknown = JSON.parse(json);
  if (typeof value !== "object" || value === null) {
    throw new LiveProtocolError("Live transport payload must be a JSON object.");
  }

  const source = value as Record<string, unknown>;
  const kind = readProperty(source, "kind", "Kind");
  const sequence = readProperty(source, "sequence", "Sequence");
  const resumeToken = readProperty(source, "resumeToken", "ResumeToken");
  const eventValue = readProperty(source, "event", "Event");
  const normalizedKind = normalizeEnum(kind, ["Event", "ResetRequired"] as const, "message kind");
  return {
    kind: normalizedKind,
    sequence: sequence === null ? null : requireSafeInteger(sequence, "sequence"),
    resumeToken: resumeToken === null ? null : requireString(resumeToken, "resumeToken"),
    event: eventValue === null ? null : normalizeLiveEvent<TRow, TKey>(eventValue)
  };
}

function normalizeLiveEvent<TRow, TKey extends LiveKey>(
  value: unknown
): LiveResultEvent<TRow, TKey> {
  if (typeof value !== "object" || value === null) {
    throw new LiveProtocolError("Live result event must be a JSON object.");
  }

  const source = value as Record<string, unknown>;
  return {
    sequence: requireSafeInteger(readProperty(source, "sequence", "Sequence"), "event.sequence"),
    kind: normalizeEnum(
      readProperty(source, "kind", "Kind"),
      ["InitialResult", "RowAdded", "RowUpdated", "RowRemoved", "ResultReordered", "ResultReset"] as const,
      "event.kind"
    ),
    key: readNullableKey(readProperty(source, "key", "Key")),
    row: readProperty(source, "row", "Row") as TRow | null,
    previousIndex: readNullableInteger(readProperty(source, "previousIndex", "PreviousIndex"), "previousIndex"),
    currentIndex: readNullableInteger(readProperty(source, "currentIndex", "CurrentIndex"), "currentIndex"),
    rows: readProperty(source, "rows", "Rows") as readonly TRow[] | null,
    order: readProperty(source, "order", "Order") as readonly TKey[] | null,
    resetReason: normalizeNullableEnum(
      readProperty(source, "resetReason", "ResetReason"),
      ["ReplayExpired", "DiffLimitExceeded", "QueryShapeChanged", "SchemaChanged", "ServerRestart"] as const,
      "resetReason"
    )
  };
}

function parseSseBlock(block: string): SseFrame | null {
  let event = "message";
  const data: string[] = [];
  for (const line of block.split("\n")) {
    if (line.startsWith(":")) {
      continue;
    }

    const colon = line.indexOf(":");
    const field = colon < 0 ? line : line.slice(0, colon);
    let value = colon < 0 ? "" : line.slice(colon + 1);
    if (value.startsWith(" ")) {
      value = value.slice(1);
    }

    if (field === "event") {
      event = value;
    } else if (field === "data") {
      data.push(value);
    }
  }

  return data.length === 0 ? null : { event, data: data.join("\n") };
}

function readProperty(source: Record<string, unknown>, camel: string, pascal: string): unknown {
  if (Object.hasOwn(source, camel)) {
    return source[camel];
  }

  if (Object.hasOwn(source, pascal)) {
    return source[pascal];
  }

  return null;
}

function normalizeEnum<const T extends readonly string[]>(
  value: unknown,
  names: T,
  field: string
): T[number] {
  if (typeof value === "number" && Number.isInteger(value) && value >= 0 && value < names.length) {
    return names[value]!;
  }

  if (typeof value === "string" && names.includes(value)) {
    return value as T[number];
  }

  throw new LiveProtocolError(`Live ${field} is invalid.`);
}

function normalizeNullableEnum<const T extends readonly string[]>(
  value: unknown,
  names: T,
  field: string
): T[number] | null {
  return value === null ? null : normalizeEnum(value, names, field);
}

function readNullableKey<TKey extends LiveKey>(value: unknown): TKey | null {
  if (value === null) {
    return null;
  }

  if (typeof value !== "string" && typeof value !== "number") {
    throw new LiveProtocolError("Live event key must be a string, number, or null.");
  }

  return value as TKey;
}

function readNullableInteger(value: unknown, field: string): number | null {
  return value === null ? null : requireSafeInteger(value, field);
}

function requireSafeInteger(value: unknown, field: string): number {
  if (typeof value !== "number" || !Number.isSafeInteger(value)) {
    throw new LiveProtocolError(`Live ${field} must be a safe integer.`);
  }

  return value;
}

function requireString(value: unknown, field: string): string {
  if (typeof value !== "string" || value.length === 0) {
    throw new LiveProtocolError(`Live ${field} must be a non-empty string.`);
  }

  return value;
}

async function abortableDelay(milliseconds: number, signal: AbortSignal): Promise<void> {
  if (signal.aborted || milliseconds === 0) {
    return;
  }

  await new Promise<void>((resolve) => {
    const handle = setTimeout(resolve, milliseconds);
    signal.addEventListener("abort", () => {
      clearTimeout(handle);
      resolve();
    }, { once: true });
  });
}
