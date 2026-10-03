import assert from "node:assert/strict";
import test from "node:test";
import { BlueTuskLiveClient } from "../dist/index.js";

// Node's fetch ignores its receiver, but a browser's window.fetch is a WebIDL operation:
// calling it with any `this` other than the global object (or undefined) throws
// "TypeError: Illegal invocation". This stand-in reproduces that contract so the tests
// fail the same way a real browser does.
function installBrowserFetch(t) {
  const original = globalThis.fetch;
  const receivers = [];
  let illegal = null;
  function browserFetch(_input, _init) {
    receivers.push(this);
    if (this !== undefined && this !== globalThis) {
      illegal = new TypeError("Failed to execute 'fetch' on 'Window': Illegal invocation");
      throw illegal;
    }

    const payload = {
      kind: "Event",
      sequence: 1,
      resumeToken: "one",
      event: {
        sequence: 1,
        kind: "InitialResult",
        key: null,
        row: null,
        previousIndex: null,
        currentIndex: null,
        rows: [{ id: 1 }],
        order: [1],
        resetReason: null
      }
    };
    return Promise.resolve(new Response(`event: change\ndata: ${JSON.stringify(payload)}\n\n`, {
      status: 200,
      headers: { "content-type": "text/event-stream" }
    }));
  }

  globalThis.fetch = browserFetch;
  t.after(() => { globalThis.fetch = original; });
  return { browserFetch, receivers, illegal: () => illegal };
}

async function receiveInitialResult(client, browser) {
  const query = client.createQuery({ query: "orders", parameters: {} });
  try {
    await new Promise((resolve, reject) => {
      query.subscribe((state) => {
        if (browser.illegal() !== null) {
          reject(browser.illegal());
        } else if (state.phase === "faulted") {
          reject(state.error);
        } else if (state.lastSequence === 1) {
          resolve();
        }
      });
      query.start();
    });
  } finally {
    query.stop();
  }

  assert.deepEqual(query.state.rows, [{ id: 1 }]);
}

const retryOptions = { initialRetryDelayMs: 0, maximumRetryDelayMs: 0, retryJitter: 0 };

test("the default fetch is invoked with the global receiver, as a browser requires", { timeout: 2000 }, async t => {
  const browser = installBrowserFetch(t);
  const client = new BlueTuskLiveClient({ endpoint: "/live", ...retryOptions });

  await receiveInitialResult(client, browser);

  assert.ok(browser.receivers.length >= 1);
  assert.ok(browser.receivers.every(receiver => receiver === globalThis));
});

test("an unbound window.fetch passed explicitly is not invoked on the client", { timeout: 2000 }, async t => {
  const browser = installBrowserFetch(t);
  const client = new BlueTuskLiveClient({ endpoint: "/live", fetch: browser.browserFetch, ...retryOptions });

  await receiveInitialResult(client, browser);

  assert.ok(browser.receivers.length >= 1);
  assert.ok(browser.receivers.every(receiver => receiver !== client));
});

test("the default fetch is resolved when the client is constructed", t => {
  const original = globalThis.fetch;
  t.after(() => { globalThis.fetch = original; });
  globalThis.fetch = undefined;

  assert.throws(() => new BlueTuskLiveClient({ endpoint: "/live" }), TypeError);
});
