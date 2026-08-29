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
        <span class="eyebrow"><i class="live-dot"></i> REAL-TIME DATA</span>
        <h1>Turn database changes into <em>live products.</em></h1>
        <p>
          Capture committed PostgreSQL changes, send them to other systems, and update connected
          users—without losing track of delivery or recovery.
        </p>
        <div class="hero-actions">
          <a mat-flat-button routerLink="/documentation/real-time/platform" class="primary-action"
            >See how real-time works</a
          ><a
            mat-stroked-button
            routerLink="/evidence"
            [queryParams]="{ subsystem: 'Streams' }"
            class="secondary-action"
            >View proof</a
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
          <span>SAFE DELIVERY</span>
          <h2>Save progress only after the work is safe.</h2>
        </div>
        <p>
          Select a step to see how BlueTusk moves from a committed database change to a recoverable
          update.
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
          <span>EXISTING DATA → LIVE CHANGES</span>
          <h2>Start live updates without missing anything.</h2>
        </div>
        <bt-source-link
          [href]="source('docs/streams/snapshot-bootstrap.md')"
          label="How the initial load works"
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
          <h2>Retries stay safe, even after a crash.</h2>
        </div>
        <p>
          BlueTusk never saves progress before the destination is durable. If the final transaction
          is sent again during recovery, each connector has a tested way to prevent stale or unsafe
          work.
        </p>
      </header>
      <div class="destination-matrix">
        <div class="matrix-head">
          <span>Destination</span><span>How it writes</span><span>How it recovers</span
          ><span>Proven guarantee</span>
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
        <mat-icon>verified_user</mat-icon>
        <p>
          <strong>Progress never moves past unsafe work.</strong> Recovery may resend the final
          unconfirmed transaction. PostgreSQL and Redis make that retry atomic, OpenSearch protects
          it with versions, and NATS gives it one stable identity for deduplication.
        </p>
        <a routerLink="/documentation/real-time/sync">See the complete delivery contract</a>
      </aside>
    </section>

    <section class="page-section live-grid">
      <div>
        <span class="section-kicker">LIVE DELIVERY</span>
        <h2>Send the right data to the right users.</h2>
        <p>
          Live sends an initial result and then only the changes. It checks permissions when needed,
          recovers missed updates, and slows down safely when clients cannot keep up.
        </p>
        <div class="tag-cloud">
          <span>SSE</span><span>WebSockets</span><span>Angular</span><span>React</span
          ><span>Vue</span><span>Svelte</span><span>Replay</span><span>Backpressure</span>
        </div>
      </div>
      <div class="control-panel">
        <small>OPERATIONS AT A GLANCE</small>
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
      version: '1.1.0-rc.1 public',
      role: 'Capture changes and recover safely',
      icon: 'stream',
      stage: 'gate-passed',
      gate: 'RC verified · 72-hour stable test pending',
    },
    {
      id: 'sync',
      name: 'Sync',
      version: '1.1.0-rc.1 public',
      role: 'Keep other systems up to date',
      icon: 'sync_alt',
      stage: 'gate-passed',
      gate: 'RC verified · 24-hour stable test pending',
    },
    {
      id: 'live',
      name: 'Live',
      version: '1.1.0-rc.1 public',
      role: 'Send permitted updates to users',
      icon: 'sensors',
      stage: 'gate-passed',
      gate: 'Package and install checks passed',
    },
    {
      id: 'control',
      name: 'Control Plane',
      version: '1.1.0-rc.1 public',
      role: 'Monitor and manage the system',
      icon: 'monitoring',
      stage: 'gate-passed',
      gate: 'Package dependencies verified',
    },
  ] as const;
  protected readonly sequence = [
    {
      id: 'commit',
      name: 'Commit',
      signal: 'Committed database change',
      icon: 'storage',
      kicker: 'SOURCE OF TRUTH',
      title: 'PostgreSQL commits the transaction',
      detail: 'PostgreSQL replication provides changes only after the transaction commits.',
      rule: 'No progress is marked complete yet.',
    },
    {
      id: 'assemble',
      name: 'Assemble',
      signal: 'Stored transaction',
      icon: 'inventory_2',
      kicker: 'ONE COMPLETE CHANGE',
      title: 'Changes become one delivery',
      detail:
        'Streams groups the transaction safely and uses disk if it grows beyond the memory limit.',
      rule: 'The consumer receives one unit of work and a way to confirm it.',
    },
    {
      id: 'apply',
      name: 'Apply',
      signal: 'Destination',
      icon: 'input',
      kicker: 'YOUR APPLICATION',
      title: 'The consumer applies the transaction',
      detail: 'Your application or a Sync connector writes the change to its destination.',
      rule: 'If the write fails, BlueTusk does not save progress.',
    },
    {
      id: 'ack',
      name: 'Acknowledge',
      signal: 'Saved progress',
      icon: 'done_all',
      kicker: 'SAFE TO CONTINUE',
      title: 'Progress advances after durable work',
      detail: 'After the destination is safe, BlueTusk records exactly where to resume.',
      rule: 'Recovery rules still depend on the destination system.',
    },
  ] as const;
  protected readonly activeStep = computed(
    () => this.sequence.find((x) => x.id === this.selectedStep()) ?? this.sequence[0],
  );
  protected readonly snapshot = [
    {
      icon: 'lock_clock',
      title: 'Choose a consistent starting point',
      body: 'Open one consistent view of the data and record where live changes begin.',
    },
    {
      icon: 'download',
      title: 'Load the existing data',
      body: 'Stream the current rows to the consumer without loading everything into memory.',
    },
    {
      icon: 'flag',
      title: 'Save the handover point',
      body: 'Record the exact position where the initial load hands over to live changes.',
    },
    {
      icon: 'play_arrow',
      title: 'Follow new changes',
      body: 'Continue from the saved position without a gap or an unknown overlap.',
    },
  ] as const;
  protected readonly destinations = [
    {
      name: 'PostgreSQL',
      icon: 'database',
      write: 'Write the full change and checkpoint together',
      recovery: 'A retry finds the saved checkpoint and does no duplicate work',
      boundary: 'Atomic state + checkpoint',
    },
    {
      name: 'NATS',
      icon: 'swap_horiz',
      write: 'Publish one durable transaction envelope',
      recovery: 'Deduplicate its stable ID in JetStream and downstream',
      boundary: 'Durable publish + stable identity',
    },
    {
      name: 'Redis',
      icon: 'memory',
      write: 'Write the full change and checkpoint in one Lua operation',
      recovery: 'A retry cannot pass an equal or newer checkpoint',
      boundary: 'Atomic state + checkpoint',
    },
    {
      name: 'OpenSearch',
      icon: 'search',
      write: 'Bulk-write documents with source versions',
      recovery: 'Replay the bulk; older versions cannot replace newer state',
      boundary: 'Replay-safe materialisation',
    },
    {
      name: 'Kafka · 1.2',
      icon: 'hub',
      write: 'Write the transaction and BlueTusk checkpoint in one broker transaction',
      recovery: 'Read only committed state and retry an uncertain broker result safely',
      boundary: 'Transactional events + checkpoint',
    },
    {
      name: 'S3 / Parquet · 1.2',
      icon: 'cloud_upload',
      write: 'Write immutable compressed data, then publish its commit manifest',
      recovery: 'Readers see only complete manifests; unfinished data stays invisible',
      boundary: 'Immutable data + commit manifest',
    },
    {
      name: 'Signed webhook · 1.2',
      icon: 'webhook',
      write: 'Send one bounded request with a stable delivery ID and signature',
      recovery: 'Retry with the same identity so receivers can deduplicate safely',
      boundary: 'Authenticated, replay-safe delivery',
    },
  ] as const;
  protected readonly controls = [
    { icon: 'source', label: 'Data sources', detail: 'Identity, current position, and delay' },
    { icon: 'archive', label: 'Replay storage', detail: 'Stored changes, retention, and backups' },
    { icon: 'groups', label: 'Consumers', detail: 'Workers and their saved progress' },
    { icon: 'history', label: 'Audit history', detail: 'Who changed what and when' },
    {
      icon: 'deployed_code',
      label: 'Kubernetes fleet · 1.2',
      detail: 'Pause, resume, reconcile, rebuild, and protected deletion',
    },
  ] as const;
}
