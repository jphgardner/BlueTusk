import { Component, computed, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { CodePanel, SourceLink, StatusPill } from '../shared/technical-ui';
import { sourceUrl } from '../content/catalog';

@Component({
  selector: 'bt-provider-page',
  imports: [
    RouterLink,
    MatButtonModule,
    MatIconModule,
    MatInputModule,
    CodePanel,
    SourceLink,
    StatusPill,
  ],
  template: `
    <section class="page-hero split-hero provider-hero">
      <div>
        <span class="eyebrow"><i class="live-dot"></i> NATIVE ADO.NET PROVIDER</span>
        <h1>PostgreSQL from the <em>wire up.</em></h1>
        <p>
          Own connections, pools, commands, types, COPY, notifications, large objects, and
          replication through one protocol-aware implementation.
        </p>
        <div class="hero-actions">
          <a
            mat-flat-button
            routerLink="/documentation/getting-started/provider-overview"
            class="primary-action"
            >Build from source</a
          ><a
            mat-stroked-button
            href="https://github.com/jphgardner/BlueTusk/tree/main/samples/BlueTusk.Samples.AdoNet"
            target="_blank"
            rel="noreferrer"
            class="secondary-action"
            >Open the sample</a
          >
        </div>
      </div>
      <aside class="terminal-card">
        <header><i></i><i></i><i></i><span>local · source checkout</span></header>
        <code
          >git clone https://github.com/jphgardner/BlueTusk.git<br />cd BlueTusk<br />dotnet build
          BlueTusk.slnx<br /><br /><b>$env:BLUETUSK_TEST_CONNECTION_STRING</b><br />dotnet run
          --project samples/BlueTusk.Samples.AdoNet</code
        ><bt-status label="V1 code-ready · source preview" stage="gate-passed" />
      </aside>
    </section>

    <section class="page-section protocol-stack">
      <header class="section-head">
        <div>
          <span>OWNED DATA PATH</span>
          <h2>One stack. No hidden provider underneath.</h2>
        </div>
        <p>The provider has no runtime dependency on Npgsql; each layer owns a focused contract.</p>
      </header>
      <div class="stack-flow">
        @for (item of stack; track item.name; let index = $index) {
          <article>
            <small>0{{ index + 1 }}</small
            ><mat-icon>{{ item.icon }}</mat-icon
            ><strong>{{ item.name }}</strong
            ><span>{{ item.detail }}</span>
          </article>
        }
      </div>
    </section>

    <section class="page-section">
      <header class="section-head">
        <div>
          <span>CONNECTION MATRIX</span>
          <h2>Security and topology stay explicit.</h2>
        </div>
        <bt-source-link
          [href]="source('docs/ado-net/authentication.md')"
          label="Authentication guide"
        />
      </header>
      <div class="matrix-table connection-matrix">
        <div class="matrix-head">
          <span>Capability</span><span>Provider behavior</span><span>Operational boundary</span>
        </div>
        @for (row of connectionMatrix; track row.name) {
          <div>
            <strong>{{ row.name }}</strong
            ><span>{{ row.behavior }}</span
            ><small>{{ row.boundary }}</small>
          </div>
        }
      </div>
    </section>

    <section class="page-section type-explorer">
      <header class="section-head">
        <div>
          <span>TYPE CATALOGUE</span>
          <h2>Find the PostgreSQL shape you need.</h2>
        </div>
        <label class="filter-input"
          ><mat-icon>search</mat-icon
          ><input
            [value]="typeQuery()"
            (input)="typeQuery.set($any($event.target).value)"
            placeholder="Filter arrays, JSONB, ranges…"
            aria-label="Filter PostgreSQL types"
        /></label>
      </header>
      <div class="type-grid">
        @for (type of filteredTypes(); track type.name) {
          <article>
            <small>{{ type.group }}</small
            ><strong>{{ type.name }}</strong
            ><span>{{ type.clr }}</span>
            <p>{{ type.detail }}</p>
          </article>
        } @empty {
          <p class="empty-state">No type family matches that filter.</p>
        }
      </div>
    </section>

    <section class="page-section">
      <header class="section-head">
        <div>
          <span>NATIVE OPERATIONS</span>
          <h2>Specialized paths remain first class.</h2>
        </div>
        <p>
          These are provider-owned APIs with dedicated behavior and tests—not helper libraries
          layered over generic SQL.
        </p>
      </header>
      <div class="feature-rail">
        @for (feature of features; track feature.title) {
          <article>
            <mat-icon>{{ feature.icon }}</mat-icon>
            <div>
              <small>{{ feature.kicker }}</small>
              <h3>{{ feature.title }}</h3>
              <p>{{ feature.body }}</p>
              <a [href]="source(feature.source)" target="_blank" rel="noreferrer"
                >Read contract <mat-icon>arrow_forward</mat-icon></a
              >
            </div>
          </article>
        }
      </div>
    </section>

    <section class="page-section code-split">
      <div>
        <span class="section-kicker">FAMILIAR ADO.NET SHAPE</span>
        <h2>A long-lived data source is the entry point.</h2>
        <p>
          The sample uses the repository’s current public surface. API names remain synchronized
          with the source as the pre-V1 naming cleanup lands.
        </p>
      </div>
      <bt-code-panel file="Program.cs" [code]="quickstart" />
    </section>
  `,
})
export class ProviderPage {
  protected readonly source = sourceUrl;
  protected readonly typeQuery = signal('');
  protected readonly stack = [
    { name: 'Transport', icon: 'cable', detail: 'Sockets, TLS, bounded reads' },
    { name: 'Protocol', icon: 'lan', detail: 'Frames, portals, cancellation' },
    { name: 'Client', icon: 'terminal', detail: 'Sessions, replication, pipeline' },
    { name: 'ADO.NET', icon: 'storage', detail: 'Pools, commands, readers' },
    { name: 'Data', icon: 'data_object', detail: 'Types, COPY, notifications' },
  ] as const;
  protected readonly connectionMatrix = [
    {
      name: 'Credentials',
      behavior: 'Static values, callbacks, password files, and cloud token providers',
      boundary: 'Secrets are never written to diagnostic output',
    },
    {
      name: 'TLS',
      behavior: 'Certificate validation and client certificate support',
      boundary: 'Application chooses trust policy explicitly',
    },
    {
      name: 'Multi-host',
      behavior: 'Target-session selection and failover across configured hosts',
      boundary: 'Server role is verified, not inferred from ordering',
    },
    {
      name: 'Pooling',
      behavior: 'Bounded per-key pools with reset and health behavior',
      boundary: 'Session and transaction pooling modes are tested',
    },
    {
      name: 'Authentication',
      behavior: 'SCRAM, native OAuth, GSSAPI/Kerberos/SSPI, and legacy paths',
      boundary: 'Availability follows negotiated server capability',
    },
  ] as const;
  protected readonly types = [
    {
      group: 'Scalar',
      name: 'Numeric + money',
      clr: 'int · long · decimal',
      detail: 'Binary and text codecs preserve PostgreSQL semantics.',
    },
    {
      group: 'Structured',
      name: 'Arrays',
      clr: 'T[]',
      detail: 'Multidimensional bounds and extension arrays are catalogue-driven.',
    },
    {
      group: 'Structured',
      name: 'Ranges + multiranges',
      clr: 'Range<T>',
      detail: 'Inclusive, exclusive, empty, and infinite bounds remain explicit.',
    },
    {
      group: 'Document',
      name: 'JSON / JSONB',
      clr: 'string · JsonDocument',
      detail: 'Text and binary paths with typed parameters.',
    },
    {
      group: 'Network',
      name: 'inet / cidr / macaddr',
      clr: 'IPAddress + values',
      detail: 'Network-specific PostgreSQL values are not flattened.',
    },
    {
      group: 'Search',
      name: 'tsvector / tsquery',
      clr: 'typed values',
      detail: 'Full-text values and EF operators share the same catalogue.',
    },
    {
      group: 'User-defined',
      name: 'Enums + composites',
      clr: 'registered CLR types',
      detail: 'Registration is snapshotted when the data source is built.',
    },
    {
      group: 'Temporal',
      name: 'date / time / interval',
      clr: 'DateOnly · TimeOnly',
      detail: 'Infinity and interval structure preserve server behavior.',
    },
  ] as const;
  protected readonly filteredTypes = computed(() => {
    const q = this.typeQuery().trim().toLowerCase();
    return q
      ? this.types.filter((x) =>
          `${x.group} ${x.name} ${x.clr} ${x.detail}`.toLowerCase().includes(q),
        )
      : this.types;
  });
  protected readonly features = [
    {
      icon: 'file_upload',
      kicker: 'BULK DATA',
      title: 'COPY',
      body: 'Text and binary import/export with typed writes, cancellation, and explicit completion.',
      source: 'docs/ado-net/copy.md',
    },
    {
      icon: 'bolt',
      kicker: 'LATENCY',
      title: 'Pipeline mode',
      body: 'Ordered groups with protocol Sync boundaries and deterministic error attachment.',
      source: 'docs/pipeline-mode.md',
    },
    {
      icon: 'notifications',
      kicker: 'MESSAGING',
      title: 'LISTEN / NOTIFY',
      body: 'Notification dispatch respects connection lifecycle and asynchronous consumption.',
      source: 'docs/ado-net/notifications.md',
    },
    {
      icon: 'dataset',
      kicker: 'LARGE VALUES',
      title: 'Sequential + large objects',
      body: 'Bounded streaming prevents large fields and objects from forcing full buffering.',
      source: 'docs/ado-net/sequential-readers.md',
    },
    {
      icon: 'stream',
      kicker: 'WAL',
      title: 'Logical replication',
      body: 'COPY BOTH, pgoutput decoding, slots, and standby feedback expose committed changes.',
      source: 'docs/replication/README.md',
    },
    {
      icon: 'cloud',
      kicker: 'IDENTITY',
      title: 'Cloud credentials',
      body: 'AWS, Azure, and Google Cloud providers retain explicit live-test boundaries.',
      source: 'docs/ado-net/cloud-identity.md',
    },
  ] as const;
  protected readonly quickstart = `var connectionString = Environment.GetEnvironmentVariable(
    "BLUETUSK_TEST_CONNECTION_STRING")
    ?? "Host=localhost;Username=postgres;Password=postgres";

await using var dataSource =
    new BlueTuskDataSourceBuilder(connectionString).Build();

await using var command = dataSource.CreateCommand(
    "SELECT $1::int4 + $2::int4");

command.Parameters.Add(new BlueTuskParameter<int>(20));
command.Parameters.Add(new BlueTuskParameter<int>(22));

var answer = await command.ExecuteScalarAsync<int>();`;
}
