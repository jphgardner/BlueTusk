import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, inject, signal } from "@angular/core";
import { bootstrapApplication } from "@angular/platform-browser";
import { BlueTuskLiveClient, type LiveClientQueryDocument, type LiveClientRow } from "@bluetusk/live";
import { AngularLiveQuery, BlueTuskLiveAngular, provideBlueTuskLive } from "@bluetusk/live-angular";

type Session = { tenant: string; name: string };
type OrderState = "Created" | "Allocated" | "Picked" | "Shipped" | "Cancelled";
type Order = {
  id: string;
  customerReference: string;
  state: OrderState;
  version: number;
  updatedAt: string;
};
type TimelineEntry = { operation: string; recordedAt: string; relayedAt?: string };

@Component({
  selector: "app-root",
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main>
      <header>
        <div><p class="eyebrow">BlueTusk production starter</p><h1>Order fulfilment operations</h1></div>
        <span>{{ session()?.name ?? 'Connecting' }} · Live {{ live?.phase() ?? 'connecting' }}</span>
      </header>
      @if (error()) { <aside role="alert">{{ error() }}</aside> }
      <section class="kpis">
        <article><strong>{{ openCount() }}</strong><span>Open</span></article>
        <article><strong>{{ shippedCount() }}</strong><span>Shipping today</span></article>
        <article><strong>{{ liveRowsCount() }}</strong><span>Live rows</span></article>
      </section>
      <section class="panel">
        <form (submit)="searchOrders($event)">
          <label>Search orders<input [value]="search()" (input)="search.set($any($event.target).value)"></label>
          <button>Search</button>
        </form>
        <form (submit)="createOrder($event)">
          <label>Customer reference<input required [value]="reference()"
            (input)="reference.set($any($event.target).value)"></label>
          <button>Create order</button>
        </form>
        <table>
          <thead><tr><th>Reference</th><th>State</th><th>Version</th><th>Updated</th><th>Actions</th></tr></thead>
          <tbody>
            @for (order of orders(); track order.id) {
              <tr><td>{{ order.customerReference }}</td><td><span class="state">{{ order.state }}</span></td>
                <td>{{ order.version }}</td><td>{{ order.updatedAt }}</td><td class="actions">
                  @if (order.state === 'Created') { <button (click)="transition(order, 'allocate')">Allocate</button> }
                  @if (order.state === 'Allocated') { <button (click)="transition(order, 'pick')">Pick</button> }
                  @if (order.state === 'Picked') { <button (click)="transition(order, 'ship')">Ship</button> }
                  @if (order.state !== 'Shipped' && order.state !== 'Cancelled') {
                    <button (click)="transition(order, 'cancel')">Cancel</button>
                  }
                  <button (click)="showTimeline(order)">Timeline</button>
                </td></tr>
            }
          </tbody>
        </table>
        @if (timeline().length > 0) {
          <ol aria-label="Order timeline">
            @for (entry of timeline(); track entry.operation + entry.recordedAt) {
              <li><strong>{{ entry.operation }}</strong> · {{ entry.recordedAt }} ·
                {{ entry.relayedAt ? 'relayed' : 'pending relay' }}</li>
            }
          </ol>
        }
      </section>
    </main>
  `
})
class AppComponent implements OnInit, OnDestroy {
  private readonly adapter = inject(BlueTuskLiveAngular);
  readonly session = signal<Session | null>(null);
  readonly orders = signal<readonly Order[]>([]);
  readonly reference = signal("");
  readonly search = signal("");
  readonly timeline = signal<readonly TimelineEntry[]>([]);
  readonly error = signal<string | null>(null);
  live?: AngularLiveQuery<LiveClientRow, string, LiveClientQueryDocument>;

  openCount(): number {
    return this.orders().filter(order => order.state !== "Shipped" && order.state !== "Cancelled").length;
  }

  shippedCount(): number { return this.orders().filter(order => order.state === "Shipped").length; }
  liveRowsCount(): number { return this.live?.rows().length ?? 0; }

  async ngOnInit(): Promise<void> {
    try {
      const [session, orders] = await Promise.all([
        getJson<Session>("/api/v1/session"),
        getJson<readonly Order[]>("/api/v1/orders")
      ]);
      this.session.set(session);
      this.orders.set(orders);
      this.live = this.adapter.createQuery({
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
          parameters: { tenant: { type: "string", value: session.tenant } }
        }
      });
      this.live.start();
    } catch (reason) { this.showError(reason); }
  }

  ngOnDestroy(): void { this.live?.destroy(); }

  async createOrder(event: Event): Promise<void> {
    event.preventDefault();
    try {
      const created = await mutate<Order>("/api/v1/orders", { customerReference: this.reference() });
      this.orders.update(current => [created, ...current]);
      this.reference.set("");
    } catch (reason) { this.showError(reason); }
  }

  async searchOrders(event: Event): Promise<void> {
    event.preventDefault();
    try {
      this.orders.set(await getJson<readonly Order[]>(`/api/v1/orders?query=${encodeURIComponent(this.search())}`));
    } catch (reason) { this.showError(reason); }
  }

  async transition(order: Order, action: "allocate" | "pick" | "ship" | "cancel"): Promise<void> {
    try {
      const changed = await mutate<Order>(`/api/v1/orders/${order.id}/${action}`, {
        expectedVersion: order.version,
        allocationReference: action === "allocate" ? `ALLOC-${order.customerReference}` : null
      });
      this.orders.update(current => current.map(item => item.id === changed.id ? changed : item));
    } catch (reason) { this.showError(reason); }
  }

  async showTimeline(order: Order): Promise<void> {
    try { this.timeline.set(await getJson<readonly TimelineEntry[]>(`/api/v1/orders/${order.id}/timeline`)); }
    catch (reason) { this.showError(reason); }
  }

  private showError(reason: unknown): void {
    this.error.set(reason instanceof Error ? reason.message : String(reason));
  }
}

const client = new BlueTuskLiveClient({ endpoint: "/api/v1/live", credentials: "same-origin" });
void bootstrapApplication(AppComponent, { providers: [provideBlueTuskLive(client)] });

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
    headers: {
      "content-type": "application/json",
      "X-CSRF-TOKEN": csrf.token,
      "Idempotency-Key": crypto.randomUUID()
    },
    body: JSON.stringify(body)
  });
  if (!response.ok) throw new Error(`Mutation failed (${response.status}).`);
  return await response.json() as T;
}
