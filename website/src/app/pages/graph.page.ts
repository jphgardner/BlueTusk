import { Component, computed, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { CodePanel, StatusPill } from '../shared/technical-ui';
import { GRAPH_COMPATIBILITY } from '../content/catalog';

@Component({
  selector: 'bt-graph-page',
  imports: [RouterLink, MatButtonModule, MatIconModule, CodePanel, StatusPill],
  template: `
    <section class="page-hero graph-hero">
      <div>
        <span class="eyebrow">PORTABLE GRAPH · LOCALLY VERIFIED</span>
        <h1>Query relationships <em>in the PostgreSQL you run.</em></h1>
        <p>
          BlueTusk Graphs runs typed EF queries and Continuous Graph on
          {{ compatibility.portableTarget }} without SQL/PGQ. It uses the tables you already have.
          The implementation passed local compatibility tests and is still in release qualification.
        </p>
        <div class="hero-actions">
          <a mat-flat-button routerLink="/graph" fragment="graph-availability" class="primary-action"
            >Check compatibility</a
          ><a mat-stroked-button routerLink="/documentation/graph/sql-pgq" class="secondary-action"
            >Read the Graph quickstart</a
          >
        </div>
      </div>
      <div class="node-field" aria-hidden="true">
        <i></i><i></i><i></i><i></i><i></i><span>Nodes + edges</span><span>PostgreSQL</span
        ><span>Graph results</span>
      </div>
    </section>

    <section id="graph-availability" class="page-section" aria-labelledby="graph-availability-title">
      <header class="section-head">
        <div>
          <span>GRAPH AVAILABILITY</span>
          <h2 id="graph-availability-title">Two ways to run a graph query.</h2>
        </div>
        <p>
          BlueTusk Graphs is the EF default in the portable candidate. Use native SQL/PGQ by
          opting in on a server with verified support.
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
            <a [routerLink]="model.route" [fragment]="model.fragment">
              {{ model.action }} <mat-icon>arrow_forward</mat-icon>
            </a>
          </article>
        }
      </div>
    </section>

    <section class="page-section capability-guard" aria-labelledby="continuous-graph-title">
      <div>
        <span class="section-kicker">CONTINUOUS GRAPH</span>
        <h2 id="continuous-graph-title">Keep graph results current.</h2>
        <p>
          Continuous Graph refreshes a registered query after relevant commits. It updates affected
          results when their scope is known and reruns the query when a change could affect the
          full result.
        </p>
        <a class="back-link" routerLink="/documentation/real-time/continuous-graph">
          Read the Continuous Graph guide <mat-icon>arrow_forward</mat-icon>
        </a>
      </div>
      <aside class="graph-release-note">
        <bt-status label="Qualification pending" stage="pending" />
        <h3>What must pass before release</h3>
        <p>
          Local tests passed on {{ compatibility.portableTarget }}, including the Topology and Fraud
          application journeys. Local Windows and Linux benchmarks met the full-query target and
          missed the scoped-update target. Exact-candidate performance and 24-hour recovery still
          need to pass. The public <code>1.1.0-rc.1</code> packages predate portable Graph.
        </p>
        <a routerLink="/evidence">See current evidence</a>
      </aside>
    </section>

    <section class="page-section">
      <header class="section-head">
        <div>
          <span>QUERY EXAMPLES</span>
          <h2>Start with portable EF queries.</h2>
        </div>
        <nav class="segmented-tabs" aria-label="Graph query example">
          @for (sample of samples; track sample.id) {
            <button
              type="button"
              [class.active]="activeSample() === sample.id"
              [attr.aria-pressed]="activeSample() === sample.id"
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
        <p>
          The candidate samples run on the portable backend. Use the guide with a checkout or
          local package that contains the portable implementation.
        </p>
      </header>
      <div>
        @for (sample of workloads; track sample.title) {
          <article>
            <mat-icon>{{ sample.icon }}</mat-icon
            ><small>{{ sample.kicker }}</small>
            <h3>{{ sample.title }}</h3>
            <p>{{ sample.body }}</p>
            <a routerLink="/documentation/real-time/continuous-graph" fragment="run-a-working-example">
              Read sample instructions <mat-icon>arrow_forward</mat-icon>
            </a>
          </article>
        }
      </div>
    </section>
  `,
})
export class GraphPage {
  protected readonly compatibility = GRAPH_COMPATIBILITY;
  protected readonly activeSample = signal('ef');
  protected readonly models = [
    {
      id: 'portable',
      icon: 'account_tree',
      kicker: 'DEFAULT IN THE PORTABLE CANDIDATE',
      title: 'BlueTusk Graphs',
      status: GRAPH_COMPATIBILITY.portableStatus,
      stage: 'gate-passed',
      body: 'Define the graph once in EF Core. BlueTusk uses ordinary PostgreSQL joins over your existing vertex and edge tables.',
      route: '/documentation/graph/sql-pgq',
      fragment: undefined,
      action: 'Read the Graph quickstart',
      rows: [
        { label: 'Locally verified', value: GRAPH_COMPATIBILITY.portableTarget },
        { label: 'Server requirements', value: 'Ordinary PostgreSQL tables and indexes' },
        { label: 'Scope', value: 'Typed EF Core queries and Continuous Graph' },
      ],
    },
    {
      id: 'native',
      icon: 'terminal',
      kicker: 'OPTIONAL BACKEND',
      title: 'Native SQL/PGQ',
      status: GRAPH_COMPATIBILITY.nativeStatus,
      stage: 'preview',
      body: 'Use the database’s own property graph and GRAPH_TABLE support when the connected server reports the required capability.',
      route: '/documentation/graph/sql-pgq',
      fragment: 'select-native-sql-pgq',
      action: 'Read the native setup',
      rows: [
        { label: 'Preview fixture', value: GRAPH_COMPATIBILITY.nativeFixture },
        { label: 'Server requirements', value: 'Verified SQL/PGQ capability' },
        { label: 'Version boundary', value: 'PostgreSQL 19 Beta 4 removed SQL/PGQ' },
      ],
    },
  ] as const;
  protected readonly samples = [
    {
      id: 'raw',
      label: 'Native SQL/PGQ',
      icon: 'terminal',
      title: 'Run SQL/PGQ directly',
      detail:
        'Use GRAPH_TABLE only on a server that reports SQL/PGQ and already has the native property graph.',
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
      label: 'Portable EF Core',
      icon: 'data_object',
      title: 'Query existing tables with EF Core',
      detail:
        'Map vertices and edges with HasPropertyGraph. The portable backend turns the typed query into parameterized joins.',
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
    () => this.samples.find((sample) => sample.id === this.activeSample()) ?? this.samples[1],
  );
  protected readonly workloads = [
    {
      icon: 'security',
      kicker: 'FRAUD',
      title: 'Connected financial risk',
      body: 'Register a bounded transfer path and observe an authorized result change after a relevant commit.',
    },
    {
      icon: 'router',
      kicker: 'NETWORK',
      title: 'Changing network topology',
      body: 'Maintain gateway relationships and expose updates through the same checkpoint and replay model.',
    },
  ] as const;
}
