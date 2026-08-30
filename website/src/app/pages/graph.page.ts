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
        <span class="eyebrow"><i class="live-dot"></i> POSTGRESQL 19 SQL/PGQ</span>
        <h1>Query relationships <em>inside PostgreSQL.</em></h1>
        <p>
          Define property graphs over relational tables, query them with SQL/PGQ, or maintain an
          authorized result as committed data changes. Every graph surface is capability guarded
          because PostgreSQL 19 is still a prerelease dependency.
        </p>
        <div class="hero-actions">
          <a mat-flat-button routerLink="/documentation/graph/sql-pgq" class="primary-action"
            >Read the SQL/PGQ guide</a
          ><a
            mat-stroked-button
            routerLink="/documentation/real-time/continuous-graph"
            class="secondary-action"
            >Read Continuous Graph</a
          >
        </div>
      </div>
      <div class="node-field" aria-hidden="true">
        <i></i><i></i><i></i><i></i><i></i><span>SQL/PGQ</span><span>WAL</span
        ><span>Authorized results</span>
      </div>
    </section>

    <section class="page-section">
      <header class="section-head">
        <div>
          <span>TWO DIFFERENT JOBS</span>
          <h2>Query now or maintain a result.</h2>
        </div>
        <p>Choose based on when the answer is needed and who owns delivery.</p>
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
            <a [routerLink]="model.route">Read the guide <mat-icon>arrow_forward</mat-icon></a>
          </article>
        }
      </div>
    </section>

    <section class="page-section capability-guard">
      <div>
        <span class="section-kicker">FAIL-CLOSED CAPABILITY CHECK</span>
        <h2>Graph APIs stay off until the server proves support.</h2>
        <p>
          BlueTusk checks the authenticated server’s catalogue and SQL/PGQ capability. A major
          version number alone is not treated as proof.
        </p>
        <bt-source-link [href]="source('docs/graph/README.md')" label="Capability contract" />
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
          <span>QUERY SURFACES</span>
          <h2>Use SQL directly or a bounded typed query.</h2>
        </div>
        <nav class="segmented-tabs" aria-label="Graph query example">
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
            @for (tag of selectedSample().tags; track $index) {
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
          <span>RUNNABLE EXAMPLES</span>
          <h2>See the recovery model in application code.</h2>
        </div>
        <p>These samples require a PostgreSQL 19 server with the negotiated SQL/PGQ capability.</p>
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
      status: 'PostgreSQL 19 prerelease',
      stage: 'preview',
      body: 'Create property-graph metadata over relational tables and query it when the application needs an answer.',
      route: '/documentation/graph/sql-pgq',
      rows: [
        { label: 'Execution', value: 'One PostgreSQL query' },
        { label: 'Result owner', value: 'The calling application' },
        {
          label: 'Availability',
          value: 'Prerelease evaluation; stable waits for PostgreSQL 19 GA',
        },
      ],
    },
    {
      id: 'continuous',
      icon: 'hub',
      kicker: 'MAINTAINED RESULT',
      title: 'Continuous Graph',
      status: '1.1 RC · PG19 gated',
      stage: 'preview',
      body: 'Use Streams and Live to refresh a registered graph query after relevant committed changes while preserving authorization.',
      route: '/documentation/real-time/continuous-graph',
      rows: [
        { label: 'Execution', value: 'Scoped update or authoritative repair' },
        { label: 'Result owner', value: 'A registered Live query and security scope' },
        { label: 'Recovery', value: 'Checkpointed source delivery and replay' },
        {
          label: 'Availability',
          value: 'Public RC; stable waits for PostgreSQL 19 GA and endurance',
        },
      ],
    },
  ] as const;
  protected readonly versions = [
    { number: '15', state: 'Unavailable', detail: 'No SQL/PGQ capability', enabled: false },
    { number: '16', state: 'Unavailable', detail: 'No SQL/PGQ capability', enabled: false },
    { number: '17', state: 'Unavailable', detail: 'No SQL/PGQ capability', enabled: false },
    { number: '18', state: 'Unavailable', detail: 'No SQL/PGQ capability', enabled: false },
    {
      number: '19',
      state: 'Guarded',
      detail: 'Enable only after capability negotiation',
      enabled: true,
    },
  ] as const;
  protected readonly samples = [
    {
      id: 'raw',
      label: 'Raw SQL',
      icon: 'terminal',
      title: 'Run SQL/PGQ directly',
      detail:
        'Use the Provider when the query is naturally expressed as SQL or falls outside the typed EF subset.',
      tags: ['MATCH', 'GRAPH_TABLE', 'Parameterized values'],
      file: 'GraphQuery.cs',
      code: `await using var command = dataSource.CreateCommand("""
    SELECT *
    FROM GRAPH_TABLE (
      social_graph
      MATCH (person IS Person)-[knows IS KNOWS]->(friend IS Person)
      COLUMNS (person.name AS person, friend.name AS friend)
    )
    """);

await using var reader = await command.ExecuteReaderAsync();`,
    },
    {
      id: 'ef',
      label: 'EF Core',
      icon: 'data_object',
      title: 'Build a typed linear path',
      detail:
        'Use model metadata for supported bounded shapes. BlueTusk quotes identifiers, binds captured values, and rejects unsupported patterns.',
      tags: ['Model metadata', 'Typed projection', 'Composable IQueryable'],
      file: 'GraphContext.cs',
      code: `var friends = await context.PropertyGraph("social", "application")
    .Match(pattern => pattern
        .Vertex<Person>("source", person => person.Id == personId)
        .Outgoing<Friendship>("edge")
        .Vertex<Person>("target"))
    .Select<FriendResult>(projection => projection
        .Property<Person, long>(
            "target", person => person.Id, result => result.PersonId)
        .Property<Person, string>(
            "target", person => person.Name, result => result.Name))
    .ToListAsync();`,
    },
  ] as const;
  protected readonly selectedSample = computed(
    () => this.samples.find((sample) => sample.id === this.activeSample()) ?? this.samples[0],
  );
  protected readonly workloads = [
    {
      icon: 'security',
      kicker: 'FRAUD',
      title: 'Connected financial risk',
      body: 'Register a bounded transfer path and observe an authorized result change after a relevant commit.',
      href: 'https://github.com/jphgardner/BlueTusk/tree/main/samples/BlueTusk.Samples.ContinuousGraph.Fraud',
    },
    {
      icon: 'router',
      kicker: 'NETWORK',
      title: 'Changing network topology',
      body: 'Maintain gateway relationships and expose updates through the same checkpoint and replay model.',
      href: 'https://github.com/jphgardner/BlueTusk/tree/main/samples/BlueTusk.Samples.ContinuousGraph.Network',
    },
  ] as const;
}
