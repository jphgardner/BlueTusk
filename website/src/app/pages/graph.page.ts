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
        <h1>Query connected data <em>where it lives.</em></h1>
        <p>
          Run graph queries inside PostgreSQL, or keep results updated as your data changes. Choose
          the approach that matches your application.
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
        ><span>Live results</span>
      </div>
    </section>

    <section class="page-section">
      <header class="section-head">
        <div>
          <span>CHOOSE THE MODEL</span>
          <h2>Run a graph query now or keep results live.</h2>
        </div>
        <p>
          Choose a one-off database query or a continuously updated result for your application.
        </p>
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
              >Read the technical guide <mat-icon>arrow_forward</mat-icon></a
            >
          </article>
        }
      </div>
    </section>

    <section class="page-section capability-guard">
      <div>
        <span class="section-kicker">SAFE VERSION CHECK</span>
        <h2>Graph features turn on only when the server is ready.</h2>
        <p>
          BlueTusk checks that PostgreSQL actually provides SQL/PGQ before using it. It does not
          assume support from the version number alone.
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
          <span>WRITE GRAPH QUERIES</span>
          <h2>Use SQL directly or build a typed EF query.</h2>
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
          <span>PRODUCTION-STYLE EXAMPLES</span>
          <h2>See continuously updated graph results in action.</h2>
        </div>
        <p>Both are complete, runnable repository projects rather than simplified mock-ups.</p>
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
      body: 'Store and query relationships directly in PostgreSQL 19, with migrations and database-first tooling.',
      route: '/documentation/graph/sql-pgq',
      rows: [
        { label: 'Source', value: 'PostgreSQL property graphs' },
        { label: 'How it runs', value: 'Query the database when you need an answer' },
        { label: 'Availability', value: 'Tested on Beta 3; stable waits for GA' },
      ],
    },
    {
      id: 'continuous',
      icon: 'hub',
      kicker: 'LIVE GRAPH RESULTS',
      title: 'Continuous Graph',
      status: '1.2 expansion in development',
      stage: 'preview',
      body: 'Keep permitted graph results updated as rows change. BlueTusk 1.2 adds bounded multi-hop paths, either-direction matching, and multiple labels while keeping a full safe refresh as the fallback.',
      route: '/documentation/real-time/continuous-graph',
      rows: [
        { label: 'Source', value: 'Committed changes from Streams' },
        { label: 'How it runs', value: 'Small update, scoped query, or full safe refresh' },
        { label: '1.2 patterns', value: '1–8 hops, either direction, multiple labels' },
        { label: 'Security', value: 'Keeps the original user permissions and row rules' },
        { label: 'Availability', value: 'Public RC; stable waits for PostgreSQL 19 GA' },
      ],
    },
  ] as const;
  protected readonly versions = [
    { number: '15', state: 'Off', detail: 'Graph support unavailable', enabled: false },
    { number: '16', state: 'Off', detail: 'Graph support unavailable', enabled: false },
    { number: '17', state: 'Off', detail: 'Graph support unavailable', enabled: false },
    { number: '18', state: 'Off', detail: 'Graph support unavailable', enabled: false },
    { number: '19', state: 'On', detail: 'Feature check and tests pass', enabled: true },
  ] as const;
  protected readonly samples = [
    {
      id: 'raw',
      label: 'Raw SQL',
      icon: 'terminal',
      title: 'Run SQL/PGQ directly',
      detail:
        'Use SQL to create a property graph and run MATCH queries directly through the provider.',
      tags: ['MATCH', 'GRAPH_TABLE', 'COLUMNS'],
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
      title: 'Build bounded graph paths with EF Core',
      detail:
        'Use one typed API for multiple labels, either-direction traversal, and bounded multi-hop paths. Unsafe shapes stop with a clear error.',
      tags: ['1–8 hops', 'Either direction', 'Multiple labels'],
      file: 'GraphContext.cs',
      code: `var paths = await context.PropertyGraph("social_graph")
    .Match(pattern => pattern
        .Vertex<Person>("source")
        .LabelsAnyOf("person", "customer")
        .UndirectedPath<Knows>("path", 1, 4)
        .LabelsAnyOf("knows", "works_with")
        .Vertex<Person>("target")
        .LabelsAnyOf("person", "customer"))
    .Select<PathResult>(projection => projection
        .Property<Person, long>(
            "source", person => person.Id, result => result.SourceId)
        .Property<Person, long>(
            "target", person => person.Id, result => result.TargetId))
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
      title: 'Detect connected financial risk',
      body: 'Keep account and transfer relationships updated so suspicious paths can be found quickly.',
      href: 'https://github.com/jphgardner/BlueTusk/tree/main/samples/BlueTusk.Samples.ContinuousGraph.Fraud',
    },
    {
      icon: 'router',
      kicker: 'NETWORK',
      title: 'Track a changing network',
      body: 'Keep network devices and their connections updated from committed database changes.',
      href: 'https://github.com/jphgardner/BlueTusk/tree/main/samples/BlueTusk.Samples.ContinuousGraph.Network',
    },
  ] as const;
}
