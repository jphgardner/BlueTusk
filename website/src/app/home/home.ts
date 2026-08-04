import { Component, computed, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

interface CodeExample {
  id: string;
  label: string;
  file: string;
  code: string;
}

@Component({
  selector: 'app-home',
  imports: [RouterLink, MatButtonModule, MatButtonToggleModule, MatIconModule, MatTooltipModule],
  templateUrl: './home.html',
  styleUrl: './home.scss',
})
export class Home {
  protected readonly selectedExample = signal('ado');
  protected readonly selectedPath = signal('provider');
  protected readonly copyState = signal('Copy');

  protected readonly products = [
    {
      name: 'Provider',
      icon: 'storage',
      description:
        'Native ADO.NET, pooling, commands, types, COPY, notifications, large objects, and replication.',
      status: 'V1 code-ready',
      statusKind: 'complete',
      tone: 'blue',
      route: '/provider',
      kicker: 'Foundation',
    },
    {
      name: 'EF Core',
      icon: 'data_object',
      description:
        'Queries, migrations, scaffolding, PostgreSQL-specific mappings, and SQL/PGQ support.',
      status: 'V1 code-ready',
      statusKind: 'complete',
      tone: 'cyan',
      route: '/ef-core',
      kicker: 'Application model',
    },
    {
      name: 'Streams',
      icon: 'stream',
      description:
        'CDC, snapshot bootstrap, durable checkpoints, relay fan-out, and replayable pipelines.',
      status: '72h evidence pending',
      statusKind: 'preview',
      tone: 'teal',
      route: '/real-time',
      kicker: 'Change data',
    },
    {
      name: 'Sync',
      icon: 'sync_alt',
      description:
        'Versioned synchronization for PostgreSQL, Redis, OpenSearch, and NATS destinations.',
      status: '24h evidence pending',
      statusKind: 'preview',
      tone: 'green',
      route: '/real-time',
      kicker: 'Data movement',
    },
    {
      name: 'Extensions',
      icon: 'extension',
      description:
        'First-party packages for PostGIS, pgvector, TimescaleDB, citext, hstore, ltree, and pg_trgm.',
      status: 'V1 code-ready',
      statusKind: 'complete',
      tone: 'amber',
      route: '/extensions',
      kicker: 'PostgreSQL native',
    },
    {
      name: 'Graph',
      icon: 'share',
      description:
        'PostgreSQL 19 SQL/PGQ today, with Continuous Graph for reactive graph workloads.',
      status: 'PG 19 GA pending',
      statusKind: 'graph',
      tone: 'purple',
      route: '/graph',
      kicker: 'Connected data',
    },
  ] as const;

  protected readonly paths = [
    {
      id: 'provider',
      icon: 'storage',
      label: 'Own the connection',
      title: 'Start with ADO.NET',
      body: 'Build the provider sample, register one long-lived data source, and exercise PostgreSQL types without another provider underneath.',
      route: '/provider',
      action: 'Explore Provider',
      proof: '11/11 live compatibility',
    },
    {
      id: 'ef-core',
      icon: 'data_object',
      label: 'Model an application',
      title: 'Start with EF Core',
      body: 'Reuse the provider data source inside DbContext and retain PostgreSQL queries, mappings, migrations, and scaffolding.',
      route: '/ef-core',
      action: 'Explore EF Core',
      proof: '1,987 official passes',
    },
    {
      id: 'real-time',
      icon: 'stream',
      label: 'React to committed data',
      title: 'Start with Streams',
      body: 'Turn pgoutput transactions into acknowledged deliveries, then add snapshots, relay, Sync, or Live as separate contracts.',
      route: '/real-time',
      action: 'Explore Real Time',
      proof: 'Exact endurance gated',
    },
    {
      id: 'extensions',
      icon: 'extension',
      label: 'Use a specialized workload',
      title: 'Start with an extension',
      body: 'Register PostGIS, pgvector, TimescaleDB, citext, hstore, ltree, or pg_trgm before building the data source.',
      route: '/extensions',
      action: 'Browse Extensions',
      proof: '7 package families',
    },
    {
      id: 'graph',
      icon: 'share',
      label: 'Connect the data',
      title: 'Start with Graph',
      body: 'Use capability-guarded PostgreSQL 19 SQL/PGQ or a checkpointed Continuous Graph projection.',
      route: '/graph',
      action: 'Explore Graph',
      proof: 'PG 19 guarded',
    },
  ] as const;

  protected readonly activePath = computed(
    () => this.paths.find((path) => path.id === this.selectedPath()) ?? this.paths[0],
  );

  protected readonly examples: readonly CodeExample[] = [
    {
      id: 'ado',
      label: 'ADO.NET',
      file: 'Program.cs',
      code: `await using var dataSource =
    new BlueTuskDataSourceBuilder(connectionString).Build();

await using var command = dataSource.CreateCommand(
    "SELECT $1::int4 + $2::int4");

command.Parameters.Add(new BlueTuskParameter<int>(20));
command.Parameters.Add(new BlueTuskParameter<int>(22));

var answer = await command.ExecuteScalarAsync<int>();`,
    },
    {
      id: 'ef',
      label: 'EF Core',
      file: 'AppDbContext.cs',
      code: `await using var dataSource =
    new BlueTuskDataSourceBuilder(connectionString).Build();

var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseBlueTusk(dataSource)
    .Options;

await using var context = new AppDbContext(options);`,
    },
    {
      id: 'copy',
      label: 'COPY',
      file: 'Import.cs',
      code: `await using var importer = await connection
    .BeginBinaryImportAsync(
        "COPY readings FROM STDIN WITH (FORMAT BINARY)",
        cancellationToken);

await importer.StartRowAsync(cancellationToken);
await importer.WriteAsync(42, cancellationToken);
await importer.WriteAsync("BlueTusk", cancellationToken);

await importer.CompleteAsync(cancellationToken);`,
    },
    {
      id: 'replication',
      label: 'Replication',
      file: 'Replication.cs',
      code: `await using var replication =
    await BlueTuskLogicalReplicationConnection.OpenAsync(
        dataSource.CreateDedicatedSessionOptions());

var stream = replication.StartReplicationAsync(
    slotName, publicationName);

await foreach (var envelope in stream.DecodePgOutputAsync())
{
    Console.WriteLine(envelope.Message.Code);
}`,
    },
    {
      id: 'streams',
      label: 'Streams',
      file: 'StreamWorker.cs',
      code: `IChangeStream changes = new PgOutputChangeStream(
    replication.StartReplicationAsync(slot, publication)
        .DecodePgOutputAsync(),
    sourceIdentity,
    transactionAssemblyOptions);

await foreach (var delivery in changes.ReadTransactionsAsync())
{
    await ApplyAsync(delivery.Transaction);
    await delivery.AcknowledgeAsync();
}`,
    },
    {
      id: 'sync',
      label: 'Sync',
      file: 'SyncPipeline.cs',
      code: `services.AddBlueTuskSync()
    .AddHostedPipeline<CatalogTransform, RedisDestination>(
        new SyncPipelineOptions
        {
            PipelineId = "catalog"
        },
        sourceIdentity,
        services => CreateSnapshotSource(services));`,
    },
  ];

  protected readonly activeExample = computed(
    () =>
      this.examples.find((example) => example.id === this.selectedExample()) ?? this.examples[0],
  );

  protected readonly evidence = [
    {
      value: '3,289',
      label: 'PostgreSQL 19 passes',
      detail: 'Zero failures across the complete 45-assembly solution matrix.',
      icon: 'fact_check',
    },
    {
      value: '12,975',
      label: 'Budgeted API signatures',
      detail: 'Exact public surface across all six product families.',
      icon: 'account_tree',
    },
    {
      value: '37',
      label: 'Allocation budgets',
      detail: 'Machine-checked command, COPY, replication, EF, Live, Sync, and graph budgets.',
      icon: 'verified',
    },
    {
      value: '72h / 24h',
      label: 'Evidence still pending',
      detail:
        'Exact runs plus 14 in-window disturbance recoveries and 28 hashed observations remain open.',
      icon: 'monitor_heart',
    },
  ] as const;

  protected readonly capabilityGroups = [
    {
      eyebrow: 'Types',
      title: 'PostgreSQL’s type system, without flattening it',
      items: [
        'Arrays',
        'Ranges',
        'Multiranges',
        'Composites',
        'Enums',
        'JSONB',
        'Network',
        'Full text',
      ],
      tone: 'blue',
    },
    {
      eyebrow: 'Database design',
      title: 'Schema features that stay PostgreSQL-native',
      items: [
        'Partitioning',
        'Row-level security',
        'Publications',
        'Subscriptions',
        'Scaffolding',
        'Migrations',
      ],
      tone: 'cyan',
    },
    {
      eyebrow: 'Specialized workloads',
      title: 'One provider, deeper capabilities',
      items: [
        'PostGIS',
        'pgvector',
        'TimescaleDB',
        'SQL/PGQ',
        'Logical replication',
        'Pipeline mode',
      ],
      tone: 'purple',
    },
  ] as const;

  protected readonly roadmap = [
    { name: 'Provider', status: 'V1 code and hardening complete', kind: 'complete' },
    { name: 'Streams', status: 'Exact 72-hour + 7 disturbances pending', kind: 'pending' },
    { name: 'Sync', status: 'Exact 24-hour + 7 disturbances pending', kind: 'pending' },
    { name: 'Live', status: 'V1 code and package gates complete', kind: 'complete' },
    { name: 'Control Plane', status: 'Dependency release chain pending', kind: 'pending' },
    { name: 'Continuous Graph', status: 'Preview candidate code-ready', kind: 'complete' },
  ] as const;

  protected selectExample(value: string): void {
    this.selectedExample.set(value);
    this.copyState.set('Copy');
  }

  protected async copyCode(): Promise<void> {
    await navigator.clipboard.writeText(this.activeExample().code);
    this.copyState.set('Copied');
    window.setTimeout(() => this.copyState.set('Copy'), 1600);
  }
}
