import { Component, computed, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { sourceUrl } from '../content/catalog';
import { SourceLink, StatusPill } from '../shared/technical-ui';

@Component({
  selector: 'bt-real-time-page',
  imports: [RouterLink, MatButtonModule, MatIconModule, SourceLink, StatusPill],
  template: `
    <section class="page-hero realtime-hero">
      <div class="realtime-copy">
        <span class="eyebrow"><i class="live-dot"></i> AFTER COMMIT</span>
        <h1>Move PostgreSQL changes <em>without losing recovery.</em></h1>
        <p>
          Streams turns committed WAL transactions into acknowledged deliveries. Sync writes them to
          other systems, Live updates authorized clients, and Control Plane exposes operational
          state. Each product has a separate job and recovery contract.
        </p>
        <div class="hero-actions">
          <a mat-flat-button routerLink="/documentation/real-time/platform" class="primary-action"
            >Read the platform guide</a
          ><a
            mat-stroked-button
            routerLink="/documentation/real-time/contracts"
            class="secondary-action"
            >Review delivery contracts</a
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
          <span>THE DELIVERY RULE</span>
          <h2>Save progress after durable work.</h2>
        </div>
        <p>Select a step to see where ownership and failure handling change.</p>
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

    <section class="page-section">
      <header class="section-head">
        <div>
          <span>CHOOSE THE CONSUMER</span>
          <h2>Use one product for one outcome.</h2>
        </div>
        <p>Streams is the source boundary; the other products consume it for different jobs.</p>
      </header>
      <div class="journey-grid journey-grid-two">
        @for (outcome of outcomes; track outcome.title) {
          <article>
            <mat-icon>{{ outcome.icon }}</mat-icon
            ><small>{{ outcome.kicker }}</small>
            <h3>{{ outcome.title }}</h3>
            <div class="journey-flow">
              @for (step of outcome.steps; track step) {
                <span>{{ step }}</span>
              }
            </div>
            <p>{{ outcome.detail }}</p>
            <a [routerLink]="outcome.route">{{ outcome.action }}</a>
          </article>
        }
      </div>
    </section>

    <section class="page-section snapshot-section">
      <header class="section-head">
        <div>
          <span>INITIAL DATA → LIVE CHANGES</span>
          <h2>Start without a gap.</h2>
        </div>
        <bt-source-link
          [href]="source('docs/streams/snapshot-bootstrap.md')"
          label="Snapshot bootstrap guide"
        />
      </header>
      <div class="timeline-flow">
        @for (step of snapshot; track step.title; let index = $index) {
          <article>
            <span>0{{ index + 1 }}</span
            ><mat-icon>{{ step.icon }}</mat-icon>
            <h3>{{ step.title }}</h3>
            <p>{{ step.body }}</p>
          </article>
        }
      </div>
    </section>

    <aside class="truth-note">
      <mat-icon>verified_user</mat-icon>
      <p>
        <strong>At-least-once delivery is explicit.</strong> A crash can resend the final
        unconfirmed transaction. Each consumer must make that retry safe before acknowledging it;
        official Sync connectors document their own atomicity or idempotency boundary.
      </p>
      <a routerLink="/documentation/real-time/sync">Read the Sync contract</a>
    </aside>
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
      role: 'Decode, checkpoint, and replay committed changes',
      icon: 'stream',
      stage: 'gate-passed',
      gate: 'Stable endurance pending',
    },
    {
      id: 'sync',
      name: 'Sync',
      version: '1.1.0-rc.1 public',
      role: 'Write changes to external destinations',
      icon: 'sync_alt',
      stage: 'gate-passed',
      gate: 'Stable endurance pending',
    },
    {
      id: 'live',
      name: 'Live',
      version: '1.1.0-rc.1 public',
      role: 'Maintain authorized client query results',
      icon: 'sensors',
      stage: 'gate-passed',
      gate: 'RC package verified',
    },
    {
      id: 'control',
      name: 'Control Plane',
      version: '1.1.0-rc.1 public',
      role: 'Inspect and audit operational state',
      icon: 'monitoring',
      stage: 'gate-passed',
      gate: 'RC package verified',
    },
  ] as const;
  protected readonly sequence = [
    {
      id: 'commit',
      name: 'Commit',
      signal: 'PostgreSQL transaction',
      icon: 'storage',
      kicker: 'SOURCE OF TRUTH',
      title: 'PostgreSQL commits the transaction',
      detail: 'Logical replication exposes the transaction only after PostgreSQL commits it.',
      rule: 'Nothing has been acknowledged by the consumer.',
    },
    {
      id: 'deliver',
      name: 'Deliver',
      signal: 'One bounded unit',
      icon: 'inventory_2',
      kicker: 'STREAMS',
      title: 'Streams assembles one delivery',
      detail:
        'Changes stay in transaction order and spill to versioned storage when memory limits require it.',
      rule: 'The delivery carries identity, position, and an acknowledgement operation.',
    },
    {
      id: 'apply',
      name: 'Apply',
      signal: 'Consumer-owned effect',
      icon: 'input',
      kicker: 'DESTINATION',
      title: 'The consumer makes its effect durable',
      detail:
        'Application code or a Sync connector writes the transaction using the destination’s recovery model.',
      rule: 'A failed or ambiguous write cannot advance progress.',
    },
    {
      id: 'ack',
      name: 'Acknowledge',
      signal: 'Saved checkpoint',
      icon: 'done_all',
      kicker: 'SAFE TO CONTINUE',
      title: 'Progress advances last',
      detail:
        'Only after the consumer’s contract permits it does BlueTusk record the next resume position.',
      rule: 'Recovery may resend only the final unconfirmed transaction.',
    },
  ] as const;
  protected readonly activeStep = computed(
    () => this.sequence.find((step) => step.id === this.selectedStep()) ?? this.sequence[0],
  );
  protected readonly outcomes = [
    {
      kicker: 'CAPTURE',
      title: 'Build a recoverable change stream',
      icon: 'stream',
      steps: ['WAL', 'Transaction', 'Checkpoint', 'Replay'],
      detail:
        'Use Streams when application code needs committed changes with explicit acknowledgement.',
      route: '/documentation/real-time/streams',
      action: 'Read the Streams guide',
    },
    {
      kicker: 'SYNCHRONIZE',
      title: 'Maintain another system',
      icon: 'sync_alt',
      steps: ['Transform', 'Write', 'Checkpoint', 'Reconcile'],
      detail: 'Use Sync for versioned destination writes, quarantine, repair, and rebuild.',
      route: '/documentation/real-time/sync',
      action: 'Read the Sync guide',
    },
    {
      kicker: 'DELIVER',
      title: 'Update connected clients',
      icon: 'sensors',
      steps: ['Authorize', 'Query', 'Diff', 'Replay'],
      detail: 'Use Live for server-authorized initial results and bounded client updates.',
      route: '/documentation/real-time/live',
      action: 'Read the Live guide',
    },
    {
      kicker: 'OPERATE',
      title: 'Inspect runtime state',
      icon: 'monitoring',
      steps: ['Inventory', 'Health', 'Audit', 'Action'],
      detail: 'Use Control Plane for redacted operational views and authorized, audited actions.',
      route: '/documentation/real-time/control-plane',
      action: 'Read the Control Plane guide',
    },
  ] as const;
  protected readonly snapshot = [
    {
      icon: 'lock_clock',
      title: 'Create a consistent fence',
      body: 'Record the WAL position associated with one exported database snapshot.',
    },
    {
      icon: 'download',
      title: 'Read existing rows',
      body: 'Stream the snapshot through bounded, restartable batches.',
    },
    {
      icon: 'flag',
      title: 'Persist the handover',
      body: 'Save snapshot progress and the exact replication starting position.',
    },
    {
      icon: 'play_arrow',
      title: 'Follow new commits',
      body: 'Resume change delivery from the fence without an unaccounted gap.',
    },
  ] as const;
}
