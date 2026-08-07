import { Component, computed, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { PRODUCT_STATUSES, sourceUrl } from '../content/catalog';
import { SourceLink, StatusPill } from '../shared/technical-ui';

@Component({
  selector: 'bt-real-time-page',
  imports: [RouterLink, MatButtonModule, MatIconModule, SourceLink, StatusPill],
  template: `
    <section class="page-hero realtime-hero">
      <div class="realtime-copy">
        <span class="eyebrow"><i class="live-dot"></i> COMMITTED CHANGE DATA</span>
        <h1>From WAL to a live experience.<br /><em>With the contract visible.</em></h1>
        <p>
          Streams, Sync, Live, and the Control Plane advance independently. Acknowledgements,
          checkpoints, replay, and destinations keep distinct responsibilities.
        </p>
        <div class="hero-actions">
          <a mat-flat-button routerLink="/documentation/real-time/platform" class="primary-action"
            >Read the correctness contract</a
          ><a mat-stroked-button routerLink="/evidence?subsystem=Streams" class="secondary-action"
            >View release evidence</a
          >
        </div>
      </div>
      <div class="lane-map">
        @for (lane of lanes; track lane.id) {
          <article [attr.data-stage]="lane.stage">
            <mat-icon>{{ lane.icon }}</mat-icon>
            <div>
              <small>{{ lane.version }}</small
              ><strong>{{ lane.name }}</strong
              ><span>{{ lane.role }}</span>
            </div>
            <bt-status [label]="lane.gate" [stage]="lane.stage" />
          </article>
        }
      </div>
    </section>

    <section class="page-section">
      <header class="section-head">
        <div>
          <span>DELIVERY CONTRACT</span>
          <h2>Acknowledge only what became durable.</h2>
        </div>
        <p>
          Select a stage to inspect the boundary between committed PostgreSQL state and downstream
          progress.
        </p>
      </header>
      <div class="sequence-explorer">
        <div class="sequence-track">
          @for (step of sequence; track step.id; let index = $index) {
            <button
              type="button"
              [class.active]="selectedStep() === step.id"
              (click)="selectedStep.set(step.id)"
            >
              <span>{{ index + 1 }}</span
              ><strong>{{ step.name }}</strong
              ><small>{{ step.signal }}</small>
            </button>
          }
        </div>
        <article>
          <mat-icon>{{ activeStep().icon }}</mat-icon>
          <div>
            <small>{{ activeStep().kicker }}</small>
            <h3>{{ activeStep().title }}</h3>
            <p>{{ activeStep().detail }}</p>
            <strong>{{ activeStep().rule }}</strong>
          </div>
        </article>
      </div>
    </section>

    <section class="page-section snapshot-section">
      <header class="section-head">
        <div>
          <span>SNAPSHOT → STREAM</span>
          <h2>Bootstrap without a change gap.</h2>
        </div>
        <bt-source-link
          [href]="source('docs/streams/snapshot-bootstrap.md')"
          label="Snapshot protocol"
        />
      </header>
      <div class="timeline-flow">
        @for (step of snapshot; track step.title; let i = $index) {
          <article>
            <span>0{{ i + 1 }}</span
            ><mat-icon>{{ step.icon }}</mat-icon>
            <h3>{{ step.title }}</h3>
            <p>{{ step.body }}</p>
          </article>
        }
      </div>
    </section>

    <section class="page-section">
      <header class="section-head">
        <div>
          <span>SYNC DESTINATIONS</span>
          <h2>Four destinations. Four explicit contracts.</h2>
        </div>
        <p>
          Sync does not pretend different systems provide identical atomicity or replay behavior.
        </p>
      </header>
      <div class="destination-matrix">
        <div class="matrix-head">
          <span>Destination</span><span>Write model</span><span>Recovery</span
          ><span>Current boundary</span>
        </div>
        @for (destination of destinations; track destination.name) {
          <div>
            <strong
              ><mat-icon>{{ destination.icon }}</mat-icon
              >{{ destination.name }}</strong
            ><span>{{ destination.write }}</span
            ><span>{{ destination.recovery }}</span
            ><small>{{ destination.boundary }}</small>
          </div>
        }
      </div>
      <aside class="truth-note">
        <mat-icon>info</mat-icon>
        <p>
          <strong>No exactly-once marketing claim.</strong> BlueTusk documents acknowledgement,
          idempotency, fencing, and destination behavior independently.
        </p>
        <a routerLink="/documentation/real-time/sync">Read Sync contract</a>
      </aside>
    </section>

    <section class="page-section live-grid">
      <div>
        <span class="section-kicker">LIVE DELIVERY</span>
        <h2>Authorized state, then updates.</h2>
        <p>
          Initial delivery, replay, shared subscriptions, backpressure, and ASP.NET transports
          retain an authoritative re-query security boundary.
        </p>
        <div class="tag-cloud">
          <span>SSE</span><span>WebSockets</span><span>Browser client</span><span>Replay</span
          ><span>Backpressure</span><span>Shared subscriptions</span>
        </div>
      </div>
      <div class="control-panel">
        <small>CONTROL PLANE VISIBILITY</small>
        @for (item of controls; track item.label) {
          <article>
            <mat-icon>{{ item.icon }}</mat-icon
            ><span
              ><strong>{{ item.label }}</strong
              ><small>{{ item.detail }}</small></span
            >
          </article>
        }
        <a routerLink="/documentation/real-time/control-plane"
          >Open operations guide <mat-icon>arrow_forward</mat-icon></a
        >
      </div>
    </section>
  `,
})
export class RealTimePage {
  protected readonly source = sourceUrl;
  protected readonly selectedStep = signal('commit');
  protected readonly lanes = [
    {
      id: 'streams',
      name: 'Streams',
      version: 'V1 candidate',
      role: 'Transactions, snapshots, relay',
      icon: 'stream',
      stage: 'pending',
      gate: '72h evidence pending',
    },
    {
      id: 'sync',
      name: 'Sync',
      version: 'V1 candidate',
      role: 'Versioned destination writes',
      icon: 'sync_alt',
      stage: 'pending',
      gate: '24h evidence pending',
    },
    {
      id: 'live',
      name: 'Live',
      version: 'V1 candidate',
      role: 'Authorized client delivery',
      icon: 'sensors',
      stage: 'gate-passed',
      gate: 'Code and package gates complete',
    },
    {
      id: 'control',
      name: 'Control Plane',
      version: 'V1 candidate',
      role: 'Operations and audit',
      icon: 'monitoring',
      stage: 'pending',
      gate: 'Dependency release chain pending',
    },
  ] as const;
  protected readonly sequence = [
    {
      id: 'commit',
      name: 'Commit',
      signal: 'PostgreSQL WAL',
      icon: 'storage',
      kicker: 'SOURCE OF TRUTH',
      title: 'PostgreSQL commits the transaction',
      detail:
        'Logical replication exposes only committed change data with a stable source identity.',
      rule: 'No destination acknowledgement exists yet.',
    },
    {
      id: 'assemble',
      name: 'Assemble',
      signal: 'Bounded spool',
      icon: 'inventory_2',
      kicker: 'TRANSACTION BOUNDARY',
      title: 'Changes become one delivery',
      detail: 'The stream assembles transaction records under bounded memory and spill behavior.',
      rule: 'The delivery owns its acknowledgement callback.',
    },
    {
      id: 'apply',
      name: 'Apply',
      signal: 'Destination',
      icon: 'input',
      kicker: 'APPLICATION WORK',
      title: 'The consumer applies the transaction',
      detail:
        'Application or connector logic writes its intended result and handles its own failure mode.',
      rule: 'Failure leaves the source position unacknowledged.',
    },
    {
      id: 'ack',
      name: 'Acknowledge',
      signal: 'Checkpoint',
      icon: 'done_all',
      kicker: 'DURABLE PROGRESS',
      title: 'Progress advances after durable work',
      detail: 'The acknowledgement updates the checkpoint using ordering and fencing rules.',
      rule: 'Acknowledgement is not an exactly-once claim.',
    },
  ] as const;
  protected readonly activeStep = computed(
    () => this.sequence.find((x) => x.id === this.selectedStep()) ?? this.sequence[0],
  );
  protected readonly snapshot = [
    {
      icon: 'lock_clock',
      title: 'Export snapshot',
      body: 'Acquire a consistent database view and a matching WAL position.',
    },
    {
      icon: 'download',
      title: 'Read baseline',
      body: 'Stream bounded rows from the snapshot into the consumer.',
    },
    {
      icon: 'flag',
      title: 'Record fence',
      body: 'Persist source identity and the exact transition position.',
    },
    {
      icon: 'play_arrow',
      title: 'Consume WAL',
      body: 'Begin live transactions beyond the fence without a gap.',
    },
  ] as const;
  protected readonly destinations = [
    {
      name: 'PostgreSQL',
      icon: 'database',
      write: 'Transactional upsert',
      recovery: 'Version check + reconciliation',
      boundary: 'Database transaction semantics',
    },
    {
      name: 'NATS',
      icon: 'swap_horiz',
      write: 'Published message',
      recovery: 'Consumer and message identity',
      boundary: 'Broker acknowledgement contract',
    },
    {
      name: 'Redis',
      icon: 'memory',
      write: 'Versioned key update',
      recovery: 'Compare-and-apply + rebuild',
      boundary: 'Key-level atomic behavior',
    },
    {
      name: 'OpenSearch',
      icon: 'search',
      write: 'Versioned document index',
      recovery: 'External version + reconcile',
      boundary: 'Index refresh is independent',
    },
  ] as const;
  protected readonly controls = [
    { icon: 'source', label: 'Sources + slots', detail: 'Identity, WAL position, and lag' },
    { icon: 'archive', label: 'Relay storage', detail: 'Segments, retention, and backups' },
    { icon: 'groups', label: 'Consumer groups', detail: 'Membership and checkpoints' },
    { icon: 'history', label: 'Audit', detail: 'Versioned operational responses' },
  ] as const;
}
