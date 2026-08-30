import {
  computed,
  getCurrentScope,
  onScopeDispose,
  readonly,
  shallowRef,
  type ComputedRef,
  type DeepReadonly,
  type ShallowRef
} from "vue";
import {
  type BlueTuskLiveClient,
  type LiveKey,
  type LiveQuery,
  type LiveQueryState,
  type LiveSubscriptionRequest
} from "@bluetusk/live";

export interface VueLiveQueryOptions {
  readonly autoStart?: boolean;
}

export class VueLiveQuery<
  TRow,
  TKey extends LiveKey,
  TParameters extends object
> {
  readonly #query: LiveQuery<TRow, TKey, TParameters>;
  readonly #mutableState: ShallowRef<LiveQueryState<TRow>>;
  readonly #unsubscribe: () => void;
  #pendingState: LiveQueryState<TRow> | null = null;
  #notificationQueued = false;
  #destroyed = false;

  readonly state: DeepReadonly<ShallowRef<LiveQueryState<TRow>>>;
  readonly rows: ComputedRef<readonly TRow[]>;
  readonly phase: ComputedRef<LiveQueryState<TRow>["phase"]>;
  readonly error: ComputedRef<Error | null>;

  constructor(query: LiveQuery<TRow, TKey, TParameters>) {
    this.#query = query;
    this.#mutableState = shallowRef(query.state);
    this.state = readonly(this.#mutableState);
    this.rows = computed(() => this.#mutableState.value.rows);
    this.phase = computed(() => this.#mutableState.value.phase);
    this.error = computed(() => this.#mutableState.value.error);
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
        this.#mutableState.value = pending;
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
  options: VueLiveQueryOptions = {}
): VueLiveQuery<TRow, TKey, TParameters> {
  const query = new VueLiveQuery(
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
  options: VueLiveQueryOptions = {}
): VueLiveQuery<TRow, TKey, TParameters> {
  if (getCurrentScope() === undefined) {
    throw new Error(
      "useBlueTuskLiveQuery must run inside a Vue setup scope; use createBlueTuskLiveQuery for explicit lifecycle ownership."
    );
  }

  const query = createBlueTuskLiveQuery<TRow, TKey, TParameters>(client, request, options);
  onScopeDispose(() => query.destroy());
  return query;
}
