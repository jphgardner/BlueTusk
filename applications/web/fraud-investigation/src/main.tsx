import { StrictMode, useEffect, useMemo, useState } from "react";
import { createRoot } from "react-dom/client";
import {
  BlueTuskLiveClient,
  type LiveClientQueryDocument,
  type LiveClientRow,
  type LiveQueryState
} from "@bluetusk/live";
import "./styles.css";

type Session = { tenant: string; name: string };
type Account = { id: string; displayName: string };
type Transfer = { id: string; sourceId: string; destinationId: string; amount: number; currency: string };
type AlertRule = { id: string; name: string; minimumAmount: number; enabled: boolean };
type InvestigationCase = { id: string; reason: string; assignee?: string; decision: string | number; version: number };
type SuspiciousPath = { accountIds: readonly string[]; transferIds: readonly string[]; totalAmount: number };
type Evidence = { operation: string; actor: string; detail: string; recordedAt: string };

const client = new BlueTuskLiveClient({ endpoint: "/api/v1/live", credentials: "same-origin" });

function App() {
  const [session, setSession] = useState<Session | null>(null);
  const [accounts, setAccounts] = useState<readonly Account[]>([]);
  const [transfers, setTransfers] = useState<readonly Transfer[]>([]);
  const [rules, setRules] = useState<readonly AlertRule[]>([]);
  const [cases, setCases] = useState<readonly InvestigationCase[]>([]);
  const [reason, setReason] = useState("");
  const [accountName, setAccountName] = useState("");
  const [sourceId, setSourceId] = useState("");
  const [destinationId, setDestinationId] = useState("");
  const [amount, setAmount] = useState("25000");
  const [ruleName, setRuleName] = useState("High-value multi-hop path");
  const [ruleMinimum, setRuleMinimum] = useState("10000");
  const [paths, setPaths] = useState<readonly SuspiciousPath[]>([]);
  const [evidence, setEvidence] = useState<Record<string, readonly Evidence[]>>({});
  const [live, setLive] = useState<LiveQueryState<LiveClientRow>>({
    phase: "idle", rows: [], lastSequence: 0, error: null
  });

  useEffect(() => {
    void Promise.all([
      getJson<Session>("/api/v1/session"),
      getJson<readonly Account[]>("/api/v1/fraud/accounts"),
      getJson<readonly Transfer[]>("/api/v1/fraud/transfers"),
      getJson<readonly AlertRule[]>("/api/v1/fraud/alert-rules"),
      getJson<readonly InvestigationCase[]>("/api/v1/fraud/cases")
    ]).then(([currentSession, currentAccounts, currentTransfers, currentRules, currentCases]) => {
      setSession(currentSession);
      setAccounts(currentAccounts);
      setTransfers(currentTransfers);
      setRules(currentRules);
      setCases(currentCases);
      setSourceId(currentAccounts[0]?.id ?? "");
      setDestinationId(currentAccounts[1]?.id ?? currentAccounts[0]?.id ?? "");
    });
  }, []);

  const document = useMemo<LiveClientQueryDocument>(() => ({
    language: "linq",
    linq: {
      schema: "fraud",
      table: "investigation_cases",
      columns: ["Id", "TenantId", "Reason", "Assignee", "Decision", "Version", "OpenedAt"],
      filters: [{ column: "TenantId", operator: "Equal", parameter: "tenant" }],
      orderings: [{ column: "OpenedAt", direction: "Descending" }]
    },
    keyColumns: ["Id"],
    maximumResultCount: 1000,
    parameters: { tenant: { type: "string", value: session?.tenant ?? "" } }
  }), [session?.tenant]);

  useEffect(() => {
    if (!session) return;
    const query = client.createQuery<LiveClientRow, string, LiveClientQueryDocument>({
      query: "fraud-live", parameters: document
    });
    const unsubscribe = query.subscribe(setLive);
    query.start();
    return () => { unsubscribe(); query.stop(); };
  }, [document, session]);

  async function registerAccount(event: React.FormEvent) {
    event.preventDefault();
    const created = await mutate<Account>("/api/v1/fraud/accounts", { displayName: accountName });
    setAccounts(current => [...current, created]);
    if (!sourceId) setSourceId(created.id);
    else if (!destinationId || destinationId === sourceId) setDestinationId(created.id);
    setAccountName("");
  }

  async function recordTransfer(event: React.FormEvent) {
    event.preventDefault();
    const created = await mutate<Transfer>("/api/v1/fraud/transfers", {
      sourceId, destinationId, amount: Number(amount), currency: "GBP"
    });
    setTransfers(current => [created, ...current]);
  }

  async function createRule(event: React.FormEvent) {
    event.preventDefault();
    const created = await mutate<AlertRule>("/api/v1/fraud/alert-rules", {
      name: ruleName, minimumAmount: Number(ruleMinimum)
    });
    setRules(current => [created, ...current]);
  }

  async function analysePaths() {
    if (!sourceId) return;
    setPaths(await getJson<readonly SuspiciousPath[]>(
      `/api/v1/fraud/accounts/${sourceId}/suspicious-paths?maximumHops=4&minimumTotal=${encodeURIComponent(ruleMinimum)}`));
  }

  async function openCase(event: React.FormEvent) {
    event.preventDefault();
    const opened = await mutate<InvestigationCase>("/api/v1/fraud/cases", { reason });
    setCases(current => [opened, ...current]);
    setReason("");
  }

  async function assign(item: InvestigationCase) {
    const updated = await mutate<InvestigationCase>(`/api/v1/fraud/cases/${item.id}/assignment`, {
      assignee: session?.name ?? "RC investigator", expectedVersion: item.version
    });
    replaceCase(updated);
  }

  async function decide(item: InvestigationCase) {
    const updated = await mutate<InvestigationCase>(`/api/v1/fraud/cases/${item.id}/decision`, {
      decision: 2, note: "Confirmed through the investigated multi-hop path.", expectedVersion: item.version
    });
    replaceCase(updated);
  }

  async function loadEvidence(item: InvestigationCase) {
    const entries = await getJson<readonly Evidence[]>(`/api/v1/fraud/cases/${item.id}/evidence`);
    setEvidence(current => ({ ...current, [item.id]: entries }));
  }

  function replaceCase(updated: InvestigationCase) {
    setCases(current => current.map(item => item.id === updated.id ? updated : item));
  }

  return <main>
    <nav><div><span className="mark">BT</span><strong>Fraud Graph Investigator</strong></div>
      <span>{session?.name ?? "Secure session"}</span></nav>
    <header><p>ContinuousGraph operations</p><h1>Follow the money.<br />Keep the evidence.</h1>
      <span className={`connection ${live.phase}`}>● {live.phase}</span></header>
    <section className="stats"><article><b>{cases.length}</b>Investigation cases</article>
      <article><b>{accounts.length}</b>Graph accounts</article><article><b>{live.lastSequence}</b>Replay sequence</article></section>

    <section className="operations">
      <article><h2>Accounts</h2><form onSubmit={registerAccount}>
        <label>Display name<input required value={accountName} onChange={event => setAccountName(event.target.value)} /></label>
        <button>Register account</button></form><ul>{accounts.map(account => <li key={account.id}>{account.displayName}</li>)}</ul></article>
      <article><h2>Transfers</h2><form onSubmit={recordTransfer}>
        <label>Source<select required value={sourceId} onChange={event => setSourceId(event.target.value)}>
          {accounts.map(account => <option key={account.id} value={account.id}>{account.displayName}</option>)}</select></label>
        <label>Destination<select required value={destinationId} onChange={event => setDestinationId(event.target.value)}>
          {accounts.map(account => <option key={account.id} value={account.id}>{account.displayName}</option>)}</select></label>
        <label>Amount (GBP)<input type="number" min="0.01" step="0.01" required value={amount} onChange={event => setAmount(event.target.value)} /></label>
        <button disabled={accounts.length < 2 || sourceId === destinationId}>Record transfer</button></form>
        <small>{transfers.length} retained transfer(s)</small></article>
      <article><h2>Alert rules & paths</h2><form onSubmit={createRule}>
        <label>Rule name<input required value={ruleName} onChange={event => setRuleName(event.target.value)} /></label>
        <label>Minimum total<input type="number" min="1" required value={ruleMinimum} onChange={event => setRuleMinimum(event.target.value)} /></label>
        <button>Create rule</button></form><button className="secondary" onClick={analysePaths} disabled={!sourceId}>Analyse multi-hop paths</button>
        <small>{rules.length} rule(s) · {paths.length} suspicious path(s)</small>
        {paths.map((path, index) => <p className="path" key={`${path.transferIds.join("-")}-${index}`}>{path.accountIds.length - 1} hops · £{path.totalAmount.toLocaleString()}</p>)}</article>
    </section>

    <section className="workspace"><aside><h2>Open investigation</h2><form onSubmit={openCase}>
      <label>Evidence summary<textarea required value={reason} onChange={event => setReason(event.target.value)} /></label>
      <button>Open case</button></form><p>Assignments and decisions are versioned and retained in an immutable evidence audit.</p></aside>
      <div><h2>Investigation queue</h2>{cases.map(item => <article className="case" key={item.id}>
        <div><strong>{item.reason}</strong><small>{item.assignee ?? "Unassigned"} · v{item.version}</small>
          {evidence[item.id]?.map(entry => <small key={`${entry.operation}-${entry.recordedAt}`}>{entry.operation}: {entry.detail} — {entry.actor}</small>)}</div>
        <div className="case-actions"><span>{String(item.decision)}</span>
          {!item.assignee && <button onClick={() => void assign(item)}>Assign to me</button>}
          <button onClick={() => void decide(item)}>Mark suspicious</button>
          <button className="secondary" onClick={() => void loadEvidence(item)}>Evidence</button></div></article>)}</div></section>
  </main>;
}

async function getJson<T>(url: string): Promise<T> {
  const response = await fetch(url, { credentials: "same-origin" });
  if (!response.ok) throw new Error(`Request failed (${response.status}).`);
  return await response.json() as T;
}

async function mutate<T>(url: string, body: unknown): Promise<T> {
  const csrf = await getJson<{ token: string }>("/api/v1/session/csrf");
  const response = await fetch(url, { method: "POST", credentials: "same-origin",
    headers: { "content-type": "application/json", "X-CSRF-TOKEN": csrf.token },
    body: JSON.stringify(body) });
  if (!response.ok) throw new Error(`Mutation failed (${response.status}).`);
  return await response.json() as T;
}

createRoot(document.getElementById("root")!).render(<StrictMode><App /></StrictMode>);
