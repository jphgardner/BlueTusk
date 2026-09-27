export const event = (kind, values = {}) => ({
  sequence: 1, kind, key: null, row: null, previousIndex: null,
  currentIndex: null, rows: null, order: null, resetReason: null, ...values
});

export const message = (kind, sequence, values = {}) => ({
  kind: "Event", sequence, resumeToken: `token-${sequence}`,
  event: event(kind, { sequence, ...values })
});

export const frame = (payload) => `event: change\ndata: ${JSON.stringify(payload)}\n\n`;
export const response = (messages) => new Response(messages.map(frame).join(""));

export function observeUntil(query, predicate) {
  return new Promise((resolve, reject) => {
    const unsubscribe = query.subscribe((state) => {
      if (state.phase === "faulted" && !predicate(state)) {
        unsubscribe();
        reject(state.error);
      } else if (predicate(state)) {
        unsubscribe();
        query.stop();
        resolve(state);
      }
    });
    query.start();
  });
}

export function deferred() {
  let resolve;
  const promise = new Promise((complete) => { resolve = complete; });
  return { promise, resolve };
}

export const tick = () => new Promise((resolve) => setImmediate(resolve));
