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
        <h1>Use EF Core.<br /><em>Keep PostgreSQL visible.</em></h1>
        <p>
          Use DbContext, LINQ, migrations, and database-first tooling without reducing PostgreSQL to
          a lowest-common-denominator database. Provider-specific behavior remains explicit and
          capability guarded.
        </p>
        <div class="hero-actions">
          <a mat-flat-button routerLink="/documentation/ef-core/overview" class="primary-action"
            >Read the EF Core guide</a
          ><a
            mat-stroked-button
            href="https://github.com/jphgardner/BlueTusk/tree/main/samples/BlueTusk.Samples.EntityFrameworkCore"
            target="_blank"
            rel="noreferrer"
            class="secondary-action"
            >Open the sample</a
          >
        </div>
      </div>
      <aside class="metric-cluster">
        <bt-status label="1.1.0-rc.1 · public" stage="gate-passed" />
        <div><strong>LINQ</strong><span>translated to PostgreSQL SQL</span></div>
        <div><strong>15–19</strong><span>live server matrix</span></div>
        <div><strong>0</strong><span>unexpected official-suite failures</span></div>
        <small>ADO.NET AND EF SHARE ONE DATA SOURCE</small>
      </aside>
    </section>

    <section class="page-section code-split">
      <div>
        <span class="section-kicker">ONE PROVIDER CONFIGURATION</span>
        <h2>Share the data source with DbContext.</h2>
        <p>
          EF-created logical connections reuse the same physical pool, runtime codecs, type
          catalogue, authentication, and diagnostics as direct ADO.NET work.
        </p>
        <bt-source-link [href]="source('docs/ef-core/README.md')" label="Complete EF reference" />
      </div>
      <bt-code-panel file="AppDbContext.cs" [code]="setupCode" />
    </section>

    <section class="page-section">
      <header class="section-head">
        <div>
          <span>CHOOSE A TASK</span>
          <h2>Use the PostgreSQL feature through the right EF workflow.</h2>
        </div>
        <p>Choose a task to see the supported surface and its boundary.</p>
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

    <section class="crosscut-band">
      @for (rule of rules; track rule.title) {
        <article>
          <mat-icon>{{ rule.icon }}</mat-icon>
          <div>
            <small>{{ rule.kicker }}</small>
            <h3>{{ rule.title }}</h3>
            <p>{{ rule.body }}</p>
          </div>
        </article>
      }
    </section>

    <aside class="truth-note">
      <mat-icon>fact_check</mat-icon>
      <p>
        <strong>Compatibility is measured, not implied.</strong> BlueTusk runs Microsoft’s
        provider-facing relational suite and separate live PostgreSQL tests. PostgreSQL-specific
        features are supported only where the provider documents and tests them.
      </p>
      <a routerLink="/evidence">Inspect the evidence</a>
    </aside>
  `,
})
export class EfCorePage {
  protected readonly source = sourceUrl;
  protected readonly activeTab = signal('queries');
  protected readonly tabs = [
    {
      id: 'queries',
      label: 'Querying',
      icon: 'query_stats',
      kicker: 'LINQ TRANSLATION',
      title: 'Write LINQ that produces PostgreSQL SQL',
      body: 'Use standard relational LINQ and focused PostgreSQL extensions. Unsupported shapes fail during translation instead of silently moving work to the client.',
      items: [
        {
          name: 'Collections and documents',
          detail: 'Arrays, ranges, multiranges, JSON, lateral expansion, and set-returning roots',
        },
        {
          name: 'PostgreSQL operators',
          detail: 'Full-text, network, regex, row-value, geometric, and typed scalar functions',
        },
        {
          name: 'Advanced relational SQL',
          detail: 'CTEs, row locking, window functions, RETURNING, ON CONFLICT, and MERGE',
        },
      ],
    },
    {
      id: 'mappings',
      label: 'Mappings',
      icon: 'conversion_path',
      kicker: 'ONE TYPE SYSTEM',
      title: 'Use the same PostgreSQL values in EF and ADO.NET',
      body: 'The data source owns the type catalogue and extension codecs; EF adds relational mapping and query translation on top.',
      items: [
        { name: 'Built-in', detail: 'Temporal, network, geometric, JSON, arrays, and ranges' },
        { name: 'User-defined', detail: 'Enums, domains, composites, records, and custom ranges' },
        {
          name: 'Extensions',
          detail: 'PostGIS, pgvector, TimescaleDB, citext, hstore, ltree, and pg_trgm',
        },
      ],
    },
    {
      id: 'schema',
      label: 'Schema',
      icon: 'schema',
      kicker: 'MIGRATIONS',
      title: 'Represent PostgreSQL schema deliberately',
      body: 'Use provider APIs for supported PostgreSQL objects and explicit migration SQL when an application needs a specialized operation outside that surface.',
      items: [
        {
          name: 'Tables and constraints',
          detail:
            'Identity, generated columns, indexes, partitions, inheritance, CHECK, and exclusion',
        },
        {
          name: 'Security and behavior',
          detail: 'Row-level security, triggers, rules, collations, and tablespaces',
        },
        {
          name: 'Database programs',
          detail: 'Views, routines, operators, publications, subscriptions, and foreign data',
        },
      ],
    },
    {
      id: 'scaffolding',
      label: 'Database first',
      icon: 'account_tree',
      kicker: 'REVERSE ENGINEERING',
      title: 'Generate a model without discarding PostgreSQL metadata',
      body: 'Discover selected schemas and tables, retain provider-owned metadata, and keep credentials out of generated code by default.',
      items: [
        { name: 'Selection', detail: 'Repeatable schema and table filters' },
        {
          name: 'PostgreSQL metadata',
          detail: 'Types, indexes, constraints, views, and schema objects',
        },
        {
          name: 'Tooling',
          detail: 'Standard dotnet ef integration and the bluetusk scaffold tool',
        },
      ],
    },
  ] as const;
  protected readonly selectedTab = computed(
    () => this.tabs.find((tab) => tab.id === this.activeTab()) ?? this.tabs[0],
  );
  protected readonly rules = [
    {
      icon: 'dns',
      kicker: 'SERVER EXECUTION',
      title: 'Queries stay on PostgreSQL',
      body: 'Provider extensions translate to SQL or fail clearly; they do not hide accidental client evaluation.',
    },
    {
      icon: 'shield',
      kicker: 'CAPABILITIES',
      title: 'Optional features are checked',
      body: 'Extensions and PostgreSQL 19 SQL/PGQ activate only when the connected server exposes the required capability.',
    },
    {
      icon: 'history',
      kicker: 'SCHEMA OWNERSHIP',
      title: 'Migrations remain reviewable',
      body: 'Generated operations preserve PostgreSQL intent while ownership and application-specific grants stay explicit.',
    },
  ] as const;
  protected readonly setupCode = `await using var dataSource =
    new BlueTuskDataSourceBuilder(connectionString).Build();

services.AddDbContext<AppDbContext>(options =>
    options.UseBlueTusk(dataSource));

await using var context = serviceProvider
    .GetRequiredService<AppDbContext>();

var active = await context.Customers
    .Where(customer => customer.Tags.Contains("active"))
    .ToListAsync();`;
}
