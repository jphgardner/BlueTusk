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
        <span class="eyebrow"><i class="live-dot"></i> DATA ACCESS FOR .NET</span>
        <h1>Fast, direct PostgreSQL access.<br /><em>Built for .NET.</em></h1>
        <p>
          Use familiar ADO.NET connections and commands with pooling, PostgreSQL types, bulk COPY,
          notifications, and replication built into one provider.
        </p>
        <div class="hero-actions">
          <a
            mat-flat-button
            routerLink="/documentation/getting-started/install"
            class="primary-action"
            >Install 1.1 RC</a
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
        <header><i></i><i></i><i></i><span>NuGet · exact prerelease</span></header>
        <code
          >dotnet new console -f net10.0<br />dotnet add package BlueTusk.Data --version
          1.1.0-rc.1<br /><br />dotnet restore --force-evaluate<br />dotnet build --configuration
          Release</code
        ><bt-status label="1.1.0-rc.1 · public" stage="gate-passed" />
      </aside>
    </section>

    <section class="page-section protocol-stack">
      <header class="section-head">
        <div>
          <span>DIRECT TO POSTGRESQL</span>
          <h2>Everything works together.</h2>
        </div>
        <p>BlueTusk talks directly to PostgreSQL and does not hide another provider underneath.</p>
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
          <span>CONNECT YOUR WAY</span>
          <h2>Secure connections, clearly configured.</h2>
        </div>
        <bt-source-link
          [href]="source('docs/ado-net/authentication.md')"
          label="Authentication guide"
        />
      </header>
      <div class="matrix-table connection-matrix">
        <div class="matrix-head">
          <span>Connection feature</span><span>What BlueTusk supports</span
          ><span>What you control</span>
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
          <span>POSTGRESQL TYPES</span>
          <h2>Use the database types you already rely on.</h2>
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
          <span>MORE THAN QUERIES</span>
          <h2>Bulk data, notifications, and replication are built in.</h2>
        </div>
        <p>Use dedicated, tested APIs for high-volume and real-time database work.</p>
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
                >Read the technical guide <mat-icon>arrow_forward</mat-icon></a
              >
            </div>
          </article>
        }
      </div>
    </section>

    <section class="page-section code-split">
      <div>
        <span class="section-kicker">FAMILIAR ADO.NET</span>
        <h2>Create one data source and reuse it.</h2>
        <p>
          This is the public 1.x API. Automated checks keep the packages, API reference, examples,
          and compatibility promises in sync.
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
    { name: 'Network', icon: 'cable', detail: 'Sockets, TLS, controlled memory use' },
    { name: 'PostgreSQL', icon: 'lan', detail: 'Database messages and cancellation' },
    { name: 'BlueTusk client', icon: 'terminal', detail: 'Sessions, replication, batching' },
    { name: 'ADO.NET', icon: 'storage', detail: 'Connection pools, commands, readers' },
    { name: 'Your data', icon: 'data_object', detail: 'Types, bulk COPY, notifications' },
  ] as const;
  protected readonly connectionMatrix = [
    {
      name: 'Credentials',
      behavior: 'Static values, callbacks, password files, and cloud token providers',
      boundary: 'Choose where credentials come from; BlueTusk keeps them out of logs',
    },
    {
      name: 'TLS',
      behavior: 'Server certificate checks and client certificates',
      boundary: 'Choose which certificates your application trusts',
    },
    {
      name: 'Multi-host',
      behavior: 'Connect to the right server and fail over between configured hosts',
      boundary: 'Choose the required server role; BlueTusk verifies it',
    },
    {
      name: 'Pooling',
      behavior: 'Reusable connection pools with limits, reset, and health checks',
      boundary: 'Choose pool size and whether session state can be retained',
    },
    {
      name: 'Authentication',
      behavior: 'Password, OAuth, Kerberos/SSPI, and supported legacy methods',
      boundary: 'The server and selected login method determine what is available',
    },
  ] as const;
  protected readonly types = [
    {
      group: 'Scalar',
      name: 'Numeric + money',
      clr: 'int · long · decimal',
      detail: 'Values keep PostgreSQL precision and behavior.',
    },
    {
      group: 'Structured',
      name: 'Arrays',
      clr: 'T[]',
      detail: 'Supports one-dimensional and multidimensional PostgreSQL arrays.',
    },
    {
      group: 'Structured',
      name: 'Ranges + multiranges',
      clr: 'Range<T>',
      detail: 'Keeps inclusive, exclusive, empty, and unbounded ranges intact.',
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
      detail: 'Works with PostgreSQL network addresses as typed values.',
    },
    {
      group: 'Search',
      name: 'tsvector / tsquery',
      clr: 'typed values',
      detail: 'Use PostgreSQL full-text search values in ADO.NET and EF Core.',
    },
    {
      group: 'User-defined',
      name: 'Enums + composites',
      clr: 'registered CLR types',
      detail: 'Register your .NET types once when the data source is created.',
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
      body: 'Import or export large amounts of data efficiently, with typed values and cancellation.',
      source: 'docs/ado-net/copy.md',
    },
    {
      icon: 'bolt',
      kicker: 'FASTER BATCHES',
      title: 'Pipeline mode',
      body: 'Send groups of commands with fewer network waits while keeping results and errors in order.',
      source: 'docs/pipeline-mode.md',
    },
    {
      icon: 'notifications',
      kicker: 'MESSAGING',
      title: 'LISTEN / NOTIFY',
      body: 'Receive PostgreSQL notifications asynchronously while the connection stays healthy.',
      source: 'docs/ado-net/notifications.md',
    },
    {
      icon: 'dataset',
      kicker: 'LARGE VALUES',
      title: 'Sequential + large objects',
      body: 'Stream large fields and objects without loading the entire value into memory.',
      source: 'docs/ado-net/sequential-readers.md',
    },
    {
      icon: 'stream',
      kicker: 'DATABASE CHANGES',
      title: 'Logical replication',
      body: 'Read committed PostgreSQL changes for Streams, Sync, Live, or your own processing.',
      source: 'docs/replication/README.md',
    },
    {
      icon: 'cloud',
      kicker: 'IDENTITY',
      title: 'Cloud credentials',
      body: 'Use short-lived AWS, Azure, or Google Cloud login tokens instead of stored passwords.',
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
