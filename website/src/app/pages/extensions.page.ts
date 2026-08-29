import { Component, computed, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { EXTENSION_CAPABILITIES, sourceUrl } from '../content/catalog';
import { CodePanel, SourceLink, StatusPill } from '../shared/technical-ui';

@Component({
  selector: 'bt-extensions-page',
  imports: [RouterLink, MatButtonModule, MatIconModule, CodePanel, SourceLink, StatusPill],
  template: `
    <section class="page-hero split-hero extensions-hero">
      <div>
        <span class="eyebrow"><i class="live-dot"></i> OPTIONAL POSTGRESQL PACKAGES</span>
        <h1>Install the feature.<br /><em>Keep the core provider focused.</em></h1>
        <p>
          Extension packages add the codecs, mappings, migrations, and query translations for one
          PostgreSQL capability. Applications pay for only the features they select.
        </p>
        <div class="hero-actions">
          <a mat-flat-button routerLink="/documentation/extensions/catalog" class="primary-action"
            >Read the extension guide</a
          ><a mat-stroked-button href="#catalog" class="secondary-action">Browse packages</a>
        </div>
      </div>
      <aside class="registry-diagram">
        <div>
          <mat-icon>extension</mat-icon><strong>Focused package</strong
          ><span>Owns one PostgreSQL capability</span>
        </div>
        <mat-icon>south</mat-icon>
        <div>
          <mat-icon>inventory_2</mat-icon><strong>Data source registration</strong
          ><span>Adds codecs before the pool starts</span>
        </div>
        <mat-icon>south</mat-icon>
        <div>
          <mat-icon>data_object</mat-icon><strong>Optional EF package</strong
          ><span>Adds mappings and LINQ only when needed</span>
        </div>
      </aside>
    </section>

    <section id="catalog" class="page-section">
      <header class="section-head">
        <div>
          <span>SUPPORTED CAPABILITIES</span>
          <h2>Choose by workload.</h2>
        </div>
        <p>Filter the current package set by .NET surface or database job.</p>
      </header>
      <div class="filter-bar" role="group" aria-label="Filter extensions">
        <div>
          <small>.NET SURFACE</small>
          @for (option of surfaces; track option) {
            <button
              type="button"
              [class.active]="surface() === option"
              (click)="setFilter('surface', option)"
            >
              {{ option }}
            </button>
          }
        </div>
        <div>
          <small>WORKLOAD</small>
          @for (option of workloads; track option) {
            <button
              type="button"
              [class.active]="workload() === option"
              (click)="setFilter('workload', option)"
            >
              {{ option }}
            </button>
          }
        </div>
      </div>
      <div class="extension-grid">
        @for (item of filtered(); track item.feature) {
          <article>
            <div>
              <span class="extension-mark">{{ mark(item.feature) }}</span
              ><bt-status
                [label]="item.state === 'supported' ? 'Public RC' : 'Source preview'"
                [stage]="item.state === 'supported' ? 'gate-passed' : 'preview'"
              />
            </div>
            <small>{{ item.workload }}</small>
            <h3>{{ item.feature }}</h3>
            <p>{{ item.notes }}</p>
            <dl>
              <div>
                <dt>.NET surface</dt>
                <dd>{{ item.surface }}</dd>
              </div>
              <div>
                <dt>Tested server</dt>
                <dd>{{ item.postgres }}</dd>
              </div>
            </dl>
            <a routerLink="/documentation/extensions/catalog"
              >Read setup and boundaries <mat-icon>arrow_forward</mat-icon></a
            >
          </article>
        } @empty {
          <p class="empty-state">No extension matches both filters.</p>
        }
      </div>
    </section>

    <section class="page-section code-split">
      <div>
        <span class="section-kicker">STARTUP CONFIGURATION</span>
        <h2>Register capabilities before building the data source.</h2>
        <p>
          The resulting configuration is immutable. Pools, commands, and EF contexts then share the
          same catalogue and exact PostgreSQL type identities.
        </p>
        <bt-source-link [href]="source('docs/extensions/README.md')" label="Extension reference" />
      </div>
      <bt-code-panel file="Extensions.cs" [code]="code" />
    </section>

    <aside class="truth-note">
      <mat-icon>info</mat-icon>
      <p>
        <strong>Extension support is feature-specific.</strong> A package records its server image,
        extension version, ADO.NET surface, EF surface, and live tests. The checked-in
        <code>pg_durable</code> adapter is an upstream-preview evaluation surface, not a production
        package.
      </p>
      <a routerLink="/documentation/extensions/catalog">Review the support matrix</a>
    </aside>
  `,
})
export class ExtensionsPage {
  protected readonly source = sourceUrl;
  protected readonly all = EXTENSION_CAPABILITIES;
  protected readonly surface = signal('All');
  protected readonly workload = signal('All');
  protected readonly surfaces = ['All', 'ADO.NET', 'EF'] as const;
  protected readonly workloads = [
    'All',
    'Text',
    'Vector',
    'Key/value',
    'Hierarchy',
    'Search',
    'Workflows',
    'Spatial',
    'Time series',
  ] as const;
  protected readonly filtered = computed(() =>
    this.all.filter((item) => {
      const surfaceMatches = this.surface() === 'All' || item.surface.includes(this.surface());
      const workload = this.workload().toLowerCase();
      const workloadMatches =
        this.workload() === 'All' || item.workload.toLowerCase().includes(workload.split(' ')[0]);
      return surfaceMatches && workloadMatches;
    }),
  );
  constructor(
    private route: ActivatedRoute,
    private router: Router,
  ) {
    const params = route.snapshot.queryParamMap;
    const surface = params.get('surface');
    const workload = params.get('workload');
    if (surface && this.surfaces.includes(surface as (typeof this.surfaces)[number])) {
      this.surface.set(surface);
    }
    if (workload && this.workloads.includes(workload as (typeof this.workloads)[number])) {
      this.workload.set(workload);
    }
  }
  protected setFilter(key: 'surface' | 'workload', value: string): void {
    if (key === 'surface') this.surface.set(value);
    else this.workload.set(value);
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        surface: this.surface() === 'All' ? null : this.surface(),
        workload: this.workload() === 'All' ? null : this.workload(),
      },
      replaceUrl: true,
    });
  }
  protected mark(name: string): string {
    return name
      .replace('TimescaleDB', 'TS')
      .replace('PostGIS', 'GIS')
      .replace('pgvector', 'VEC')
      .replace('pg_trgm', 'TRG')
      .replace('pg_durable', 'DUR')
      .slice(0, 4)
      .toUpperCase();
  }
  protected readonly code = `var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UseCitext()
    .UseVector()
    .UsePostGis()
    .Build();

services.AddDbContext<AppDbContext>(options =>
    options.UseBlueTusk(dataSource, provider =>
        provider
            .UseCitext()
            .UsePgVector()
            .UsePostGis()));`;
}
