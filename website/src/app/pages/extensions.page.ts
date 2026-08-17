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
        <span class="eyebrow"><i class="live-dot"></i> OPTIONAL POSTGRESQL DEPTH</span>
        <h1>Extensions without <em>core bloat.</em></h1>
        <p>
          Eight first-party families compose through a registry snapshot. Install only the
          capabilities your application uses while preserving native ADO.NET and EF behavior.
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
          ><span>Types · features · translations</span>
        </div>
        <mat-icon>south</mat-icon>
        <div>
          <mat-icon>inventory_2</mat-icon><strong>Builder registry</strong
          ><span>Mutable during configuration</span>
        </div>
        <mat-icon>south</mat-icon>
        <div>
          <mat-icon>lock</mat-icon><strong>Data source snapshot</strong
          ><span>Immutable at runtime</span>
        </div>
      </aside>
    </section>

    <section id="catalog" class="page-section">
      <header class="section-head">
        <div>
          <span>CAPABILITY CATALOG</span>
          <h2>Choose the workload, then the surface.</h2>
        </div>
        <p>Filters are local and shareable; support claims link to the repository guide.</p>
      </header>
      <div class="filter-bar" role="group" aria-label="Filter extensions">
        <div>
          <small>SURFACE</small>
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
              ><bt-status label="Preview" />
            </div>
            <small>{{ item.workload }}</small>
            <h3>{{ item.feature }}</h3>
            <p>{{ item.notes }}</p>
            <dl>
              <div>
                <dt>Surface</dt>
                <dd>{{ item.surface }}</dd>
              </div>
              <div>
                <dt>Gate</dt>
                <dd>{{ item.postgres }}</dd>
              </div>
            </dl>
            <a [href]="source(item.sourcePath)" target="_blank" rel="noreferrer"
              >Source evidence <mat-icon>open_in_new</mat-icon></a
            >
          </article>
        } @empty {
          <p class="empty-state">No extension matches both filters.</p>
        }
      </div>
    </section>

    <section class="page-section code-split">
      <div>
        <span class="section-kicker">COMPOSITION MODEL</span>
        <h2>Register before the data source is built.</h2>
        <p>
          Builder changes are snapshotted into the resulting data source. Optional packages remain
          independently deployable and the core provider takes no extension-specific dependency.
        </p>
        <bt-source-link [href]="source('docs/extensions/README.md')" />
      </div>
      <bt-code-panel file="Extensions.cs" [code]="code" />
    </section>

    <section class="page-section">
      <header class="section-head">
        <div>
          <span>AUTHORING FLOW</span>
          <h2>Add a capability without fragmenting the platform.</h2>
        </div>
        <p>
          The same extension abstractions connect provider codecs, immutable descriptors, and EF
          translation.
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
    .UsePgDurable()
    .Build();

await using var command = dataSource.CreateCommand(
    "SELECT embedding <-> $1 FROM catalog ORDER BY 1 LIMIT 10");

command.Parameters.Add(
    new BlueTuskParameter<BlueTuskVector>(embedding));`;
  protected readonly authoring = [
    {
      icon: 'data_object',
      title: 'Define values',
      body: 'Use immutable CLR values that preserve PostgreSQL semantics.',
    },
    {
      icon: 'conversion_path',
      title: 'Register codecs',
      body: 'Add text/binary mappings through the type registry.',
    },
    {
      icon: 'flag',
      title: 'Describe features',
      body: 'Publish immutable descriptors for capability discovery.',
    },
    {
      icon: 'functions',
      title: 'Add EF translation',
      body: 'Compose optional query and mapping support without a core dependency.',
    },
  ] as const;
}
