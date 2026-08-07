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
        <span class="eyebrow"><i class="live-dot"></i> PLATFORM TOPOLOGY</span>
        <h1>One PostgreSQL-native system.<br /><em>Clear boundaries.</em></h1>
        <p>
          The wire protocol, ADO.NET, EF Core, replication, Streams, Sync, Live, extensions, and
          graph form a deliberately layered platform—not a bundle of wrappers.
        </p>
        <div class="hero-actions">
          <a
            mat-flat-button
            routerLink="/documentation/getting-started/architecture"
            class="primary-action"
            >Read the architecture</a
          ><a mat-stroked-button routerLink="/evidence" class="secondary-action"
            >Inspect evidence</a
          >
        </div>
      </div>
      <aside class="signal-card">
        <small>DEPENDENCY RULE</small
        ><strong>Applications flow down.<br />Evidence flows up.</strong>
        <p>
          Higher layers depend on stable lower-layer contracts. The protocol layer never takes an EF
          or application dependency.
        </p>
        <bt-source-link [href]="source('docs/architecture/overview.md')" />
      </aside>
    </section>

    <section class="page-section">
      <header class="section-head">
        <div>
          <span>INTERACTIVE LAYER MAP</span>
          <h2>Trace responsibility, not marketing.</h2>
        </div>
        <p>Select a layer to inspect what it owns and what it is allowed to depend on.</p>
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
              @for (item of activeLayer().owns; track item) {
                <span>{{ item }}</span>
              }
            </div>
            <div>
              <small>DEPENDS ON</small>
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
          <span>EXECUTION JOURNEYS</span>
          <h2>Follow a committed change.</h2>
        </div>
        <p>Three paths share PostgreSQL truth while keeping delivery contracts explicit.</p>
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
          <span>RELEASE TRAINS</span>
          <h2>One platform, independently earned confidence.</h2>
        </div>
        <p>
          V1 implementation, exact-candidate evidence, and stable publication remain deliberately
          separate states.
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
      role: 'Workloads',
      icon: 'developer_mode',
      description:
        'Application and service code chooses the data-access or event contract it needs.',
      owns: ['Domain behavior', 'Authorization context', 'Destination semantics'],
      depends: ['EF Core', 'ADO.NET', 'Live clients'],
    },
    {
      id: 'ef',
      index: '02',
      name: 'EF Core + extensions',
      role: 'Application model',
      icon: 'data_object',
      description:
        'Query translation, mappings, migrations, scaffolding, and optional extension packages.',
      owns: ['LINQ translation', 'Model metadata', 'Design-time tooling'],
      depends: ['ADO.NET provider', 'Extension abstractions'],
    },
    {
      id: 'provider',
      index: '03',
      name: 'ADO.NET Provider',
      role: 'Data access',
      icon: 'storage',
      description:
        'Connections, commands, pooling, type mapping, COPY, notifications, and replication.',
      owns: ['ADO.NET contracts', 'Pool lifecycle', 'PostgreSQL types'],
      depends: ['Client protocol', 'Transport'],
    },
    {
      id: 'protocol',
      index: '04',
      name: 'Protocol + transport',
      role: 'Wire engine',
      icon: 'lan',
      description: 'Bounded framing, authentication, cancellation, TLS, and pipeline-aware I/O.',
      owns: ['Frontend/backend frames', 'Buffer ownership', 'Wire cancellation'],
      depends: ['System.IO.Pipelines', 'Sockets and TLS'],
    },
    {
      id: 'realtime',
      index: '05',
      name: 'Real-time data plane',
      role: 'Committed changes',
      icon: 'stream',
      description:
        'Logical replication becomes acknowledged, checkpointed transactions and downstream projections.',
      owns: ['Source identity', 'Checkpoint fencing', 'Relay retention'],
      depends: ['Replication', 'PostgreSQL stores'],
    },
  ] as const;
  protected readonly activeLayer = computed(
    () => this.layers.find((x) => x.id === this.selectedLayer()) ?? this.layers[0],
  );
  protected readonly journeys = [
    {
      kicker: 'REQUEST / RESPONSE',
      title: 'Application query',
      icon: 'sync_alt',
      steps: ['DbContext', 'Provider', 'Protocol', 'PostgreSQL'],
      detail: 'Typed values and server capabilities remain visible across every layer.',
    },
    {
      kicker: 'COMMITTED CHANGE',
      title: 'Snapshot then stream',
      icon: 'timeline',
      steps: ['Snapshot', 'WAL fence', 'Streams', 'Checkpoint'],
      detail: 'A recorded fence prevents gaps between the consistent snapshot and live WAL.',
    },
    {
      kicker: 'DESTINATION FLOW',
      title: 'Versioned synchronization',
      icon: 'account_tree',
      steps: ['Relay', 'Transform', 'Destination', 'Reconcile'],
      detail: 'PostgreSQL, NATS, Redis, and OpenSearch each keep an explicit delivery contract.',
    },
  ] as const;
  protected readonly crosscutting = [
    {
      icon: 'shield',
      kicker: 'SECURITY',
      title: 'Threat boundaries are documented',
      body: 'Transport security, credential sources, authorization, and application responsibilities are separated.',
    },
    {
      icon: 'monitoring',
      kicker: 'OBSERVABILITY',
      title: 'Trace the real operations',
      body: 'Metrics, traces, slow commands, checkpoints, and WAL lag expose the work the system performs.',
    },
    {
      icon: 'verified',
      kicker: 'COMPATIBILITY',
      title: 'Change is mechanically checked',
      body: 'Public API freezes, persisted formats, and PostgreSQL matrices turn promises into gates.',
    },
  ] as const;
}
