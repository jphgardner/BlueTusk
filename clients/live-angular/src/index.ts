import {
  computed,
  inject,
  InjectionToken,
  makeEnvironmentProviders,
  signal,
  type EnvironmentProviders,
  type Signal,
  type WritableSignal
} from "@angular/core";
import {
  BlueTuskLiveClient,
  type LiveKey,
  type LiveQuery,
  type LiveQueryState,
  type LiveSubscriptionRequest
} from "@bluetusk/live";

export const BLUE_TUSK_LIVE_CLIENT = new InjectionToken<BlueTuskLiveClient>(
  "BLUE_TUSK_LIVE_CLIENT"
);

export function provideBlueTuskLive(client: BlueTuskLiveClient): EnvironmentProviders {
  if (client === null || client === undefined) {
    throw new TypeError("A BlueTuskLiveClient is required.");
  }

  return makeEnvironmentProviders([
    { provide: BLUE_TUSK_LIVE_CLIENT, useValue: client },
    {
      provide: BlueTuskLiveAngular,
      useFactory: () => new BlueTuskLiveAngular(inject(BLUE_TUSK_LIVE_CLIENT))
    }
  ]);
}

export class AngularLiveQuery<
  TRow,
  TKey extends LiveKey,
  TParameters extends object
> {
  readonly #query: LiveQuery<TRow, TKey, TParameters>;
  readonly #state: WritableSignal<LiveQueryState<TRow>>;
  readonly #unsubscribe: () => void;
  #pendingState: LiveQueryState<TRow> | null = null;
  #notificationQueued = false;
  #destroyed = false;

  readonly state: Signal<LiveQueryState<TRow>>;
  readonly rows: Signal<readonly TRow[]>;
  readonly phase: Signal<LiveQueryState<TRow>["phase"]>;
  readonly error: Signal<Error | null>;

  constructor(query: LiveQuery<TRow, TKey, TParameters>) {
    this.#query = query;
    this.#state = signal(query.state);
    this.state = this.#state.asReadonly();
    this.rows = computed(() => this.#state().rows);
    this.phase = computed(() => this.#state().phase);
    this.error = computed(() => this.#state().error);
    this.#unsubscribe = query.subscribe((state) => this.#scheduleState(state));
  }

  start(): void {
    this.#query.start();
  }

  stop(): void {
    this.#query.stop();
  }

  destroy(): void {
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
        this.#state.set(pending);
      }
    });
  }
}

export class BlueTuskLiveAngular {
  readonly #client: BlueTuskLiveClient;

  constructor(client: BlueTuskLiveClient) {
    this.#client = client;
  }

  createQuery<TRow, TKey extends LiveKey, TParameters extends object>(
    request: LiveSubscriptionRequest<TParameters>
  ): AngularLiveQuery<TRow, TKey, TParameters> {
    return new AngularLiveQuery(this.#client.createQuery<TRow, TKey, TParameters>(request));
  }
}
