"use strict";
const byId = id => document.getElementById(id);
let session;
function operationIdV7() {
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  let milliseconds = BigInt(Date.now());
  for (let index = 5; index >= 0; index--) { bytes[index] = Number(milliseconds & 255n); milliseconds >>= 8n; }
  bytes[6] = (bytes[6] & 15) | 112;
  bytes[8] = (bytes[8] & 63) | 128;
  const hex = Array.from(bytes, byte => byte.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0,8)}-${hex.slice(8,12)}-${hex.slice(12,16)}-${hex.slice(16,20)}-${hex.slice(20)}`;
}
async function responseJson(url, options) {
  const response = await fetch(url, {credentials:"same-origin", ...options});
  if (!response.ok) throw new Error(`Request failed (${response.status}). Check your permissions, query, and configured limits.`);
  return response.json();
}
async function refreshSchema() {
  const target = byId("schema");
  target.replaceChildren();
  try {
    const snapshot = await responseJson("schema");
    for (const relation of snapshot.Relations) {
      const details = document.createElement("details"), summary = document.createElement("summary");
      summary.textContent = `${relation.Schema}.${relation.Name}`;
      details.append(summary);
      for (const column of relation.Columns) {
        const item = document.createElement("div"); item.className = "column";
        item.textContent = `${column.Name} · ${column.PostgreSqlType}${column.IsNullable ? "" : " · required"}`;
        details.append(item);
      }
      target.append(details);
    }
    if (!snapshot.Relations.length) target.textContent = "No relations in your authorized schema scope.";
  } catch (error) { target.textContent = error.message; }
}
async function query(explain) {
  byId("run").disabled = byId("explain").disabled = true;
  byId("status").textContent = explain ? "Requesting query plan…" : "Running bounded read query…";
  const operationId = operationIdV7();
  try {
    session ??= await responseJson("session");
    const result = await responseJson("query", {method:"POST",headers:{"Content-Type":"application/json",[session.header ?? session.Header]:session.token ?? session.Token},body:JSON.stringify({Sql:byId("sql").value,Explain:explain,OperationId:operationId})});
    const table = document.createElement("table"), head = document.createElement("thead"), headRow = document.createElement("tr");
    for (const column of result.columns) { const th = document.createElement("th"); th.textContent = `${column.name} · ${column.type}`; headRow.append(th); }
    head.append(headRow); table.append(head);
    const body = document.createElement("tbody");
    for (const row of result.rows) { const tr = document.createElement("tr"); for (const value of row) { const td = document.createElement("td"); td.textContent = value === null ? "NULL" : String(value); tr.append(td); } body.append(tr); }
    table.append(body); byId("results").replaceChildren(table);
    byId("status").textContent = `${result.count} rows returned${explain ? " · query plan" : ""}. Audit operation ${operationId}.`;
  } catch (error) { byId("status").textContent = `${error.message} Request ${operationId}; if execution may have begun, inspect its audit before retrying.`; }
  finally { byId("run").disabled = byId("explain").disabled = false; }
}
byId("run").addEventListener("click", () => query(false));
byId("explain").addEventListener("click", () => query(true));
byId("refresh").addEventListener("click", refreshSchema);
let operationalCursor = null;
let pendingReplay = null;
async function inspectOperations(next) {
  const button = byId("inspect"), nextButton = byId("inspect-next");
  button.disabled = nextButton.disabled = true;
  try {
    const result = await responseJson("operations/live" + (next && operationalCursor ? "?after=" + encodeURIComponent(operationalCursor) : ""));
    const table = document.createElement("table"), head = document.createElement("thead"), row = document.createElement("tr");
    for (const label of ["Subscription", "Clients", "Sequence", "Invalidation lag", "Resume rejections"]) { const th = document.createElement("th"); th.textContent = label; row.append(th); }
    head.append(row); table.append(head); const body = document.createElement("tbody");
    for (const item of result.subscriptions) { const tr = document.createElement("tr"); for (const value of [item.fingerprint,item.subscribers,item.persistedSequence,item.invalidationLag ?? "Unavailable",item.resumeRejections]) { const td = document.createElement("td"); td.textContent = String(value); tr.append(td); } body.append(tr); }
    table.append(body); byId("subscriptions").replaceChildren(table);
    const selection = byId("replay-target"), previous = selection.value; selection.replaceChildren();
    const placeholder = document.createElement("option"); placeholder.value = ""; placeholder.textContent = "Select an authorized target"; selection.append(placeholder);
    for (const target of result.replayTargets) { const option = document.createElement("option"); option.value = option.textContent = target; selection.append(option); }
    selection.value = previous; operationalCursor = result.next; nextButton.hidden = !operationalCursor;
    byId("operations-status").textContent = `${result.subscriptions.length} subscriptions · observed ${result.observedAt}.`;
  } catch (error) { byId("operations-status").textContent = error.message; }
  finally { button.disabled = nextButton.disabled = false; }
}
byId("inspect").addEventListener("click", () => inspectOperations(false));
byId("inspect-next").addEventListener("click", () => inspectOperations(true));
byId("replay-form").addEventListener("submit", async event => {
  event.preventDefault(); const button = byId("replay"); button.disabled = true;
  try {
    session ??= await responseJson("session");
    const input = JSON.stringify({Target:byId("replay-target").value,Confirmation:byId("replay-confirmation").value,Reason:byId("replay-reason").value});
    if (pendingReplay?.input !== input) pendingReplay = {input, operationId:operationIdV7()};
    const response = await fetch("operations/replay", {method:"POST",credentials:"same-origin",headers:{"Content-Type":"application/json",[session.header ?? session.Header]:session.token ?? session.Token},body:JSON.stringify({OperationId:pendingReplay.operationId,...JSON.parse(input)})});
    if (!response.ok) throw new Error(`Replay failed (${response.status}). Check the target, confirmation, permissions, and audit service.`);
    byId("operations-status").textContent = `Quarantine replay completed. Audit operation ${pendingReplay.operationId}.`;
    byId("replay-confirmation").value = "";
    pendingReplay = null;
  } catch (error) { byId("operations-status").textContent = `${error.message}${pendingReplay ? " Audit operation " + pendingReplay.operationId + " before repeating a request with an uncertain outcome." : ""}`; }
  finally { button.disabled = false; }
});
let traceCursor = null;
byId("trace-load").addEventListener("click", async () => {
  try {
    const result = await responseJson("events/streams"), selection = byId("trace-stream"); selection.replaceChildren();
    for (const alias of result.streams) { const option = document.createElement("option"); option.value = option.textContent = alias; selection.append(option); }
    traceCursor = null; byId("trace-next").hidden = true;
    byId("trace-status").textContent = `${result.streams.length} authorized streams.`;
  } catch (error) { byId("trace-status").textContent = error.message; }
});
byId("trace-stream").addEventListener("change", () => { traceCursor = null; byId("trace-next").hidden = true; });
async function readTrace(next) {
  const refresh = byId("trace-refresh"), nextButton = byId("trace-next"); refresh.disabled = nextButton.disabled = true;
  try {
    const stream = byId("trace-stream").value; if (!stream) throw new Error("Load and select an authorized stream first.");
    const result = await responseJson("events/?stream=" + encodeURIComponent(stream) + (next && traceCursor ? "&after=" + traceCursor : ""));
    const table = document.createElement("table"), head = document.createElement("thead"), row = document.createElement("tr");
    for (const label of ["Sequence", "Event identity", "Type", "Version", "Occurred", "Payload bytes"]) { const th = document.createElement("th"); th.textContent = label; row.append(th); }
    head.append(row); table.append(head); const body = document.createElement("tbody");
    for (const item of result.events) { const tr = document.createElement("tr"); for (const value of [item.sequence,item.id,item.type,item.version,item.occurredAt,item.payloadBytes]) { const td = document.createElement("td"); td.textContent = String(value); tr.append(td); } body.append(tr); }
    table.append(body); byId("trace-results").replaceChildren(table);
    traceCursor = result.next; nextButton.hidden = !traceCursor;
    byId("trace-status").textContent = `${result.events.length} events. Payloads are omitted.`;
  } catch (error) { byId("trace-status").textContent = error.message; }
  finally { refresh.disabled = nextButton.disabled = false; }
}
byId("trace-refresh").addEventListener("click", () => readTrace(false));
byId("trace-next").addEventListener("click", () => readTrace(true));
refreshSchema();
