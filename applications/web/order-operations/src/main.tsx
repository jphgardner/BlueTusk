import { StrictMode, useEffect, useMemo, useState } from "react";
import { createRoot } from "react-dom/client";
import { BlueTuskLiveClient, type LiveClientQueryDocument, type LiveClientRow } from "@bluetusk/live";
import { useBlueTuskLiveQuery } from "@bluetusk/live-react";
import "./styles.css";

type Session = { tenant: string; name: string };
type Order = {
  id: string;
  customerReference: string;
  state: "Created" | "Allocated" | "Picked" | "Shipped" | "Cancelled";
  version: number;
  updatedAt: string;
};
type TimelineEntry = { operation: string; recordedAt: string; relayedAt?: string };

const liveClient = new BlueTuskLiveClient({
  endpoint: "/api/v1/live",
  credentials: "same-origin",
  onResumeToken: token => token === null
    ? sessionStorage.removeItem("orders-live-resume")
    : sessionStorage.setItem("orders-live-resume", token)
});

function App() {
  const [session, setSession] = useState<Session | null>(null);
  const [orders, setOrders] = useState<readonly Order[]>([]);
  const [reference, setReference] = useState("");
  const [search, setSearch] = useState("");
  const [timeline, setTimeline] = useState<readonly TimelineEntry[]>([]);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    void getJson<Session>("/api/v1/session").then(setSession).catch(showError(setError));
    void getJson<readonly Order[]>("/api/v1/orders").then(setOrders).catch(showError(setError));
  }, []);
  const liveRequest = useMemo(() => ({
    query: "orders-live",
    parameters: {
      language: "linq",
      linq: {
        schema: "orders",
        table: "fulfilment_orders",
        columns: ["Id", "TenantId", "CustomerReference", "State", "Version", "UpdatedAt"],
        filters: [{ column: "TenantId", operator: "Equal", parameter: "tenant" }],
        orderings: [{ column: "UpdatedAt", direction: "Descending" }]
      },
      keyColumns: ["Id"],
      maximumResultCount: 1000,
      parameters: { tenant: { type: "string", value: session?.tenant ?? "" } }
    } satisfies LiveClientQueryDocument,
    resumeToken: sessionStorage.getItem("orders-live-resume") ?? undefined
  }), [session?.tenant]);
  const live = useBlueTuskLiveQuery<LiveClientRow, string, LiveClientQueryDocument>(
    liveClient,
    liveRequest
  );

  async function createOrder(event: React.FormEvent) {
    event.preventDefault();
    try {
      const created = await mutate<Order>("/api/v1/orders", { customerReference: reference });
      setOrders(current => [created, ...current]);
      setReference("");
    } catch (reason) {
      showError(setError)(reason);
    }
  }

  async function searchOrders(event: React.FormEvent) {
    event.preventDefault();
    try {
      setOrders(await getJson<readonly Order[]>(`/api/v1/orders?query=${encodeURIComponent(search)}`));
    } catch (reason) { showError(setError)(reason); }
  }

  async function transition(order: Order, action: "allocate" | "pick" | "ship" | "cancel") {
    try {
      const changed = await mutate<Order>(`/api/v1/orders/${order.id}/${action}`, {
        expectedVersion: order.version,
        allocationReference: action === "allocate" ? `ALLOC-${order.customerReference}` : null
      });
      setOrders(current => current.map(item => item.id === changed.id ? changed : item));
    } catch (reason) { showError(setError)(reason); }
  }

  async function showTimeline(order: Order) {
    try { setTimeline(await getJson<readonly TimelineEntry[]>(`/api/v1/orders/${order.id}/timeline`)); }
    catch (reason) { showError(setError)(reason); }
  }

  return <main>
    <header><p className="eyebrow">BlueTusk RC staging</p><h1>Order fulfilment operations</h1>
      <span>{session?.name ?? "Connecting"} · Live {live.phase}</span></header>
    {error && <aside role="alert">{error}</aside>}
    <section className="kpis">
      <Kpi label="Open" value={orders.filter(order => !["Shipped", "Cancelled"].includes(order.state)).length} />
      <Kpi label="Shipping today" value={orders.filter(order => order.state === "Shipped").length} />
      <Kpi label="Live rows" value={live.rows.length} />
    </section>
    <section className="panel">
      <form onSubmit={searchOrders}><label>Search orders<input value={search}
        onChange={event => setSearch(event.target.value)} /></label><button>Search</button></form>
      <form onSubmit={createOrder}><label>Customer reference<input required value={reference}
        onChange={event => setReference(event.target.value)} /></label><button>Create order</button></form>
      <table><thead><tr><th>Reference</th><th>State</th><th>Version</th><th>Updated</th><th>Actions</th></tr></thead>
        <tbody>{orders.map(order => <tr key={order.id}><td>{order.customerReference}</td>
          <td><span className={`state ${order.state.toLowerCase()}`}>{order.state}</span></td>
          <td>{order.version}</td><td>{new Date(order.updatedAt).toLocaleString()}</td><td>
            {order.state === "Created" && <button onClick={() => void transition(order, "allocate")}>Allocate</button>}
            {order.state === "Allocated" && <button onClick={() => void transition(order, "pick")}>Pick</button>}
            {order.state === "Picked" && <button onClick={() => void transition(order, "ship")}>Ship</button>}
            {!(["Shipped", "Cancelled"] as string[]).includes(order.state) &&
              <button onClick={() => void transition(order, "cancel")}>Cancel</button>}
            <button onClick={() => void showTimeline(order)}>Timeline</button>
          </td></tr>)}</tbody></table>
      {timeline.length > 0 && <ol aria-label="Order timeline">{timeline.map(entry =>
        <li key={`${entry.operation}-${entry.recordedAt}`}><strong>{entry.operation}</strong>
          <time>{new Date(entry.recordedAt).toLocaleString()}</time>{entry.relayedAt ? " · relayed" : " · pending relay"}</li>)}</ol>}
    </section>
  </main>;
}

function Kpi({ label, value }: { label: string; value: number }) {
  return <article><strong>{value}</strong><span>{label}</span></article>;
}

async function getJson<T>(url: string): Promise<T> {
  const response = await fetch(url, { credentials: "same-origin" });
  if (!response.ok) throw new Error(`Request failed (${response.status}).`);
  return await response.json() as T;
}

async function mutate<T>(url: string, body: unknown): Promise<T> {
  const csrf = await getJson<{ token: string }>("/api/v1/session/csrf");
  const response = await fetch(url, {
    method: "POST",
    credentials: "same-origin",
    headers: { "content-type": "application/json", "X-CSRF-TOKEN": csrf.token,
      "Idempotency-Key": crypto.randomUUID() },
    body: JSON.stringify(body)
  });
  if (!response.ok) throw new Error(`Mutation failed (${response.status}).`);
  return await response.json() as T;
}

const showError = (setError: (message: string) => void) => (reason: unknown) =>
  setError(reason instanceof Error ? reason.message : String(reason));

createRoot(document.getElementById("root")!).render(<StrictMode><App /></StrictMode>);
