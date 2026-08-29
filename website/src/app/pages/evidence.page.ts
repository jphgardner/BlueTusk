import { Component, computed, effect, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { FormsModule } from '@angular/forms';
import { EVIDENCE, PRODUCT_STATUSES, sourceUrl } from '../content/catalog';
import { EvidenceStatus } from '../content/models';
import { StatusPill } from '../shared/technical-ui';

@Component({
  selector: 'bt-evidence-page',
  imports: [FormsModule, MatIconModule, StatusPill],
  template: `
    <section class="page-hero evidence-hero">
      <div>
        <span class="eyebrow"><i class="live-dot"></i> 1.1 RC EVIDENCE · 29 AUG 2026</span>
        <h1>See what is tested—and <em>what is still pending.</em></h1>
        <p>
          Every release claim links to evidence. Passed checks, open work, package history,
          security, and performance results are shown separately and without hidden assumptions.
        </p>
      </div>
      <div class="evidence-totals">
        <article>
          <strong>{{ passedCount() }}</strong
          ><span>recorded as passed</span>
        </article>
        <article>
          <strong>{{ pendingCount() }}</strong
          ><span>explicitly pending</span>
        </article>
        <article><strong>13,056</strong><span>public APIs checked</span></article>
      </div>
    </section>

    <section class="page-section evidence-dashboard">
      <header class="section-head">
        <div>
          <span>TEST AND RELEASE RECORDS</span>
          <h2>Explore the current results.</h2>
        </div>
        <button
          type="button"
          class="reset-filter"
          (click)="resetFilters()"
          [disabled]="!subsystem() && !kind() && !status()"
        >
          Reset filters
        </button>
      </header>
      <div class="filter-bar evidence-filters">
        <label
          ><small>SUBSYSTEM</small
          ><select [ngModel]="subsystem()" (ngModelChange)="setFilter('subsystem', $event)">
            <option value="">All subsystems</option>
            @for (value of subsystems; track value) {
              <option [value]="value">{{ value }}</option>
            }
          </select></label
        >
        <label
          ><small>KIND</small
          ><select [ngModel]="kind()" (ngModelChange)="setFilter('kind', $event)">
            <option value="">All evidence</option>
            @for (value of kinds; track value) {
              <option [value]="value">{{ value }}</option>
            }
          </select></label
        >
        <label
          ><small>STATUS</small
          ><select [ngModel]="status()" (ngModelChange)="setFilter('status', $event)">
            <option value="">All states</option>
            <option value="passed">Passed</option>
            <option value="pending">Pending</option>
            <option value="guarded">Guarded</option>
          </select></label
        >
        <span>{{ filtered().length }} / {{ records.length }} RECORDS</span>
      </div>
      <div class="evidence-records">
        @for (record of filtered(); track record.id) {
          <article [attr.data-status]="record.status">
            <header>
              <mat-icon>{{ icon(record.kind) }}</mat-icon
              ><span>{{ record.subsystem }} · {{ record.kind }}</span
              ><bt-status
                [label]="record.status"
                [stage]="record.status === 'passed' ? 'gate-passed' : record.status"
              />
            </header>
            <strong>{{ record.value }}</strong>
            <h3>{{ record.label }}</h3>
            <p>{{ record.detail }}</p>
            <footer>
              <small>AS OF {{ record.asOf }}</small
              ><a [href]="url(record.sourcePath, record.anchor)" target="_blank" rel="noreferrer"
                >Source <mat-icon>open_in_new</mat-icon></a
              >
            </footer>
          </article>
        } @empty {
          <p class="empty-state">No evidence records match those filters.</p>
        }
      </div>
    </section>

    <section class="page-section">
      <header class="section-head">
        <div>
          <span>PRODUCT STATUS</span>
          <h2>See what is available and what comes next.</h2>
        </div>
        <p>
          Public RC packages are available now. Stable release still requires the remaining tests
          and approvals shown here.
        </p>
      </header>
      <div class="release-ledger">
        @for (product of products; track product.id) {
          <article>
            <div>
              <strong>{{ product.name }}</strong
              ><span>{{ product.version }}</span>
            </div>
            <div>
              <small>PACKAGE</small><strong>{{ product.packageState }}</strong>
            </div>
            <div>
              <small>REMAINING CHECK</small><strong>{{ product.gateState }}</strong>
            </div>
            <div>
              <small>WHAT TO KNOW</small><span>{{ product.limitations[0] }}</span>
            </div>
          </article>
        }
      </div>
    </section>

    <section class="page-section limitations">
      <div>
        <mat-icon>warning_amber</mat-icon>
        <div>
          <span class="section-kicker">KNOWN LIMITS</span>
          <h2>What this page does not claim.</h2>
        </div>
      </div>
      <ul>
        <li>
          <code>1.1.0-rc.1</code> is public on NuGet and npm; this page does not represent it as
          stable <code>1.1.0</code>.
        </li>
        <li>Passing the code checks does not mean every production environment has been tested.</li>
        <li>
          Recovery can resend the final unconfirmed transaction. PostgreSQL and Redis commit state
          with the checkpoint, OpenSearch uses replay-safe versions, and NATS consumers keep the
          stable transaction ID beyond the broker's deduplication window.
        </li>
        <li>
          Stable Streams still needs its exact 72-hour run, and Sync still needs its exact 24-hour
          run, including the planned failure-and-recovery checks.
        </li>
        <li>
          SQL/PGQ turns on only when the server supports it, and stable PostgreSQL 19 support waits
          for the final GA release and its test results.
        </li>
        <li>
          Stable release still requires an independent review, real application trials, and tested
          backup, restore, and rollback procedures.
        </li>
      </ul>
    </section>
  `,
})
export class EvidencePage {
  protected readonly records = EVIDENCE;
  protected readonly products = PRODUCT_STATUSES;
  protected readonly subsystem = signal('');
  protected readonly kind = signal('');
  protected readonly status = signal('');
  protected readonly subsystems = [...new Set(EVIDENCE.map((x) => x.subsystem))].sort();
  protected readonly kinds = [...new Set(EVIDENCE.map((x) => x.kind))].sort();
  protected readonly filtered = computed(() =>
    this.records.filter(
      (x) =>
        (!this.subsystem() || x.subsystem === this.subsystem()) &&
        (!this.kind() || x.kind === this.kind()) &&
        (!this.status() || x.status === this.status()),
    ),
  );
  protected readonly passedCount = computed(
    () => this.records.filter((x) => x.status === 'passed').length,
  );
  protected readonly pendingCount = computed(
    () => this.records.filter((x) => x.status === 'pending').length,
  );
  constructor(
    private route: ActivatedRoute,
    private router: Router,
  ) {
    const params = this.route.snapshot.queryParamMap;
    this.subsystem.set(params.get('subsystem') ?? '');
    this.kind.set(params.get('kind') ?? '');
    this.status.set(params.get('status') ?? '');
  }
  protected setFilter(key: string, value: string): void {
    if (key === 'subsystem') this.subsystem.set(value);
    if (key === 'kind') this.kind.set(value);
    if (key === 'status') this.status.set(value);
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        subsystem: this.subsystem() || null,
        kind: this.kind() || null,
        status: this.status() || null,
      },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }
  protected resetFilters(): void {
    this.subsystem.set('');
    this.kind.set('');
    this.status.set('');
    this.setFilter('status', '');
  }
  protected url = sourceUrl;
  protected icon(kind: string): string {
    return (
      (
        {
          compatibility: 'dns',
          tests: 'fact_check',
          security: 'shield',
          performance: 'speed',
          release: 'rocket_launch',
        } as Record<string, string>
      )[kind] ?? 'verified'
    );
  }
}
