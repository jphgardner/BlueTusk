import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { CodePanel, SourceLink, StatusPill } from '../shared/technical-ui';
import { sourceUrl } from '../content/catalog';

@Component({
  selector: 'bt-provider-page',
  imports: [RouterLink, MatButtonModule, MatIconModule, CodePanel, SourceLink, StatusPill],
  template: `
    <section class="page-hero split-hero provider-hero">
      <div>
        <span class="eyebrow"><i class="live-dot"></i> ADO.NET PROVIDER</span>
        <h1>Direct PostgreSQL access.<br /><em>One long-lived data source.</em></h1>
        <p>
          Use standard ADO.NET connections, commands, readers, and transactions with native
          PostgreSQL types, COPY, notifications, and replication. BlueTusk owns the complete wire
          path; it does not wrap another provider.
        </p>
        <div class="hero-actions">
          <a
            mat-flat-button
            routerLink="/documentation/getting-started/quickstart"
            class="primary-action"
            >Run the quickstart</a
          ><a
            mat-stroked-button
            routerLink="/documentation/getting-started/provider-overview"
            class="secondary-action"
            >Read the Provider guide</a
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

    <section class="page-section code-split">
      <div>
        <span class="section-kicker">THE APPLICATION ENTRY POINT</span>
        <h2>Create the data source once.</h2>
        <p>
          A data source owns configuration, the physical connection pool, runtime codecs, and the
          PostgreSQL type catalogue. Open short-lived logical connections or commands from it as
          work arrives.
        </p>
        <bt-source-link [href]="source('docs/ado-net/README.md')" label="ADO.NET reference" />
      </div>
      <bt-code-panel file="Program.cs" [code]="quickstart" />
    </section>

    <section class="page-section">
      <header class="section-head">
        <div>
          <span>WHAT THE PROVIDER OWNS</span>
          <h2>Use the API that matches the database job.</h2>
        </div>
        <p>The core provider stays focused on PostgreSQL communication and ADO.NET behavior.</p>
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
                >Read the guide <mat-icon>arrow_forward</mat-icon></a
              >
            </div>
          </article>
        }
      </div>
    </section>

    <section class="page-section type-explorer">
      <header class="section-head">
        <div>
          <span>POSTGRESQL-NATIVE VALUES</span>
          <h2>Keep database meaning intact.</h2>
        </div>
        <bt-source-link [href]="source('docs/types/README.md')" label="Type mapping guide" />
      </header>
      <div class="type-grid">
        @for (type of types; track type.name) {
          <article>
            <small>{{ type.group }}</small
            ><strong>{{ type.name }}</strong
            ><span>{{ type.clr }}</span>
            <p>{{ type.detail }}</p>
          </article>
        }
      </div>
    </section>

    <section class="page-section">
      <header class="section-head">
        <div>
          <span>APPLICATION RESPONSIBILITIES</span>
          <h2>Secure defaults still need deliberate configuration.</h2>
        </div>
        <bt-source-link
          [href]="source('docs/ado-net/authentication.md')"
          label="Authentication guide"
        />
      </header>
      <div class="matrix-table connection-matrix">
        <div class="matrix-head">
          <span>Area</span><span>BlueTusk provides</span><span>You decide</span>
        </div>
        @for (row of responsibilities; track row.name) {
          <div>
            <strong>{{ row.name }}</strong
            ><span>{{ row.provider }}</span
            ><small>{{ row.application }}</small>
          </div>
        }
      </div>
    </section>
  `,
})
export class ProviderPage {
  protected readonly source = sourceUrl;
  protected readonly features = [
    {
      icon: 'cable',
      kicker: 'CONNECTIONS',
      title: 'Authentication, pooling, and failover',
      body: 'Open secure sessions, bound pool use, reset state, and route across configured PostgreSQL hosts.',
      source: 'docs/ado-net/pooling.md',
    },
    {
      icon: 'terminal',
      kicker: 'DATABASE WORK',
      title: 'Commands, transactions, and batches',
      body: 'Bind typed parameters, stream results, cancel work, and group commands without SQL interpolation.',
      source: 'docs/ado-net/README.md',
    },
    {
      icon: 'file_upload',
      kicker: 'DATA MOVEMENT',
      title: 'COPY, notifications, and large values',
      body: 'Use dedicated APIs for bulk transfer, LISTEN/NOTIFY, sequential reads, and PostgreSQL large objects.',
      source: 'docs/ado-net/copy.md',
    },
    {
      icon: 'stream',
      kicker: 'DATABASE CHANGES',
      title: 'Physical and logical replication',
      body: 'Open dedicated replication sessions, decode pgoutput, and advance WAL feedback deliberately.',
      source: 'docs/replication/README.md',
    },
  ] as const;
  protected readonly types = [
    {
      group: 'Structured',
      name: 'Arrays + records',
      clr: 'T[] · mapped CLR types',
      detail: 'Arrays, enums, domains, composites, and lossless records use catalogue identity.',
    },
    {
      group: 'Bounds',
      name: 'Ranges + multiranges',
      clr: 'BlueTuskRange<T>',
      detail: 'Inclusive, exclusive, empty, infinite, and unbounded states remain distinct.',
    },
    {
      group: 'Documents',
      name: 'JSON / JSONB / XML',
      clr: 'string · JSON values',
      detail: 'The selected PostgreSQL identity is retained for parameters, including nulls.',
    },
    {
      group: 'Specialized',
      name: 'Network + geometry',
      clr: 'typed value objects',
      detail: 'Network, geometric, bit-string, money, and full-text values stay strongly typed.',
    },
    {
      group: 'Temporal',
      name: 'Date, time, and interval',
      clr: 'DateOnly · TimeOnly · values',
      detail: 'PostgreSQL infinity, 24:00, time-zone, and interval behavior is preserved.',
    },
    {
      group: 'Extensions',
      name: 'PostGIS + pgvector + more',
      clr: 'focused package types',
      detail:
        'Optional packages add codecs without making the core provider carry every extension.',
    },
  ] as const;
  protected readonly responsibilities = [
    {
      name: 'Credentials',
      provider: 'Callbacks, password files, client certificates, and cloud identity adapters',
      application: 'Choose the source, scope, and rotation policy',
    },
    {
      name: 'TLS',
      provider: 'Platform certificate validation and explicit client-certificate support',
      application: 'Choose trusted authorities and deployment policy',
    },
    {
      name: 'Pool limits',
      provider: 'Bounded pools, cancellable waiters, reset, health, and lifetime controls',
      application: 'Set limits from measured concurrency and database capacity',
    },
    {
      name: 'Session state',
      provider: 'Safe reset and rejection of incompatible multiplexed work',
      application: 'Use dedicated sessions when state must persist',
    },
  ] as const;
  protected readonly quickstart = `using BlueTusk.Data;

var connectionString = Environment.GetEnvironmentVariable(
    "BLUETUSK_CONNECTION_STRING")
    ?? throw new InvalidOperationException("Connection string is required.");

await using var dataSource =
    new BlueTuskDataSourceBuilder(connectionString).Build();

await using var command = dataSource.CreateCommand(
    "SELECT $1::int4 + $2::int4");

command.Parameters.Add(new BlueTuskParameter<int>(20));
command.Parameters.Add(new BlueTuskParameter<int>(22));

var answer = await command.ExecuteScalarAsync<int>();`;
}
