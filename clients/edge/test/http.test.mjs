import assert from "node:assert/strict";
import test from "node:test";
import { EdgeHttpRemoteTransport, EdgeHttpError } from "../dist/index.js";

test("HTTP wire keeps canonical Int64 and raw UTF-8 bytes with bearer-only credentials", async () => {
  let request;
  const payload = "{ \"raw\" : \"嵐\" }";
  const remote = new EdgeHttpRemoteTransport({ endpoint: "http://localhost/edge", bearerToken: () => "token", fetch: async (url, init) => {
    request = { url, init }; return new Response(JSON.stringify({ kind: "applied", record: { id: "1", revision: "9007199254740997", payload: Buffer.from(payload).toString("base64"), deleted: false } }), { status: 200 });
  } });
  const outcome = await remote.applyMutation({ scope: { tenant: "tenant", id: "orders", epoch: "9007199254740999" }, id: "a55a5cce-7677-4e4e-8a59-701b688433ab", documentId: "1", expectedRevision: "9007199254740993", kind: "upsert", payload });
  assert.equal(outcome.record.payload, payload); assert.equal(outcome.record.revision, "9007199254740997");
  assert.equal(request.init.credentials, "omit"); assert.equal(request.init.headers.Authorization, "Bearer token");
  assert.equal(JSON.parse(request.init.body).expectedRevision, "9007199254740993");
  assert.match(request.url, /epoch=9007199254740999/);
});
test("HTTP denies noncanonical integers, non-UUID mutations and typed server authorization failures", async () => {
  const remote = new EdgeHttpRemoteTransport({ endpoint: "http://localhost/edge", bearerToken: () => "token", fetch: async () => new Response(null, { status: 403 }) });
  await assert.rejects(() => remote.beginSnapshot({ tenant: "other", id: "orders", epoch: "1" }), error => error instanceof EdgeHttpError && error.status === 403);
  await assert.rejects(() => remote.beginSnapshot({ tenant: "other", id: "orders", epoch: "01" }), TypeError);
  await assert.rejects(() => remote.applyMutation({ scope: { tenant: "tenant", id: "orders", epoch: "1" }, id: "not-uuid", documentId: "1", expectedRevision: "0", kind: "upsert", payload: "{}" }), TypeError);
});
test("HTTP counts streamed response bytes independently of content length", async () => {
  assert.throws(() => new EdgeHttpRemoteTransport({ endpoint: "http://localhost/edge", bearerToken: () => "token", maxResponseBytes: 64 }), RangeError);
  const remote = new EdgeHttpRemoteTransport({ endpoint: "http://localhost/edge", bearerToken: () => "token", maxRecordBytes: 2, maxResponseBytes: 5000,
    fetch: async () => new Response(new ReadableStream({ start(controller) { controller.enqueue(new Uint8Array(3000)); controller.enqueue(new Uint8Array(3000)); controller.close() } })) });
  await assert.rejects(() => remote.beginSnapshot({ tenant: "tenant", id: "orders", epoch: "1" }), RangeError);
});
