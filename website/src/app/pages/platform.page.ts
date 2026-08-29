import { Component, computed, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { sourceUrl } from '../content/catalog';
import { SourceLink } from '../shared/technical-ui';

@Component({
  selector: 'bt-platform-page',
  imports: [RouterLink, MatButtonModule, MatIconModule, SourceLink],
  template: `
    <section class="page-hero split-hero">
      <div>
        <span class="eyebrow"><i class="live-dot"></i> PLATFORM MAP</span>
        <h1>Start with data access.<br /><em>Add only what the workload needs.</em></h1>
        <p>
          BlueTusk is a set of focused PostgreSQL products for .NET. The Provider is the foundation;
          EF Core, real-time delivery, extensions, and graph features are optional layers with
          explicit ownership.
        </p>
        <div class="hero-actions">
          <a
            mat-flat-button
            routerLink="/documentation/getting-started/quickstart"
            class="primary-action"
            >Run the quickstart</a
          ><a
            mat-stroked-button
            routerLink="/documentation/getting-started/architecture"
            class="secondary-action"
            >Read the architecture guide</a
          >
        </div>
      </div>
      <aside class="signal-card">
        <small>THE DEFAULT PATH</small>
        <strong>Provider first.<br />Everything else is a choice.</strong>
        <p>
          Add EF Core for model-driven data access. Add Streams only when committed changes must
          leave PostgreSQL. Add an extension or graph package only for that specific workload.
        </p>
        <bt-source-link
          [href]="source('docs/architecture/overview.md')"
          label="Architecture source"
        />
      </aside>
    </section>

    <section class="page-section">
      <header class="section-head">
        <div>
          <span>OWNERSHIP BY LAYER</span>
          <h2>Know which part is responsible.</h2>
        </div>
        <p>Select a layer to see what it owns and what it builds on.</p>
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
              <small>OWNS</small>
              @for (item of activeLayer().owns; track $index) {
                <span>{{ item }}</span>
              }
            </div>
            <div>
              <small>BUILDS ON</small>
              @for (item of activeLayer().depends; track $index) {
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
          <span>CHOOSE A PATH</span>
          <h2>Start from the outcome, not the product list.</h2>
        </div>
        <p>Each path has one clear entry point and a focused technical guide.</p>
      </header>
      <div class="journey-grid journey-grid-two">
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
            <a [routerLink]="journey.route">{{ journey.action }}</a>
          </article>
        }
      </div>
    </section>

    <section class="crosscut-band">
      @for (item of principles; track item.title) {
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
  protected readonly selectedLayer = signal('applications');
  protected readonly layers = [
    {
      id: 'applications',
      index: '01',
      name: '.NET application',
      role: 'Business behavior and authorization',
      icon: 'developer_mode',
      description:
        'Your application decides what data means, who may see it, and which BlueTusk products it actually needs.',
      owns: ['Business rules', 'User authorization', 'Application lifecycle'],
      depends: ['ADO.NET, EF Core, or Live', 'Application configuration'],
    },
    {
      id: 'ef',
      index: '02',
      name: 'EF Core',
      role: 'Models, LINQ, and schema changes',
      icon: 'data_object',
      description:
        'Use DbContext, LINQ, migrations, and database-first tooling while retaining PostgreSQL-specific features.',
      owns: ['Query translation', 'Relational mappings', 'Migrations and scaffolding'],
      depends: ['BlueTusk Provider', 'Optional EF extension packages'],
    },
    {
      id: 'provider',
      index: '03',
      name: 'ADO.NET Provider',
      role: 'Connections and database operations',
      icon: 'storage',
      description:
        'The Provider owns pools, commands, transactions, PostgreSQL types, COPY, notifications, and replication sessions.',
      owns: ['ADO.NET APIs', 'Connection pools', 'PostgreSQL type catalogue'],
      depends: ['Protocol and transport', 'PostgreSQL capabilities'],
    },
    {
      id: 'protocol',
      index: '04',
      name: 'Protocol + transport',
      role: 'Secure PostgreSQL communication',
      icon: 'lan',
      description:
        'The lowest layer handles authentication, TLS, PostgreSQL messages, cancellation, and bounded network buffers.',
      owns: ['Wire protocol', 'Network deadlines', 'Memory boundaries'],
      depends: ['Sockets and TLS', 'PostgreSQL server behavior'],
    },
    {
      id: 'realtime',
      index: '05',
      name: 'Real-time products',
      role: 'Work after a transaction commits',
      icon: 'stream',
      description:
        'Streams captures committed changes; Sync, Live, Control Plane, and Continuous Graph consume those changes for different outcomes.',
      owns: ['Source identity', 'Checkpoints and replay', 'Delivery-specific recovery'],
      depends: ['Logical replication', 'Durable application state'],
    },
  ] as const;
  protected readonly activeLayer = computed(
    () => this.layers.find((layer) => layer.id === this.selectedLayer()) ?? this.layers[0],
  );
  protected readonly journeys = [
    {
      kicker: 'DIRECT DATA ACCESS',
      title: 'Run commands and transactions',
      icon: 'terminal',
      steps: ['Data source', 'Connection', 'Command', 'PostgreSQL'],
      detail: 'Choose the Provider when the application needs direct control over database work.',
      route: '/provider',
      action: 'Explore Provider',
    },
    {
      kicker: 'MODEL-DRIVEN DATA ACCESS',
      title: 'Use LINQ and migrations',
      icon: 'data_object',
      steps: ['DbContext', 'LINQ', 'Provider', 'PostgreSQL'],
      detail: 'Choose EF Core for relational models, query translation, and schema evolution.',
      route: '/ef-core',
      action: 'Explore EF Core',
    },
    {
      kicker: 'COMMITTED CHANGES',
      title: 'Update systems or users',
      icon: 'stream',
      steps: ['PostgreSQL', 'Streams', 'Sync or Live', 'Destination'],
      detail: 'Choose the real-time products only when work must continue after commit.',
      route: '/real-time',
      action: 'Explore Real Time',
    },
    {
      kicker: 'SPECIALIZED POSTGRESQL',
      title: 'Add an extension or graph',
      icon: 'extension',
      steps: ['Capability', 'Focused package', 'Provider or EF', 'PostgreSQL'],
      detail:
        'Keep optional database features isolated in packages that own their mappings and tests.',
      route: '/extensions',
      action: 'Browse Extensions',
    },
  ] as const;
  protected readonly principles = [
    {
      icon: 'shield',
      kicker: 'SECURITY',
      title: 'Applications keep authorization',
      body: 'BlueTusk protects transport and credentials; the application remains responsible for users and business permissions.',
    },
    {
      icon: 'monitoring',
      kicker: 'OPERATIONS',
      title: 'Every boundary is observable',
      body: 'Metrics and traces cover connection use, commands, replication lag, checkpoints, and delivery health.',
    },
    {
      icon: 'verified',
      kicker: 'COMPATIBILITY',
      title: 'Capabilities are negotiated',
      body: 'Server features are discovered explicitly; unsupported behavior fails clearly instead of being guessed.',
    },
  ] as const;
}
