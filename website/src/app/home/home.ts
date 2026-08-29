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
      description: 'Connect .NET applications directly to PostgreSQL with familiar ADO.NET APIs.',
      status: '1.1 RC public',
      statusKind: 'complete',
      tone: 'blue',
      route: '/provider',
      kicker: 'Foundation',
    },
    {
      name: 'EF Core',
      icon: 'data_object',
      description:
        'Use LINQ, migrations, and tooling without giving up PostgreSQL-specific features.',
      status: '1.1 RC public',
      statusKind: 'complete',
      tone: 'cyan',
      route: '/ef-core',
      kicker: 'Application model',
    },
    {
      name: 'Streams',
      icon: 'stream',
      description: 'Turn committed database changes into reliable, recoverable event streams.',
      status: 'RC public · stable test pending',
      statusKind: 'complete',
      tone: 'teal',
      route: '/real-time',
      kicker: 'Change data',
    },
    {
      name: 'Sync',
      icon: 'sync_alt',
      description: 'Keep PostgreSQL, Redis, OpenSearch, and NATS destinations up to date.',
      status: 'RC public · stable test pending',
      statusKind: 'complete',
      tone: 'green',
      route: '/real-time',
      kicker: 'Data movement',
    },
    {
      name: 'Extensions',
      icon: 'extension',
      description: 'Add PostGIS, pgvector, TimescaleDB, and other features only when needed.',
      status: '1.1 RC public',
      statusKind: 'complete',
      tone: 'amber',
      route: '/extensions',
      kicker: 'PostgreSQL native',
    },
    {
      name: 'Graph',
      icon: 'share',
      description: 'Query connected data and keep graph results updated as PostgreSQL changes.',
      status: 'RC public · waits for PG 19',
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
      body: 'Connect a .NET application, run a parameterized query, and use PostgreSQL types through familiar ADO.NET APIs.',
      route: '/provider',
      action: 'Explore Provider',
      proof: '11/11 live checks passed',
    },
    {
      id: 'ef-core',
      icon: 'data_object',
      label: 'Model an application',
      title: 'Start with EF Core',
      body: 'Use a DbContext, LINQ, and migrations while keeping access to PostgreSQL-specific features.',
      route: '/ef-core',
      action: 'Explore EF Core',
      proof: '1,987 official passes',
    },
    {
      id: 'real-time',
      icon: 'stream',
      label: 'Build real-time features',
      title: 'Start with Streams',
      body: 'Capture committed database changes, then sync other systems or update connected users.',
      route: '/real-time',
      action: 'Explore Real Time',
      proof: 'Long-running test pending',
    },
    {
      id: 'extensions',
      icon: 'extension',
      label: 'Add database features',
      title: 'Start with an extension',
      body: 'Install PostGIS, pgvector, TimescaleDB, or another supported extension only when your app needs it.',
      route: '/extensions',
      action: 'Browse Extensions',
      proof: '7 V1 + 1 preview',
    },
    {
      id: 'graph',
      icon: 'share',
      label: 'Connect the data',
      title: 'Start with Graph',
      body: 'Query relationships inside PostgreSQL or keep graph results updated as data changes.',
      route: '/graph',
      action: 'Explore Graph',
      proof: 'Waits for PostgreSQL 19 GA',
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
      label: 'PostgreSQL 19 tests passed',
      detail: 'Zero failures across all 45 tested projects.',
      icon: 'fact_check',
    },
    {
      value: '13,056',
      label: 'Public APIs checked',
      detail: '13,056 API signatures checked across all six product families.',
      icon: 'account_tree',
    },
    {
      value: '46',
      label: 'Memory limits checked',
      detail:
        'Automated memory checks cover commands, COPY, replication, EF, Live, Sync, and graph.',
      icon: 'verified',
    },
    {
      value: '65 / 65',
      label: 'Public RC packages',
      detail:
        'Every NuGet and npm artifact is public at 1.1.0-rc.1 and passed clean consumer verification.',
      icon: 'inventory_2',
    },
  ] as const;

  protected readonly capabilityGroups = [
    {
      eyebrow: 'Types',
      title: 'Use PostgreSQL types directly',
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
      title: 'Keep your PostgreSQL database design',
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
      title: 'Add advanced features when needed',
      items: [
        'PostGIS',
        'pgvector',
        'TimescaleDB',
        'pg_durable',
        'SQL/PGQ',
        'Logical replication',
        'Pipeline mode',
      ],
      tone: 'purple',
    },
  ] as const;

  protected readonly roadmap = [
    { name: 'Provider', status: '1.1.0-rc.1 public', kind: 'complete' },
    { name: 'Streams', status: 'RC public · 72-hour stable test pending', kind: 'complete' },
    { name: 'Sync', status: 'RC public · 24-hour stable test pending', kind: 'complete' },
    { name: 'Live', status: '1.1.0-rc.1 public', kind: 'complete' },
    { name: 'Control Plane', status: '1.1.0-rc.1 public', kind: 'complete' },
    {
      name: 'Continuous Graph',
      status: 'RC public · waits for PostgreSQL 19 GA',
      kind: 'complete',
    },
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
