import { Component, computed, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { EXTENSION_CAPABILITIES, sourceUrl } from '../content/catalog';
import { CapabilityRecord } from '../content/models';
import { CodePanel, SourceLink, StatusPill } from '../shared/technical-ui';

@Component({
  selector: 'bt-extensions-page',
  imports: [RouterLink, MatButtonModule, MatIconModule, CodePanel, SourceLink, StatusPill],
  template: `
    <section class="page-hero split-hero extensions-hero">
      <div>
        <span class="eyebrow"><i class="live-dot"></i> OPTIONAL POSTGRESQL FEATURES</span>
        <h1>Add the database features <em>you need.</em></h1>
        <p>
          Add PostGIS, pgvector, TimescaleDB, or another supported extension without making every
          application carry features it never uses.
        </p>
        <div class="hero-actions">
          <a mat-flat-button routerLink="/documentation/extensions/catalog" class="primary-action"
            >Extension guide</a
          ><a mat-stroked-button href="#catalog" class="secondary-action">Browse catalog</a>
        </div>
      </div>
      <aside class="registry-diagram">
        <div>
          <mat-icon>extension</mat-icon><strong>Extension package</strong
          ><span>Adds only the feature you selected</span>
        </div>
        <mat-icon>south</mat-icon>
        <div>
          <mat-icon>inventory_2</mat-icon><strong>Data source setup</strong
          ><span>Configure extensions before startup</span>
        </div>
        <mat-icon>south</mat-icon>
        <div>
          <mat-icon>lock</mat-icon><strong>Running application</strong
          ><span>Uses one safe, fixed configuration</span>
        </div>
      </aside>
    </section>

    <section id="catalog" class="page-section">
      <header class="section-head">
        <div>
          <span>EXTENSION CATALOG</span>
          <h2>Find the feature your application needs.</h2>
        </div>
        <p>Filter by .NET API or task. Every support claim links to its technical guide.</p>
      </header>
      <div class="filter-bar" role="group" aria-label="Filter extensions">
        <div>
          <small>.NET API</small>
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
                [label]="item.state === 'supported' ? '1.1 RC public' : 'Preview'"
                [stage]="item.state === 'supported' ? 'gate-passed' : 'preview'"
              />
            </div>
            <small>{{ item.workload }}</small>
            <h3>{{ item.feature }}</h3>
            <p>{{ item.notes }}</p>
            <dl>
              <div>
                <dt>Works with</dt>
                <dd>{{ item.surface }}</dd>
              </div>
              <div>
                <dt>PostgreSQL support</dt>
                <dd>{{ item.postgres }}</dd>
              </div>
            </dl>
            <a [href]="source(item.sourcePath)" target="_blank" rel="noreferrer"
              >Read technical evidence <mat-icon>open_in_new</mat-icon></a
            >
          </article>
        } @empty {
          <p class="empty-state">No extension matches both filters.</p>
        }
      </div>
    </section>

    <section class="page-section code-split">
      <div>
        <span class="section-kicker">SIMPLE SETUP</span>
        <h2>Choose extensions when your application starts.</h2>
        <p>
          Add the extension packages you need before creating the data source. The configuration is
          then fixed for safe, consistent use, and unused extensions add nothing to the core
          provider.
        </p>
        <bt-source-link [href]="source('docs/extensions/README.md')" />
      </div>
      <bt-code-panel file="Extensions.cs" [code]="code" />
    </section>

    <section class="page-section">
      <header class="section-head">
        <div>
          <span>BUILD AN EXTENSION</span>
          <h2>Add support once for ADO.NET and EF Core.</h2>
        </div>
        <p>
          One extension package can define its .NET values, PostgreSQL conversion, feature details,
          and optional EF Core support.
        </p>
      </header>
      <div class="timeline-flow">
        @for (step of authoring; track step.title; let i = $index) {
          <article>
            <span>0{{ i + 1 }}</span
            ><mat-icon>{{ step.icon }}</mat-icon>
            <h3>{{ step.title }}</h3>
            <p>{{ step.body }}</p>
          </article>
        }
      </div>
    </section>
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
    if (surface && this.surfaces.includes(surface as (typeof this.surfaces)[number]))
      this.surface.set(surface);
    if (workload && this.workloads.includes(workload as (typeof this.workloads)[number]))
      this.workload.set(workload);
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

await using var command = dataSource.CreateCommand(
    "SELECT embedding <-> $1 FROM catalog ORDER BY 1 LIMIT 10");

command.Parameters.Add(
    new BlueTuskParameter<BlueTuskVector>(embedding));`;
  protected readonly authoring = [
    {
      icon: 'data_object',
      title: 'Define values',
      body: 'Create .NET values that keep the meaning of the PostgreSQL type.',
    },
    {
      icon: 'conversion_path',
      title: 'Convert values',
      body: 'Define how values move between .NET and PostgreSQL in text or binary form.',
    },
    {
      icon: 'flag',
      title: 'Describe the extension',
      body: 'Tell BlueTusk which features the package provides and requires.',
    },
    {
      icon: 'functions',
      title: 'Add EF translation',
      body: 'Add optional LINQ and model support without changing the core provider.',
    },
  ] as const;
}
