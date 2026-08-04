import { Component, computed, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { CodePanel, SourceLink, StatusPill } from '../shared/technical-ui';
import { sourceUrl } from '../content/catalog';

@Component({
  selector: 'bt-ef-core-page',
  imports: [RouterLink, MatButtonModule, MatIconModule, CodePanel, SourceLink, StatusPill],
  template: `
    <section class="page-hero split-hero">
      <div>
        <span class="eyebrow"><i class="live-dot"></i> ENTITY FRAMEWORK CORE</span>
        <h1>Use a DbContext.<br /><em>Keep PostgreSQL.</em></h1>
        <p>
          Translate PostgreSQL-native queries, preserve database objects through migrations, and
          reverse engineer rich metadata without abandoning familiar EF Core workflows.
        </p>
        <div class="hero-actions">
          <a mat-flat-button routerLink="/documentation/ef-core/overview" class="primary-action"
            >Open EF guide</a
          ><a
            mat-stroked-button
            href="https://github.com/jphgardner/BlueTusk/tree/main/samples/BlueTusk.Samples.EntityFrameworkCore"
            target="_blank"
            rel="noreferrer"
            class="secondary-action"
            >Run sample</a
          >
        </div>
      </div>
      <aside class="metric-cluster">
        <bt-status label="V1 code-ready · source preview" stage="gate-passed" />
        <div><strong>1,987</strong><span>official cases passed</span></div>
        <div><strong>124</strong><span>upstream skips retained</span></div>
        <div><strong>0</strong><span>unexpected failures</span></div>
        <small>2,111 CASES DISCOVERED · PG 18/19 FULL GATE</small>
      </aside>
    </section>

    <section class="page-section code-split">
      <div>
        <span class="section-kicker">DATA-SOURCE FIRST</span>
        <h2>One pool across ADO.NET and EF.</h2>
        <p>
          Build the provider data source once, then give it to the EF provider. Connection ownership
          and PostgreSQL type registration remain centralized.
        </p>
        <bt-source-link [href]="source('docs/ef-core/README.md')" />
      </div>
      <bt-code-panel file="AppDbContext.cs" [code]="setupCode" />
    </section>

    <section class="page-section">
      <header class="section-head">
        <div>
          <span>POSTGRESQL SURFACE</span>
          <h2>Explore the model by workload.</h2>
        </div>
        <p>Each tab represents implemented, repository-documented behavior.</p>
      </header>
      <nav class="segmented-tabs" aria-label="EF Core capability area">
        @for (tab of tabs; track tab.id) {
          <button
            type="button"
            [class.active]="activeTab() === tab.id"
            (click)="activeTab.set(tab.id)"
          >
            {{ tab.label }}
          </button>
        }
      </nav>
      <div class="tab-feature">
        <div>
          <mat-icon>{{ selectedTab().icon }}</mat-icon
          ><small>{{ selectedTab().kicker }}</small>
          <h3>{{ selectedTab().title }}</h3>
          <p>{{ selectedTab().body }}</p>
        </div>
        <div class="capability-list">
          @for (item of selectedTab().items; track item.name) {
            <article>
              <strong>{{ item.name }}</strong
              ><span>{{ item.detail }}</span>
            </article>
          }
        </div>
      </div>
    </section>

    <section class="page-section">
      <header class="section-head">
        <div>
          <span>SCHEMA OBJECT MATRIX</span>
          <h2>Database design remains modelled.</h2>
        </div>
        <label class="filter-input"
          ><mat-icon>search</mat-icon
          ><input
            [value]="schemaQuery()"
            (input)="schemaQuery.set($any($event.target).value)"
            placeholder="Filter partitions, RLS, indexes…"
            aria-label="Filter schema capabilities"
        /></label>
      </header>
      <div class="schema-grid">
        @for (item of filteredSchema(); track item.name) {
          <article>
            <mat-icon>{{ item.icon }}</mat-icon
            ><strong>{{ item.name }}</strong
            ><span>{{ item.support }}</span>
            <p>{{ item.detail }}</p>
          </article>
        } @empty {
          <p class="empty-state">No schema capability matches that filter.</p>
        }
      </div>
    </section>

    <section class="page-section evidence-callout">
      <div>
        <span class="section-kicker">SPECIFICATION EVIDENCE</span>
        <h2>Coverage is counted, not implied.</h2>
        <p>
          The official relational specification assembly is reported separately from BlueTusk-native
          live tests. Capability-adjusted cases run across PostgreSQL 15–19.
        </p>
        <a mat-stroked-button routerLink="/evidence" class="secondary-action"
          >Inspect all evidence</a
        >
      </div>
      <div class="test-bars">
        <article><span>PASS</span><strong>1,987</strong><i style="--bar:94.1%"></i></article>
        <article><span>UPSTREAM SKIP</span><strong>124</strong><i style="--bar:5.9%"></i></article>
        <article><span>UNEXPECTED FAIL</span><strong>0</strong><i style="--bar:0%"></i></article>
        <small>Source: docs/ef-core/specification-tests.md</small>
      </div>
    </section>
  `,
})
export class EfCorePage {
  protected readonly source = sourceUrl;
  protected readonly activeTab = signal('queries');
  protected readonly schemaQuery = signal('');
  protected readonly tabs = [
    {
      id: 'queries',
      label: 'Queries',
      icon: 'query_stats',
      kicker: 'LINQ TRANSLATION',
      title: 'PostgreSQL operators stay composable',
      body: 'Translate provider-aware LINQ without hiding the generated SQL boundary.',
      items: [
        {
          name: 'Collections',
          detail: 'Arrays, ranges, multiranges, lateral, and set-returning functions',
        },
        {
          name: 'Documents',
          detail: 'JSON/JSONB traversal, containment, and complex-type queries',
        },
        {
          name: 'Search + network',
          detail: 'Full text, inet/cidr, and PostgreSQL-specific operators',
        },
      ],
    },
    {
      id: 'mappings',
      label: 'Mappings',
      icon: 'conversion_path',
      kicker: 'TYPE SYSTEM',
      title: 'Native values across both providers',
      body: 'The EF layer composes over the provider catalogue rather than inventing a parallel codec system.',
      items: [
        { name: 'Built-in', detail: 'Temporal, network, geometric, JSON, arrays, and ranges' },
        { name: 'User-defined', detail: 'Enums, composites, domains, and extension values' },
        {
          name: 'Extensions',
          detail: 'pgvector, PostGIS, TimescaleDB, hstore, ltree, citext, and pg_trgm',
        },
      ],
    },
    {
      id: 'migrations',
      label: 'Migrations',
      icon: 'schema',
      kicker: 'DATABASE DESIGN',
      title: 'Model PostgreSQL schema objects',
      body: 'Generate PostgreSQL-aware operations while retaining escape hatches for guarded features.',
      items: [
        {
          name: 'Tables',
          detail: 'Identity, generated columns, comments, partitions, inheritance, and tablespaces',
        },
        { name: 'Policy', detail: 'CHECK, RLS, exclusion constraints, triggers, and rules' },
        { name: 'Data movement', detail: 'Publications, subscriptions, foreign data, and views' },
      ],
    },
    {
      id: 'scaffolding',
      label: 'Scaffolding',
      icon: 'account_tree',
      kicker: 'DATABASE FIRST',
      title: 'Bring server metadata back into code',
      body: 'Reverse engineer PostgreSQL-specific types and model annotations with schema filtering.',
      items: [
        { name: 'Discovery', detail: 'Schemas, tables, columns, keys, indexes, and custom types' },
        { name: 'Retention', detail: 'Provider annotations survive code generation' },
        {
          name: 'Security',
          detail: 'Design-time connection handling follows documented boundaries',
        },
      ],
    },
    {
      id: 'graph',
      label: 'SQL/PGQ',
      icon: 'share',
      kicker: 'POSTGRESQL 19',
      title: 'Graph translation behind a capability guard',
      body: 'Typed graph constructs only activate when the PostgreSQL 19 SQL/PGQ surface is actually detected.',
      items: [
        { name: 'Raw SQL', detail: 'Execute SQL/PGQ directly through the provider' },
        { name: 'Migrations', detail: 'Create and reverse engineer property graph metadata' },
        {
          name: 'Typed queries',
          detail:
            'Translate supported EF graph constructs with explicit failure for unsupported shapes',
        },
      ],
    },
  ] as const;
  protected readonly selectedTab = computed(
    () => this.tabs.find((x) => x.id === this.activeTab()) ?? this.tabs[0],
  );
  protected readonly schema = [
    {
      icon: 'table_chart',
      name: 'Partitions + inheritance',
      support: 'Migrations + scaffolding',
      detail: 'PostgreSQL table topology survives the model boundary.',
    },
    {
      icon: 'shield',
      name: 'Row-level security',
      support: 'Migrations',
      detail: 'Policies are modelled as PostgreSQL schema behavior.',
    },
    {
      icon: 'rule',
      name: 'CHECK + exclusion',
      support: 'Migrations + discovery',
      detail: 'Constraint intent remains explicit.',
    },
    {
      icon: 'publish',
      name: 'Publications + subscriptions',
      support: 'Migrations',
      detail: 'Logical replication objects are first class.',
    },
    {
      icon: 'extension',
      name: 'Extensions + custom types',
      support: 'Migrations + mapping',
      detail: 'Installed capabilities connect to provider type registration.',
    },
    {
      icon: 'view_quilt',
      name: 'Views + foreign data',
      support: 'Migrations + scaffolding',
      detail: 'Database-owned read models remain discoverable.',
    },
    {
      icon: 'functions',
      name: 'Routines + operators',
      support: 'Migrations',
      detail: 'Advanced catalogue objects retain PostgreSQL naming.',
    },
    {
      icon: 'share',
      name: 'Property graphs',
      support: 'PG 19 guarded',
      detail: 'Older servers return capability-safe empty discovery.',
    },
  ] as const;
  protected readonly filteredSchema = computed(() => {
    const q = this.schemaQuery().trim().toLowerCase();
    return q
      ? this.schema.filter((x) => `${x.name} ${x.support} ${x.detail}`.toLowerCase().includes(q))
      : this.schema;
  });
  protected readonly setupCode = `await using var dataSource =
    new BlueTuskDataSourceBuilder(connectionString).Build();

var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseBlueTusk(dataSource)
    .Options;

await using var context = new AppDbContext(options);
var active = await context.Customers
    .Where(customer => customer.Tags.Contains("active"))
    .ToListAsync();`;
}
