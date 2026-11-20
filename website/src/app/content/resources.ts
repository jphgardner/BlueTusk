export interface ResourceSection {
  readonly title: string;
  readonly description: string;
  readonly links: readonly { label: string; href: string }[];
}
export interface Resource {
  readonly path: string;
  readonly title: string;
  readonly description: string;
  readonly action: { label: string; href: string };
  readonly sections: readonly ResourceSection[];
}
const guide = (label: string, slug: string) => ({ label, href: '/documentation/' + slug });
const page = (label: string, href: string) => ({ label, href });

export const RESOURCES: readonly Resource[] = [
  {
    path: 'learn',
    title: 'Learn by building',
    description:
      'Follow a practical path from your first PostgreSQL query to application features that survive restarts and keep clients current.',
    action: guide('Run your first query', 'getting-started/quickstart'),
    sections: [
      {
        title: 'Set up data access',
        description:
          'Install a pinned package set, configure a shared data source, and choose direct commands or EF Core.',
        links: [
          guide('Install BlueTusk', 'getting-started/install'),
          guide('Configure dependency injection', 'provider/dependency-injection'),
          guide('Configure a DbContext', 'ef-core/overview'),
        ],
      },
      {
        title: 'Build a real-time feature',
        description:
          'Start with a snapshot and committed transactions. Forward the result to a destination or an authorized browser.',
        links: [
          guide('Run the Streams sample', 'real-time/streams-sample'),
          guide('Configure Sync', 'real-time/sync'),
          guide('Add Live updates', 'real-time/live'),
        ],
      },
      {
        title: 'Work with connected data',
        description:
          'Map existing tables and use a query or traversal with explicit limits. Watch the same results after a commit.',
        links: [
          guide('Map a property graph', 'graph/property-graphs'),
          guide('Run a traversal', 'graph/traversals'),
          page('Explore service topology', '/use-cases/service-topology'),
        ],
      },
      {
        title: 'Study complete applications',
        description:
          'See how application models, workers, policies, and browser connections fit together.',
        links: [
          guide('Runnable examples', 'getting-started/examples'),
          page('Application use cases', '/use-cases'),
          page('Prepare a deployment', '/production'),
        ],
      },
    ],
  },
  {
    path: 'compatibility',
    title: 'Choose a compatible configuration',
    description:
      'Match your BlueTusk 1.2 packages to the .NET runtime, EF Core version, and PostgreSQL capabilities you plan to deploy.',
    action: page('Browse 1.2 packages', '/packages'),
    sections: [
      {
        title: '.NET and EF Core',
        description:
          'The runtime libraries target .NET 5–11. Aspire integrations require .NET 8 or later. Match the EF Core runtime and tools to the provider’s dependency for your application target.',
        links: [
          guide('Check the .NET and EF Core matrix', 'getting-started/runtime-compatibility'),
          guide('ADO.NET contracts', 'provider/ado-net-compatibility'),
          guide('EF Core configuration', 'ef-core/overview'),
        ],
      },
      {
        title: 'PostgreSQL 15–18',
        description:
          'Use portable BlueTusk Graphs over ordinary mapped tables. Logical replication requires a configured publication and slot, and native SQL/PGQ requires a server that provides it.',
        links: [
          guide('Portable and native graphs', 'graph/graph-reference'),
          guide('Configure replication', 'provider/replication-reference'),
          guide('Server extension versions', 'extensions/reference'),
        ],
      },
      {
        title: 'Browsers and transports',
        description:
          'Select the Live client for your framework and the configured transport on your server. Scope Edge caches and replay to the authenticated user or tenant.',
        links: [
          guide('Live clients and transports', 'real-time/live-reference'),
          guide('Edge synchronization', 'services/edge'),
          page('Browser packages', '/packages?family=Live'),
        ],
      },
      {
        title: 'Upgrades and stored data',
        description:
          'Inspect migration SQL and verify that workers and clients can read the formats persisted by the target deployment.',
        links: [
          guide('Upgrade a deployment', 'operations/upgrade-guide'),
          guide('Live replay formats', 'real-time/live-format-compatibility'),
          guide('Jobs durable format', 'services/jobs-durable-format'),
        ],
      },
    ],
  },
  {
    path: 'production',
    title: 'Run BlueTusk in production',
    description:
      'Prepare database roles, workload budgets, observable services, and recovery paths for your application.',
    action: guide('Open the deployment checklist', 'operations/production-checklist'),
    sections: [
      {
        title: 'Provision and secure',
        description:
          'Select separate runtime, migration, and replication identities. Use TLS with certificate verification and enforce tenant access on each table and result.',
        links: [
          guide('Application security', 'operations/security'),
          guide('Database authentication', 'provider/authentication'),
          guide('Deployment topologies', 'operations/deployment'),
        ],
      },
      {
        title: 'Size and observe',
        description:
          'Bound pooled connections, command time, retained WAL, and subscriber work. Alert on backpressure, oldest progress, and inability to replay.',
        links: [
          guide('Tune the workload', 'operations/performance'),
          guide('Observe a deployment', 'operations/observability'),
          guide('Check service health', 'operations/application-platform-health'),
        ],
      },
      {
        title: 'Restore and recover',
        description:
          'Rehearse database restoration and recovery of checkpoints and live subscriptions. Verify a fresh authorized result after reconnect or failover.',
        links: [
          guide('Recover a real-time pipeline', 'real-time/operations'),
          guide('Recover background jobs', 'services/jobs-failover'),
          guide('Troubleshoot an application', 'operations/troubleshooting'),
        ],
      },
    ],
  },
  {
    path: 'whats-new',
    title: 'What’s new in BlueTusk 1.2',
    description:
      'Explore the 1.2 tools for PostgreSQL access, graph traversal, live applications, and durable services.',
    action: page('Choose 1.2 packages', '/packages'),
    sections: [
      {
        title: 'Graphs on existing tables',
        description:
          'Compose typed EF patterns, run bounded neighbors and shortest-path queries, and watch reachable results under subscriber authorization. Bounded traversals use the BlueTusk server engine by default when its compatible extension and registration are available.',
        links: [
          page('Explore Graphs', '/graph'),
          guide('Install the optional engine', 'graph/engine'),
          page('Try the topology explorer', '/use-cases/service-topology'),
        ],
      },
      {
        title: 'More ways to deliver changes',
        description:
          'Sync writes committed work to Kafka, S3, or signed webhooks alongside the other adapters. Use framework-specific Live packages for Angular, React, Vue, and Svelte.',
        links: [
          page('Explore Sync', '/real-time/sync'),
          page('Explore Live', '/real-time/live'),
          page('Find Live browser clients', '/packages?family=Live'),
        ],
      },
      {
        title: 'Build and operate the application',
        description:
          'Use the .NET production starter, inspect a database with doctor, and manage registered workloads through Control Plane.',
        links: [
          guide('Use the CLI tools', 'operations/tools'),
          guide('Browse the dashboard', 'real-time/control-plane'),
          page('Find the production starter', '/packages?family=Provider'),
        ],
      },
      {
        title: 'Durable data and work',
        description:
          'Use PostgreSQL-backed documents, events, projections, jobs, workflows, search, and offline synchronization as your application grows.',
        links: [
          page('Explore data services', '/data-services'),
          page('See the offline workflow', '/use-cases/offline-apps'),
          guide('Plan your upgrade', 'operations/upgrade-guide'),
        ],
      },
    ],
  },
];
