import type { SearchRecord } from './models';
import { RESOURCES } from './resources';
import { REAL_TIME_RESOURCES } from './real-time-resources';
import { USE_CASES } from './use-cases';

export interface Product {
  readonly path: string;
  readonly name: string;
  readonly title: string;
  readonly description: string;
  readonly icon: string;
  readonly package?: string;
  readonly category: string;
  readonly start: string;
  readonly tasks: readonly { title: string; description: string; guide: string }[];
}

export const PRODUCTS: readonly Product[] = [
  {
    path: 'provider',
    name: 'ADO.NET',
    title: 'Connect .NET to PostgreSQL.',
    description:
      'Run parameterized SQL, manage transactions, stream large results, and bulk-load rows through a pooled data source.',
    icon: 'terminal',
    package: 'BlueTusk.Data',
    category: 'provider',
    start: 'getting-started/quickstart',
    tasks: [
      {
        title: 'Manage connections',
        description: 'Share a data source and configure connection lifetimes and pool limits.',
        guide: 'provider/pooling',
      },
      {
        title: 'Load data in bulk',
        description: 'Use COPY for imports and exports with PostgreSQL type mappings.',
        guide: 'provider/copy',
      },
      {
        title: 'Configure authentication',
        description: 'Set up server identity checks and credentials.',
        guide: 'provider/authentication',
      },
    ],
  },
  {
    path: 'ef-core',
    name: 'EF Core',
    title: 'Build with LINQ and PostgreSQL.',
    description:
      'Map entities to PostgreSQL tables, compose queries, scaffold existing schemas, and run migrations with the BlueTusk EF Core provider.',
    icon: 'data_object',
    package: 'BlueTusk.EntityFrameworkCore',
    category: 'ef-core',
    start: 'ef-core/overview',
    tasks: [
      {
        title: 'Configure a DbContext',
        description: 'Register the provider and share a data source with direct ADO.NET code.',
        guide: 'ef-core/overview',
      },
      {
        title: 'Map PostgreSQL types',
        description: 'Configure type mappings, query translations, and migrations.',
        guide: 'ef-core/ef-core-reference',
      },
      {
        title: 'Query relationships',
        description: 'Compose typed graph patterns over your existing tables.',
        guide: 'graph/property-graphs',
      },
    ],
  },
  {
    path: 'real-time',
    name: 'Real-time data',
    title: 'Build from committed changes.',
    description:
      'Capture PostgreSQL transactions with Streams, synchronize other systems with Sync, and deliver authorized query results to applications with Live.',
    icon: 'sensors',
    category: 'real-time',
    start: 'real-time/platform',
    tasks: [
      {
        title: 'Consume database changes',
        description: 'Bootstrap a snapshot and acknowledge whole transactions after processing.',
        guide: 'real-time/streams',
      },
      {
        title: 'Synchronize a destination',
        description: 'Choose an adapter and define the destination keys and replay behavior.',
        guide: 'real-time/sync',
      },
      {
        title: 'Subscribe to query results',
        description: 'Register a bounded query and deliver updates to authorized clients.',
        guide: 'real-time/live',
      },
    ],
  },
  {
    path: 'graph',
    name: 'Graphs',
    title: 'Follow relationships in PostgreSQL.',
    description:
      'Use your existing vertex and edge tables for typed patterns, bounded traversals, and live results that respect the subscriber’s access.',
    icon: 'hub',
    category: 'graph',
    start: 'graph/property-graphs',
    tasks: [
      {
        title: 'Define a property graph',
        description: 'Map edges and endpoints in EF Core and query them on PostgreSQL 15–18.',
        guide: 'graph/property-graphs',
      },
      {
        title: 'Run a bounded traversal',
        description:
          'Explore neighbors, reachable vertices, and shortest paths with explicit budgets.',
        guide: 'graph/traversals',
      },
      {
        title: 'Watch connected data',
        description: 'Maintain an authorized graph result after relevant commits.',
        guide: 'real-time/continuous-graph',
      },
    ],
  },
  {
    path: 'extensions',
    name: 'Extensions',
    title: 'Use PostgreSQL’s specialized types.',
    description:
      'Enable vector similarity, geospatial data, time series, tree paths, and more with packages for the extensions you use.',
    icon: 'extension',
    category: 'extensions',
    start: 'extensions/catalog',
    tasks: [
      {
        title: 'Choose a package',
        description:
          'Match the server extension, the client type mappings, and any EF Core adapter.',
        guide: 'extensions/catalog',
      },
      {
        title: 'Configure extension support',
        description: 'Find registration methods, CLR types, and migration APIs.',
        guide: 'extensions/reference',
      },
    ],
  },
  {
    path: 'data-services',
    name: 'Data services',
    title: 'Build on PostgreSQL.',
    description:
      'Store documents and events, run durable background work, query projections, add search, and connect intermittently online applications.',
    icon: 'layers',
    category: 'services',
    start: 'services/overview',
    tasks: [
      {
        title: 'Store documents',
        description: 'Work with JSONB metadata and attached content.',
        guide: 'services/documents',
      },
      {
        title: 'Build from events',
        description: 'Append events, project state, and track projection progress.',
        guide: 'services/events',
      },
      {
        title: 'Run durable work',
        description: 'Enqueue jobs and coordinate multi-step workflows.',
        guide: 'services/jobs',
      },
      {
        title: 'Index and query data',
        description: 'Choose a search backend and configure the indexing pipeline.',
        guide: 'services/search',
      },
    ],
  },
];

const productSearch: readonly SearchRecord[] = PRODUCTS.map((product) => ({
  title: product.name,
  description: product.description,
  route: '/' + product.path,
  group: 'Product',
  keywords: product.package ?? '',
}));

export const SITE_SEARCH: readonly SearchRecord[] = [
  ...productSearch,
  ...[...RESOURCES, ...REAL_TIME_RESOURCES].map((resource) => ({
    title: resource.title,
    description: resource.description,
    route: '/' + resource.path,
    group: 'Guide',
    keywords: resource.sections.map(({ title }) => title).join(' '),
  })),
  ...USE_CASES.map((item) => ({
    title: item.name,
    description: item.summary,
    route: '/use-cases/' + item.slug,
    group: 'Use cases',
    keywords: item.stages.map(({ title }) => title).join(' '),
  })),
  {
    title: 'BlueTusk packages',
    description: 'Find a 1.2 .NET library, tool, template, or browser client.',
    route: '/packages',
    group: 'Install',
    keywords: 'nuget npm packages dotnet install',
  },
  {
    title: 'BlueTusk architecture',
    description: 'Trace a query, commit, or graph refresh.',
    route: '/architecture',
    group: 'Platform',
    keywords: 'architecture data flow design',
  },
];
