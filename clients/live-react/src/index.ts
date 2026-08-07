import { useEffect, useMemo, useSyncExternalStore } from "react";
import {
  type BlueTuskLiveClient,
  type LiveKey,
  type LiveQueryState,
  type LiveSubscriptionRequest
} from "@bluetusk/live";

export function useBlueTuskLiveQuery<
  TRow,
  TKey extends LiveKey,
  TParameters extends object
>(
  client: BlueTuskLiveClient,
  request: LiveSubscriptionRequest<TParameters>
): LiveQueryState<TRow> {
  const query = useMemo(
    () => client.createQuery<TRow, TKey, TParameters>(request),
    [client, request]
  );
  const state = useSyncExternalStore(
    (onStoreChange) => query.subscribe(() => onStoreChange()),
    () => query.state,
    () => query.state
  );

  useEffect(() => {
    query.start();
    return () => query.stop();
  }, [query]);

  return state;
}
