import assert from "node:assert/strict";
import test from "node:test";
import {
  BlueTuskLiveClient,
  LiveProtocolError,
  LiveResultStore,
  parseServerSentEvents,
  parseTransportMessage
} from "../dist/index.js";

const event = (kind, values = {}) => ({
  sequence: values.sequence ?? 1,
  kind,
  key: null,
  row: null,
  previousIndex: null,
  currentIndex: null,
  rows: null,
  order: null,
  resetReason: null,
  ...values
});

test("keyed reducer applies initial, add, update, reorder, and remove", () => {
  const store = new LiveResultStore();
  store.apply(event("InitialResult", {
    rows: [{ id: 1, value: "one" }],
    order: [1]
  }));
  store.apply(event("RowAdded", {
    sequence: 2,
    key: 2,
    row: { id: 2, value: "two" },
    currentIndex: 1
  }));
  store.apply(event("RowUpdated", {
    sequence: 3,
    key: 2,
    row: { id: 2, value: "TWO" }
  }));
  store.apply(event("ResultReordered", {
    sequence: 4,
    order: [2, 1]
  }));
  store.apply(event("RowRemoved", {
    sequence: 5,
    key: 1
  }));
  assert.deepEqual(store.rows, [{ id: 2, value: "TWO" }]);
});

test("SSE parser preserves frames split across UTF-8 chunks and multiline data", async () => {
  const bytes = new TextEncoder().encode("event: change\r\ndata: {\"a\":\r\ndata: 1}\r\n\r\n");
  const stream = new ReadableStream({
    start(controller) {
      controller.enqueue(bytes.slice(0, 17));
      controller.enqueue(bytes.slice(17));
      controller.close();
    }
  });

  const frames = [];
  for await (const frame of parseServerSentEvents(stream)) {
    frames.push(frame);
  }

  assert.deepEqual(frames, [{ event: "change", data: "{\"a\":\n1}" }]);
});

test("transport parser accepts version-one PascalCase numeric payloads", () => {
  const message = parseTransportMessage(JSON.stringify({
    Kind: 0,
    Sequence: 1,
    ResumeToken: "signed",
    Event: {
      Sequence: 1,
      Kind: 0,
      Key: null,
      Row: null,
      PreviousIndex: null,
      CurrentIndex: null,
      Rows: [{ id: 1 }],
      Order: [1],
      ResetReason: null
    }
  }));

  assert.equal(message.kind, "Event");
  assert.equal(message.event.kind, "InitialResult");
});

test("query discards an expired token only after 409 and reconnects to an authoritative result", async () => {
  const seenRequests = [];
  const tokens = [];
  let calls = 0;
  const fetch = async (_url, init) => {
    seenRequests.push(JSON.parse(init.body));
    calls++;
    if (calls === 1) {
      return new Response(null, { status: 409 });
    }

    const payload = {
      kind: "Event",
      sequence: 4,
      resumeToken: "fresh-token",
      event: event("ResultReset", {
        sequence: 4,
        rows: [{ id: 7 }],
        order: [7],
        resetReason: "ReplayExpired"
      })
    };
    const body = `event: change\ndata: ${JSON.stringify(payload)}\n\n`;
    return new Response(body, {
      status: 200,
      headers: { "content-type": "text/event-stream" }
    });
  };
  const client = new BlueTuskLiveClient({
    endpoint: "/live",
    fetch,
    initialRetryDelayMs: 0,
    maximumRetryDelayMs: 0,
    retryJitter: 0,
    onResumeToken: (token) => tokens.push(token)
  });
  const query = client.createQuery({
    query: "orders",
    parameters: { tenant: "a" },
    resumeToken: "expired-token"
  });

  await new Promise((resolve, reject) => {
    const unsubscribe = query.subscribe((state) => {
      if (state.phase === "faulted") {
        reject(state.error);
      }
      if (state.lastSequence === 4) {
        unsubscribe();
        query.stop();
        resolve();
      }
    });
    query.start();
  });

  assert.equal(seenRequests[0].resumeToken, "expired-token");
  assert.equal("resumeToken" in seenRequests[1], false);
  assert.deepEqual(tokens, [null, "fresh-token"]);
  assert.deepEqual(query.state.rows, [{ id: 7 }]);
});

test("result store rejects duplicate reset keys", () => {
  const store = new LiveResultStore();
  assert.throws(
    () => store.apply(event("ResultReset", {
      rows: [{ id: 1 }, { id: 1 }],
      order: [1, 1]
    })),
    LiveProtocolError
  );
});

test("an authoritative reset may bridge an expired replay sequence", async () => {
  let calls = 0;
  const fetch = async () => {
    calls++;
    const payload = calls === 1
      ? {
          kind: "Event",
          sequence: 1,
          resumeToken: "one",
          event: event("InitialResult", {
            rows: [{ id: 1 }],
            order: [1]
          })
        }
      : {
          kind: "Event",
          sequence: 9,
          resumeToken: "nine",
          event: event("ResultReset", {
            sequence: 9,
            rows: [{ id: 9 }],
            order: [9],
            resetReason: "ReplayExpired"
          })
        };
    return new Response(`event: change\ndata: ${JSON.stringify(payload)}\n\n`);
  };
  const client = new BlueTuskLiveClient({
    endpoint: "/live",
    fetch,
    initialRetryDelayMs: 0,
    maximumRetryDelayMs: 0,
    retryJitter: 0
  });
  const query = client.createQuery({ query: "orders", parameters: {} });

  await new Promise((resolve, reject) => {
    query.subscribe((state) => {
      if (state.phase === "faulted") {
        reject(state.error);
      }
      if (state.lastSequence === 9) {
        query.stop();
        resolve();
      }
    });
    query.start();
  });

  assert.deepEqual(query.state.rows, [{ id: 9 }]);
});
