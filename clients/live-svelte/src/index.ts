import { onDestroy } from "svelte";
import { derived, writable, type Readable, type Writable } from "svelte/store";
import {
  type BlueTuskLiveClient,
  type LiveKey,
  type LiveQuery,
  type LiveQueryState,
  type LiveSubscriptionRequest
} from "@bluetusk/live";

export interface SvelteLiveQueryOptions {
  readonly autoStart?: boolean;
}

export class SvelteLiveQuery<
  TRow,
  TKey extends LiveKey,
  TParameters extends object
> {
  readonly #query: LiveQuery<TRow, TKey, TParameters>;
  readonly #mutableState: Writable<LiveQueryState<TRow>>;
  readonly #unsubscribe: () => void;
  #pendingState: LiveQueryState<TRow> | null = null;
  #notificationQueued = false;
  #destroyed = false;

  readonly state: Readable<LiveQueryState<TRow>>;
  readonly rows: Readable<readonly TRow[]>;
  readonly phase: Readable<LiveQueryState<TRow>["phase"]>;
  readonly error: Readable<Error | null>;

  constructor(query: LiveQuery<TRow, TKey, TParameters>) {
    this.#query = query;
    this.#mutableState = writable(query.state);
    this.state = { subscribe: this.#mutableState.subscribe };
    this.rows = derived(this.#mutableState, (state) => state.rows);
    this.phase = derived(this.#mutableState, (state) => state.phase);
    this.error = derived(this.#mutableState, (state) => state.error);
    this.#unsubscribe = query.subscribe((state) => this.#scheduleState(state));
  }

  start(): void {
    this.#query.start();
  }

  stop(): void {
    this.#query.stop();
  }

  destroy(): void {
    if (this.#destroyed) {
      return;
    }

    this.#destroyed = true;
    this.#pendingState = null;
    this.#unsubscribe();
    this.#query.stop();
  }

  #scheduleState(state: LiveQueryState<TRow>): void {
    this.#pendingState = state;
    if (this.#notificationQueued) {
      return;
    }

    this.#notificationQueued = true;
    queueMicrotask(() => {
      this.#notificationQueued = false;
      const pending = this.#pendingState;
      this.#pendingState = null;
      if (!this.#destroyed && pending !== null) {
        this.#mutableState.set(pending);
      }
    });
  }
}

export function createBlueTuskLiveQuery<
  TRow,
  TKey extends LiveKey,
  TParameters extends object
>(
  client: BlueTuskLiveClient,
  request: LiveSubscriptionRequest<TParameters>,
  options: SvelteLiveQueryOptions = {}
): SvelteLiveQuery<TRow, TKey, TParameters> {
  const query = new SvelteLiveQuery(
    client.createQuery<TRow, TKey, TParameters>(request)
  );
  if (options.autoStart ?? true) {
    query.start();
  }

  return query;
}

export function useBlueTuskLiveQuery<
  TRow,
  TKey extends LiveKey,
  TParameters extends object
>(
  client: BlueTuskLiveClient,
  request: LiveSubscriptionRequest<TParameters>,
  options: SvelteLiveQueryOptions = {}
): SvelteLiveQuery<TRow, TKey, TParameters> {
  const query = createBlueTuskLiveQuery<TRow, TKey, TParameters>(client, request, options);
  onDestroy(() => query.destroy());
  return query;
}
