import { Component, computed, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { CodePanel, SourceLink, StatusPill } from '../shared/technical-ui';
import { sourceUrl } from '../content/catalog';

@Component({
  selector: 'bt-graph-page',
  imports: [RouterLink, MatButtonModule, MatIconModule, CodePanel, SourceLink, StatusPill],
  template: `
    <section class="page-hero graph-hero">
      <div>
        <span class="eyebrow"><i class="live-dot"></i> CONNECTED DATA</span>
        <h1>Graph in PostgreSQL.<br /><em>Two different jobs.</em></h1>
        <p>
          Use PostgreSQL 19 SQL/PGQ for server-side property graph queries. Use Continuous Graph for
          checkpointed projections that react to committed changes.
        </p>
        <div class="hero-actions">
          <a mat-flat-button routerLink="/documentation/graph/sql-pgq" class="primary-action"
            >SQL/PGQ guide</a
          ><a
            mat-stroked-button
            routerLink="/documentation/real-time/continuous-graph"
            class="secondary-action"
            >Continuous Graph guide</a
          >
        </div>
      </div>
      <div class="node-field" aria-hidden="true">
        <i></i><i></i><i></i><i></i><i></i><span>SQL/PGQ</span><span>WAL</span
        ><span>Projection</span>
      </div>
    </section>

    <section class="page-section">
      <header class="section-head">
        <div>
          <span>CHOOSE THE MODEL</span>
          <h2>Query graph state or maintain it continuously.</h2>
        </div>
        <p>The two surfaces have separate runtime requirements, evidence and release boundaries.</p>
      </header>
      <div class="comparison-grid">
        @for (model of models; track model.id) {
          <article [attr.data-tone]="model.id">
            <header>
              <mat-icon>{{ model.icon }}</mat-icon>
              <div>
                <small>{{ model.kicker }}</small>
                <h3>{{ model.title }}</h3>
              </div>
              <bt-status [label]="model.status" [stage]="model.stage" />
            </header>
            <p>{{ model.body }}</p>
            <dl>
              @for (row of model.rows; track row.label) {
                <div>
                  <dt>{{ row.label }}</dt>
                  <dd>{{ row.value }}</dd>
                </div>
              }
            </dl>
            <a [routerLink]="model.route"
              >Read the implementation guide <mat-icon>arrow_forward</mat-icon></a
            >
          </article>
        }
      </div>
    </section>

    <section class="page-section capability-guard">
      <div>
        <span class="section-kicker">SERVER CAPABILITY GUARD</span>
        <h2>PostgreSQL 19 is detected, not assumed.</h2>
        <p>
          The authenticated connection probes the documented information schema before enabling
          SQL/PGQ. A version string alone is not treated as product support.
        </p>
        <bt-source-link [href]="source('docs/graph/README.md')" />
      </div>
      <div class="version-rail">
        @for (version of versions; track version.number) {
          <article [class.enabled]="version.enabled">
            <strong>{{ version.number }}</strong
            ><span>{{ version.state }}</span
            ><small>{{ version.detail }}</small>
          </article>
        }
      </div>
    </section>

    <section class="page-section">
      <header class="section-head">
        <div>
          <span>QUERY SURFACE</span>
          <h2>Start raw. Move to typed where supported.</h2>
        </div>
        <nav class="segmented-tabs">
          @for (sample of samples; track sample.id) {
            <button
              type="button"
              [class.active]="activeSample() === sample.id"
              (click)="activeSample.set(sample.id)"
            >
              {{ sample.label }}
            </button>
          }
        </nav>
      </header>
      <div class="code-split compact">
        <div>
          <mat-icon>{{ selectedSample().icon }}</mat-icon>
          <h3>{{ selectedSample().title }}</h3>
          <p>{{ selectedSample().detail }}</p>
          <div class="tag-cloud">
            @for (tag of selectedSample().tags; track tag) {
              <span>{{ tag }}</span>
            }
          </div>
        </div>
        <bt-code-panel [file]="selectedSample().file" [code]="selectedSample().code" />
      </div>
    </section>

    <section class="page-section sample-workloads">
      <header class="section-head">
        <div>
          <span>CONTINUOUS WORKLOADS</span>
          <h2>Run the projections that define the boundary.</h2>
        </div>
        <p>Both samples are executable projects in the repository, not conceptual mockups.</p>
      </header>
      <div>
        @for (sample of workloads; track sample.title) {
          <article>
            <mat-icon>{{ sample.icon }}</mat-icon
            ><small>{{ sample.kicker }}</small>
            <h3>{{ sample.title }}</h3>
            <p>{{ sample.body }}</p>
            <a [href]="sample.href" target="_blank" rel="noreferrer"
              >Open sample <mat-icon>open_in_new</mat-icon></a
            >
          </article>
        }
      </div>
    </section>
  `,
})
export class GraphPage {
  protected readonly source = sourceUrl;
  protected readonly activeSample = signal('raw');
  protected readonly models = [
    {
      id: 'sql',
      icon: 'account_tree',
      kicker: 'DATABASE QUERY',
      title: 'PostgreSQL SQL/PGQ',
      status: 'PG 19 GA pending',
      stage: 'preview',
      body: 'Create, discover, migrate, scaffold, and query property graphs through a capability-guarded PostgreSQL 19 surface.',
      route: '/documentation/graph/sql-pgq',
      rows: [
        { label: 'Source', value: 'PostgreSQL property graphs' },
        { label: 'Execution', value: 'On-demand SQL query' },
        { label: 'Availability', value: 'Beta 3 tested; GA gated' },
      ],
    },
    {
      id: 'continuous',
      icon: 'hub',
      kicker: 'REACTIVE PROJECTION',
      title: 'Continuous Graph',
      status: '1.1 candidate',
      stage: 'gate-passed',
      body: 'Maintain authorised graph results through trusted CDC deltas, compiler-scoped GRAPH_TABLE deltas, and fail-closed full repair.',
      route: '/documentation/real-time/continuous-graph',
      rows: [
        { label: 'Source', value: 'Streams transactions' },
        { label: 'Execution', value: 'Three-tier incremental maintenance' },
        { label: 'Security', value: 'Original RLS and Live scope retained' },
        { label: 'Availability', value: 'PostgreSQL 19 GA + evidence gated' },
      ],
    },
  ] as const;
  protected readonly versions = [
    { number: '15', state: 'Guarded', detail: 'Empty safe discovery', enabled: false },
    { number: '16', state: 'Guarded', detail: 'Empty safe discovery', enabled: false },
    { number: '17', state: 'Guarded', detail: 'Empty safe discovery', enabled: false },
    { number: '18', state: 'Guarded', detail: 'Empty safe discovery', enabled: false },
    { number: '19', state: 'Enabled', detail: 'SQL/PGQ probe + tests', enabled: true },
  ] as const;
  protected readonly samples = [
    {
      id: 'raw',
      label: 'Raw SQL',
      icon: 'terminal',
      title: 'Execute SQL/PGQ directly',
      detail:
        'The provider can execute graph DDL and MATCH queries like any capability-guarded PostgreSQL statement.',
      tags: ['MATCH', 'GRAPH_TABLE', 'COLUMNS'],
      file: 'GraphQuery.cs',
      code: `await using var command = dataSource.CreateCommand("""
    SELECT *
    FROM GRAPH_TABLE (
      social_graph
      MATCH (person:Person)-[knows:KNOWS]->(friend:Person)
      COLUMNS (person.name AS person, friend.name AS friend)
    )
    """);

await using var reader = await command.ExecuteReaderAsync();`,
    },
    {
      id: 'ef',
      label: 'EF Core',
      icon: 'data_object',
      title: 'Translate supported typed constructs',
      detail:
        'Typed EF constructs produce SQL/PGQ while unsupported shapes fail explicitly instead of falling back silently.',
      tags: ['Migrations', 'Scaffolding', 'Typed translation'],
      file: 'GraphContext.cs',
      code: `var paths = await context.PropertyGraph("social_graph")
    .Match<Person, Knows, Person>()
    .Where(path => path.Edge.Since >= 2024)
    .Select(path => new { path.Source.Name, Friend = path.Target.Name })
    .ToListAsync();`,
    },
  ] as const;
  protected readonly selectedSample = computed(
    () => this.samples.find((x) => x.id === this.activeSample()) ?? this.samples[0],
  );
  protected readonly workloads = [
    {
      icon: 'security',
      kicker: 'FRAUD',
      title: 'Connected-risk projection',
      body: 'Apply account and transfer changes into a graph used to surface suspicious paths.',
      href: 'https://github.com/jphgardner/BlueTusk/tree/main/samples/BlueTusk.Samples.ContinuousGraph.Fraud',
    },
    {
      icon: 'router',
      kicker: 'NETWORK',
      title: 'Topology projection',
      body: 'Maintain network nodes and links from acknowledged committed changes.',
      href: 'https://github.com/jphgardner/BlueTusk/tree/main/samples/BlueTusk.Samples.ContinuousGraph.Network',
    },
  ] as const;
}
