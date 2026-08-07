import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, inject, signal } from "@angular/core";
import { bootstrapApplication } from "@angular/platform-browser";
import { BlueTuskLiveClient, type LiveClientQueryDocument, type LiveClientRow } from "@bluetusk/live";
import { AngularLiveQuery, BlueTuskLiveAngular, provideBlueTuskLive } from "@bluetusk/live-angular";

type Session = { tenant: string; name: string };
type Service = { id: string; name: string; health: string; version: number; updatedAt: string };
type Dependency = { id: string; sourceId: string; destinationId: string };
type Incident = { id: string; serviceId: string; summary: string; openedAt: string };

@Component({
  selector: "app-root",
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav><strong>BlueTusk / Topology</strong><span>{{ session()?.name ?? 'Secure session' }}</span></nav>
    <main>
      <header><div><p>PostgreSQL 19 RC staging</p><h1>Service Topology Centre</h1></div>
        <span class="live">● {{ live?.phase() ?? 'connecting' }}</span></header>
      <section class="summary"><article><b>{{ services().length }}</b><span>Services</span></article>
        <article><b>{{ degradedCount() }}</b><span>Degraded</span></article>
        <article><b>{{ liveRowsCount() }}</b><span>Live nodes</span></article></section>
      <section class="canvas">
        <div class="toolbar"><h2>Dependency map</h2><form (submit)="register($event)">
          <input name="name" required placeholder="New service name" [value]="newName()"
            (input)="newName.set($any($event.target).value)" /><button>Register</button></form></div>
        <div class="nodes">
          @for (service of services(); track service.id) {
            <article class="node" [class.degraded]="service.health !== 'Healthy' && service.health !== 'Unknown'">
              <span class="pulse"></span><div><strong>{{ service.name }}</strong><small>{{ service.health }}</small></div>
              <em>v{{ service.version }}</em><button (click)="analyse(service)">Blast radius</button>
              <button (click)="openIncident(service)">Incident</button>
            </article>
          } @empty { <p class="empty">Register a service to begin mapping dependencies.</p> }
        </div>
        @if (services().length > 1) {
          <form class="dependency" (submit)="connect($event)">
            <label>Caller<select [value]="sourceId()" (change)="sourceId.set($any($event.target).value)">
              @for (service of services(); track service.id) { <option [value]="service.id">{{ service.name }}</option> }
            </select></label>
            <label>Dependency<select [value]="destinationId()" (change)="destinationId.set($any($event.target).value)">
              @for (service of services(); track service.id) { <option [value]="service.id">{{ service.name }}</option> }
            </select></label><button>Connect</button>
          </form>
        }
        @if (analysis()) { <p role="status">{{ analysis() }}</p> }
        <section><h2>Open incidents</h2>@for (incident of incidents(); track incident.id) {
          <article><strong>{{ incident.summary }}</strong> · {{ serviceName(incident.serviceId) }}</article>
        } @empty { <p>No open incidents.</p> }</section>
      </section>
    </main>`,
})
class AppComponent implements OnInit, OnDestroy {
  private readonly adapter = inject(BlueTuskLiveAngular);
  readonly session = signal<Session | null>(null);
  readonly services = signal<readonly Service[]>([]);
  readonly newName = signal("");
  readonly dependencies = signal<readonly Dependency[]>([]);
  readonly incidents = signal<readonly Incident[]>([]);
  readonly sourceId = signal("");
  readonly destinationId = signal("");
  readonly analysis = signal("");
  live?: AngularLiveQuery<LiveClientRow, string, LiveClientQueryDocument>;

  degradedCount(): number {
    return this.services().filter(service => service.health === "Degraded" || service.health === "Unavailable").length;
  }

  liveRowsCount(): number { return this.live === undefined ? 0 : this.live.rows().length; }

  async ngOnInit(): Promise<void> {
    const [session, services, dependencies, incidents] = await Promise.all([
      getJson<Session>("/api/v1/session"),
      getJson<readonly Service[]>("/api/v1/topology/services"),
      getJson<readonly Dependency[]>("/api/v1/topology/dependencies"),
      getJson<readonly Incident[]>("/api/v1/topology/incidents")
    ]);
    this.session.set(session);
    this.services.set(services);
    this.dependencies.set(dependencies);
    this.incidents.set(incidents);
    this.sourceId.set(services[0]?.id ?? "");
    this.destinationId.set(services[1]?.id ?? services[0]?.id ?? "");
    this.live = this.adapter.createQuery({
      query: "topology-live",
      parameters: {
        language: "linq",
        linq: {
          schema: "topology", table: "services",
          columns: ["Id", "TenantId", "Name", "Health", "Version", "UpdatedAt"],
          filters: [{ column: "TenantId", operator: "Equal", parameter: "tenant" }],
          orderings: [{ column: "Name", direction: "Ascending" }]
        },
        keyColumns: ["Id"], maximumResultCount: 1000,
        parameters: { tenant: { type: "string", value: session.tenant } }
      }
    });
    this.live.start();
  }

  ngOnDestroy(): void { this.live?.destroy(); }

  async register(event: Event): Promise<void> {
    event.preventDefault();
    const created = await mutate<Service>("/api/v1/topology/services", { name: this.newName() });
    this.services.update(items => [...items, created].sort((a, b) => a.name.localeCompare(b.name)));
    if (!this.sourceId()) this.sourceId.set(created.id);
    else if (!this.destinationId() || this.destinationId() === this.sourceId()) this.destinationId.set(created.id);
    this.newName.set("");
  }

  async connect(event: Event): Promise<void> {
    event.preventDefault();
    const created = await mutate<Dependency>("/api/v1/topology/dependencies", {
      sourceId: this.sourceId(), destinationId: this.destinationId()
    });
    this.dependencies.update(items => [...items, created]);
  }

  async analyse(service: Service): Promise<void> {
    const affected = await getJson<readonly string[]>(`/api/v1/topology/services/${service.id}/blast-radius`);
    this.analysis.set(`${service.name} affects ${affected.length} upstream service(s).`);
  }

  async openIncident(service: Service): Promise<void> {
    const created = await mutate<Incident>(`/api/v1/topology/services/${service.id}/incidents`, {
      summary: `${service.name} operational incident`
    });
    this.incidents.update(items => [created, ...items]);
  }

  serviceName(id: string): string { return this.services().find(service => service.id === id)?.name ?? id; }
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
  const response = await fetch(url, { method: "POST", credentials: "same-origin",
    headers: { "content-type": "application/json", "X-CSRF-TOKEN": csrf.token },
    body: JSON.stringify(body) });
  if (!response.ok) throw new Error(`Mutation failed (${response.status}).`);
  return await response.json() as T;
}
