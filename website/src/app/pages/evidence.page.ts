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
        <span class="eyebrow"><i class="live-dot"></i> EVIDENCE SNAPSHOT · 29 AUG 2026</span>
        <h1>Separate what passed from <em>what remains open.</em></h1>
        <p>
          This page records package publication, compatibility, tests, security, performance, and
          release gates. Each result has a date, a state, and a source; an RC package is never
          presented as a stable approval.
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
        <article>
          <strong>{{ guardedCount() }}</strong
          ><span>capability guarded</span>
        </article>
      </div>
    </section>

    <section class="page-section evidence-dashboard">
      <header class="section-head">
        <div>
          <span>FILTERABLE RECORD</span>
          <h2>Inspect one claim at a time.</h2>
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
          <h2>Package availability and stable approval are different.</h2>
        </div>
        <p>
          Every product is available as the coordinated 1.1 RC. Stable promotion depends on the
          remaining product-specific and platform-wide gates.
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
          <span class="section-kicker">INTERPRETATION</span>
          <h2>Read the boundary with the result.</h2>
        </div>
      </div>
      <ul>
        <li><code>1.1.0-rc.1</code> is public; it is not stable <code>1.1.0</code>.</li>
        <li>A repository or package test does not approve an application’s production topology.</li>
        <li>
          Streams and Sync stable promotion still require their exact long-running recovery gates.
        </li>
        <li>
          SQL/PGQ remains capability guarded until PostgreSQL 19 GA and exact-candidate evidence
          pass.
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
  protected readonly guardedCount = computed(
    () => this.records.filter((x) => x.status === 'guarded').length,
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
