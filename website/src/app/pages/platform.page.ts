import { Component, computed, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { PRODUCT_STATUSES, sourceUrl } from '../content/catalog';
import { SourceLink, StatusPill } from '../shared/technical-ui';

@Component({
  selector: 'bt-platform-page',
  imports: [RouterLink, MatButtonModule, MatIconModule, SourceLink, StatusPill],
  template: `
    <section class="page-hero split-hero">
      <div>
        <span class="eyebrow"><i class="live-dot"></i> HOW BLUETUSK FITS TOGETHER</span>
        <h1>Everything your .NET app needs.<br /><em>One PostgreSQL platform.</em></h1>
        <p>
          Start with fast, familiar data access. Add EF Core, change streams, live updates,
          extensions, or graph features when your application needs them.
        </p>
        <div class="hero-actions">
          <a
            mat-flat-button
            routerLink="/documentation/getting-started/architecture"
            class="primary-action"
            >See how it works</a
          ><a mat-stroked-button routerLink="/evidence" class="secondary-action">View proof</a>
        </div>
      </div>
      <aside class="signal-card">
        <small>A SIMPLE DESIGN RULE</small
        ><strong>Each part does one job.<br />Your app stays in control.</strong>
        <p>
          Use only the product areas you need. BlueTusk keeps the lower-level database work separate
          from your application and business code.
        </p>
        <bt-source-link [href]="source('docs/architecture/overview.md')" />
      </aside>
    </section>

    <section class="page-section">
      <header class="section-head">
        <div>
          <span>EXPLORE THE PLATFORM</span>
          <h2>See what each part does.</h2>
        </div>
        <p>Select a part to see what it does and what it connects to.</p>
      </header>
      <div class="layer-explorer">
        <nav aria-label="Architecture layers">
          @for (layer of layers; track layer.id) {
            <button
              type="button"
              [class.active]="selectedLayer() === layer.id"
              (click)="selectedLayer.set(layer.id)"
            >
              <mat-icon>{{ layer.icon }}</mat-icon
              ><span
                ><strong>{{ layer.name }}</strong
                ><small>{{ layer.role }}</small></span
              ><mat-icon>chevron_right</mat-icon>
            </button>
          }
        </nav>
        <article class="layer-detail">
          <span class="index-label">{{ activeLayer().index }} / {{ layers.length }}</span>
          <mat-icon>{{ activeLayer().icon }}</mat-icon>
          <h3>{{ activeLayer().name }}</h3>
          <p>{{ activeLayer().description }}</p>
          <div class="detail-columns">
            <div>
              <small>WHAT IT HANDLES</small>
              @for (item of activeLayer().owns; track item) {
                <span>{{ item }}</span>
              }
            </div>
            <div>
              <small>WHAT IT USES</small>
              @for (item of activeLayer().depends; track item) {
                <span>{{ item }}</span>
              }
            </div>
          </div>
        </article>
      </div>
    </section>

    <section class="page-section">
      <header class="section-head">
        <div>
          <span>FOLLOW THE DATA</span>
          <h2>See how work moves through BlueTusk.</h2>
        </div>
        <p>Follow a query, a database change, or a live update from start to finish.</p>
      </header>
      <div class="journey-grid">
        @for (journey of journeys; track journey.title) {
          <article>
            <mat-icon>{{ journey.icon }}</mat-icon
            ><small>{{ journey.kicker }}</small>
            <h3>{{ journey.title }}</h3>
            <div class="journey-flow">
              @for (step of journey.steps; track step) {
                <span>{{ step }}</span>
              }
            </div>
            <p>{{ journey.detail }}</p>
          </article>
        }
      </div>
    </section>

    <section class="page-section">
      <header class="section-head">
        <div>
          <span>RELEASE STATUS</span>
          <h2>Know what is ready today.</h2>
        </div>
        <p>
          We show public availability, completed testing, and remaining stable-release work
          separately so you can make an informed choice.
        </p>
      </header>
      <div class="status-table">
        @for (product of statuses; track product.id) {
          <article>
            <div>
              <strong>{{ product.name }}</strong
              ><small>{{ product.version }}</small>
            </div>
            <bt-status [label]="product.gateState" [stage]="product.stage" /><span>{{
              product.packageState
            }}</span>
            <p>{{ product.limitations[0] }}</p>
          </article>
        }
      </div>
    </section>

    <section class="crosscut-band">
      @for (item of crosscutting; track item.title) {
        <article>
          <mat-icon>{{ item.icon }}</mat-icon>
          <div>
            <small>{{ item.kicker }}</small>
            <h3>{{ item.title }}</h3>
            <p>{{ item.body }}</p>
          </div>
        </article>
      }
    </section>
  `,
})
export class PlatformPage {
  protected readonly source = sourceUrl;
  protected readonly statuses = PRODUCT_STATUSES;
  protected readonly selectedLayer = signal('applications');
  protected readonly layers = [
    {
      id: 'applications',
      index: '01',
      name: '.NET applications',
      role: 'Your code',
      icon: 'developer_mode',
      description: 'Your application chooses how it reads data, writes data, or receives updates.',
      owns: ['Business rules', 'User permissions', 'What each update means'],
      depends: ['EF Core', 'ADO.NET', 'Live updates'],
    },
    {
      id: 'ef',
      index: '02',
      name: 'EF Core + extensions',
      role: 'Data models and LINQ',
      icon: 'data_object',
      description:
        'Use DbContext, LINQ, migrations, and database-first tooling with PostgreSQL features.',
      owns: ['LINQ to SQL', 'Data mappings', 'Migrations and tooling'],
      depends: ['BlueTusk Provider', 'Optional extensions'],
    },
    {
      id: 'provider',
      index: '03',
      name: 'ADO.NET Provider',
      role: 'Data access',
      icon: 'storage',
      description:
        'Open connections, run commands, reuse connection pools, and work with PostgreSQL types.',
      owns: ['ADO.NET APIs', 'Connection pools', 'PostgreSQL types'],
      depends: ['Database communication', 'Network connection'],
    },
    {
      id: 'protocol',
      index: '04',
      name: 'Database communication',
      role: 'Secure network layer',
      icon: 'lan',
      description:
        'Handles login, TLS, network reads and writes, cancellation, and efficient batching.',
      owns: ['PostgreSQL messages', 'Memory use', 'Command cancellation'],
      depends: ['.NET pipelines', 'Sockets and TLS'],
    },
    {
      id: 'realtime',
      index: '05',
      name: 'Real-time products',
      role: 'Changes after commit',
      icon: 'stream',
      description:
        'Turns committed database changes into recoverable updates for systems, users, and graphs.',
      owns: ['Where changes came from', 'Safe recovery position', 'Stored replay data'],
      depends: ['PostgreSQL replication', 'PostgreSQL storage'],
    },
    {
      id: 'operations',
      index: '06',
      name: 'Operations + Kubernetes',
      role: 'Run a fleet safely',
      icon: 'deployed_code',
      description:
        'BlueTusk 1.2 adds a Kubernetes operator and one redacted fleet view for controlled day-two operations.',
      owns: ['Desired deployment state', 'Reconciliation', 'Audited operator actions'],
      depends: ['Control Plane', 'Kubernetes custom resources'],
    },
  ] as const;
  protected readonly activeLayer = computed(
    () => this.layers.find((x) => x.id === this.selectedLayer()) ?? this.layers[0],
  );
  protected readonly journeys = [
    {
      kicker: 'READ OR WRITE DATA',
      title: 'Run an application query',
      icon: 'sync_alt',
      steps: ['DbContext', 'Provider', 'Network', 'PostgreSQL'],
      detail: 'Your .NET types and PostgreSQL features remain available from start to finish.',
    },
    {
      kicker: 'START REAL-TIME SAFELY',
      title: 'Load existing data, then follow changes',
      icon: 'timeline',
      steps: ['Existing data', 'Starting position', 'New changes', 'Saved progress'],
      detail:
        'BlueTusk records the handover point so no change is missed between the initial load and live updates.',
    },
    {
      kicker: 'KEEP SYSTEMS UPDATED',
      title: 'Send each change to its destination',
      icon: 'account_tree',
      steps: ['Store', 'Transform', 'Write', 'Check'],
      detail:
        'Each destination uses its own durable checkpoint and retry rules; 1.2 adds Kafka, S3/Parquet, and signed webhooks.',
    },
    {
      kicker: 'OPERATE A FLEET · 1.2',
      title: 'Declare, review, and reconcile deployments',
      icon: 'deployed_code',
      steps: ['Custom resource', 'Safety checks', 'Fenced reconcile', 'Redacted status'],
      detail:
        'The operator adds finalizers before mutation, uses compare-and-swap updates, and never reads Kubernetes Secret values.',
    },
  ] as const;
  protected readonly crosscutting = [
    {
      icon: 'shield',
      kicker: 'SECURITY',
      title: 'Security responsibilities are clear',
      body: 'The guides explain how connections, credentials, permissions, and application security fit together.',
    },
    {
      icon: 'monitoring',
      kicker: 'OBSERVABILITY',
      title: 'See what the system is doing',
      body: 'Metrics and traces show slow commands, saved progress, replication delay, and operational health.',
    },
    {
      icon: 'verified',
      kicker: 'COMPATIBILITY',
      title: 'Compatibility is checked automatically',
      body: 'Automated checks protect public APIs, stored data formats, and supported PostgreSQL versions.',
    },
    {
      icon: 'rocket_launch',
      kicker: 'STARTER · 1.2',
      title: 'Begin from a production-shaped application',
      body: 'The Clean Architecture starter includes API, worker, migrations, tests, telemetry, containers, Helm, SLOs, and runbooks.',
    },
  ] as const;
}
