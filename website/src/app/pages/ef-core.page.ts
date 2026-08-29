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
        <h1>Use EF Core.<br /><em>Keep PostgreSQL powerful.</em></h1>
        <p>
          Keep the DbContext, LINQ, migrations, and tooling you know while using PostgreSQL-specific
          types and features when they add value.
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
        <bt-status label="1.1.0-rc.1 · public" stage="gate-passed" />
        <div><strong>1,987</strong><span>official cases passed</span></div>
        <div><strong>124</strong><span>upstream skips retained</span></div>
        <div><strong>0</strong><span>unexpected failures</span></div>
        <small>2,111 TESTS FOUND · FULL POSTGRESQL 18/19 RUN</small>
      </aside>
    </section>

    <section class="page-section code-split">
      <div>
        <span class="section-kicker">ONE SHARED CONNECTION POOL</span>
        <h2>Configure once. Use it everywhere.</h2>
        <p>
          Build one data source and share it with EF Core. Connections and PostgreSQL types stay
          consistent across your application.
        </p>
        <bt-source-link [href]="source('docs/ef-core/README.md')" />
      </div>
      <bt-code-panel file="AppDbContext.cs" [code]="setupCode" />
    </section>

    <section class="page-section">
      <header class="section-head">
        <div>
          <span>WHAT YOU CAN BUILD</span>
          <h2>Explore EF Core features by task.</h2>
        </div>
        <p>Choose a task to see the PostgreSQL features available through EF Core.</p>
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
          <span>DATABASE DESIGN</span>
          <h2>Keep PostgreSQL features in your EF model.</h2>
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
          <p class="empty-state">No database feature matches that filter.</p>
        }
      </div>
    </section>

    <section class="page-section evidence-callout">
      <div>
        <span class="section-kicker">EF CORE TEST RESULTS</span>
        <h2>See exactly what was tested.</h2>
        <p>
          BlueTusk runs the official EF Core relational tests and its own live PostgreSQL tests.
          Version-specific cases are checked across PostgreSQL 15–19.
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
      title: 'Write LINQ that uses PostgreSQL well',
      body: 'Use PostgreSQL-specific operations in LINQ and inspect the SQL that EF Core generates.',
      items: [
        {
          name: 'Collections',
          detail: 'Query arrays, ranges, multiranges, lateral joins, and set-returning functions',
        },
        {
          name: 'Documents',
          detail: 'Read and filter JSON/JSONB documents and complex types',
        },
        {
          name: 'Search + network',
          detail: 'Use full-text search, network types, and PostgreSQL operators',
        },
      ],
    },
    {
      id: 'mappings',
      label: 'Mappings',
      icon: 'conversion_path',
      kicker: 'TYPE SYSTEM',
      title: 'Use the same PostgreSQL types everywhere',
      body: 'EF Core and ADO.NET share one type system, so values behave consistently across your application.',
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
      title: 'Create PostgreSQL-aware migrations',
      body: 'Build migrations for PostgreSQL features, with raw SQL available for anything specialised.',
      items: [
        {
          name: 'Tables',
          detail: 'Identity, generated columns, comments, partitions, inheritance, and tablespaces',
        },
        {
          name: 'Rules and security',
          detail: 'CHECK, row-level security, exclusion constraints, triggers, and rules',
        },
        { name: 'Data movement', detail: 'Publications, subscriptions, foreign data, and views' },
      ],
    },
    {
      id: 'scaffolding',
      label: 'Scaffolding',
      icon: 'account_tree',
      kicker: 'DATABASE FIRST',
      title: 'Generate models from an existing database',
      body: 'Create EF models from PostgreSQL tables and types, with filters for the schemas you want.',
      items: [
        { name: 'Discovery', detail: 'Schemas, tables, columns, keys, indexes, and custom types' },
        {
          name: 'PostgreSQL details',
          detail: 'Provider-specific settings survive code generation',
        },
        {
          name: 'Security',
          detail: 'Design-time connections follow the documented security rules',
        },
      ],
    },
    {
      id: 'graph',
      label: 'SQL/PGQ',
      icon: 'share',
      kicker: 'POSTGRESQL 19',
      title: 'Use graph queries when the server supports them',
      body: 'Typed graph queries turn on only after BlueTusk confirms PostgreSQL 19 SQL/PGQ is available.',
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
      detail: 'Table partitions and inheritance remain visible in the EF model.',
    },
    {
      icon: 'shield',
      name: 'Row-level security',
      support: 'Migrations',
      detail: 'Create PostgreSQL row-level security policies through migrations.',
    },
    {
      icon: 'rule',
      name: 'CHECK + exclusion',
      support: 'Migrations + discovery',
      detail: 'Create and discover PostgreSQL CHECK and exclusion constraints.',
    },
    {
      icon: 'publish',
      name: 'Publications + subscriptions',
      support: 'Migrations',
      detail: 'Create logical replication publications and subscriptions in migrations.',
    },
    {
      icon: 'extension',
      name: 'Extensions + custom types',
      support: 'Migrations + mapping',
      detail: 'Installed extensions connect to the same types used by the provider.',
    },
    {
      icon: 'view_quilt',
      name: 'Views + foreign data',
      support: 'Migrations + scaffolding',
      detail: 'Discover views and foreign tables when generating a model.',
    },
    {
      icon: 'functions',
      name: 'Routines + operators',
      support: 'Migrations',
      detail: 'Create PostgreSQL functions and operators without renaming them.',
    },
    {
      icon: 'share',
      name: 'Property graphs',
      support: 'PG 19 guarded',
      detail: 'Graph features stay off on servers that do not support them.',
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
