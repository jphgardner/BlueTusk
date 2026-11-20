interface ProductDetails {
  readonly title: string;
  readonly summary: string;
  readonly links: readonly { title: string; detail: string; route: string; icon: string }[];
}

export const PRODUCT_DETAILS: Readonly<Record<string, ProductDetails>> = {
  provider: {
    title: 'Choose the right data path',
    summary:
      'Keep a shared data source, then choose a command shape that matches your query and session requirements.',
    links: [
      {
        title: 'Borrow a session',
        detail: 'Check pool capacity, reset behavior, and routing to primary or replica hosts.',
        route: '/documentation/provider/pooling',
        icon: 'storage',
      },
      {
        title: 'Choose a result reader',
        detail:
          'Stream large values and rows using sequential access when buffering costs too much.',
        route: '/documentation/provider/sequential-readers',
        icon: 'view_stream',
      },
      {
        title: 'Load data with COPY',
        detail: 'Import or export rows without sending an INSERT statement for every row.',
        route: '/documentation/provider/copy',
        icon: 'file_upload',
      },
    ],
  },
  'ef-core': {
    title: 'Model your database deliberately',
    summary:
      'Keep model configuration and migrations aligned. Review generated SQL and use a deployment identity to apply changes.',
    links: [
      {
        title: 'Choose PostgreSQL mappings',
        detail: 'Map arrays, JSON, ranges, composites, and extension-defined types.',
        route: '/documentation/ef-core/ef-core-reference',
        icon: 'data_object',
      },
      {
        title: 'Scaffold an existing schema',
        detail: 'Generate a context from explicitly selected schemas with the .NET tool.',
        route: '/documentation/operations/tools',
        icon: 'terminal',
      },
      {
        title: 'Query connected data',
        detail:
          'Reuse EF metadata for vertex and edge tables and compose bounded queries with LINQ.',
        route: '/graph',
        icon: 'hub',
      },
    ],
  },
  'real-time': {
    title: 'Choose how to use committed changes',
    summary:
      'Start with the delivery unit your application needs. Capture a transaction, persist a destination, or push an authorized query result.',
    links: [
      {
        title: 'Streams',
        detail: 'Read changes in committed transaction order with a durable checkpoint.',
        route: '/real-time/streams',
        icon: 'sensors',
      },
      {
        title: 'Sync',
        detail: 'Apply change transactions to a database, index, broker, or webhook destination.',
        route: '/real-time/sync',
        icon: 'sync',
      },
      {
        title: 'Live',
        detail: 'Update an authorized view and resume a subscriber from a retained cursor.',
        route: '/real-time/live',
        icon: 'bolt',
      },
    ],
  },
  graph: {
    title: 'Choose how to query a graph',
    summary:
      'Use the same mapped tables for composable patterns, bounded traversal, or results that refresh after commits.',
    links: [
      {
        title: 'Typed patterns',
        detail: 'Run parameterized joins over vertices and edges on PostgreSQL 15–18.',
        route: '/documentation/graph/property-graphs',
        icon: 'hub',
      },
      {
        title: 'Server-side traversal',
        detail:
          'Install and register the optional engine to run supported bounded searches inside PostgreSQL by default.',
        route: '/documentation/graph/engine',
        icon: 'alt_route',
      },
      {
        title: 'Continuous Graph',
        detail:
          'Keep an authorized result current and distinguish affected-key requery from full repair.',
        route: '/documentation/real-time/continuous-graph',
        icon: 'monitoring',
      },
    ],
  },
  extensions: {
    title: 'Match server features to client types',
    summary:
      'Install the extension in PostgreSQL before enabling its BlueTusk codecs or EF mappings. Add only the integrations your app uses.',
    links: [
      {
        title: 'Vector data',
        detail: 'Map pgvector values and use distance operators with a suitable index.',
        route: '/documentation/extensions/reference#pgvector',
        icon: 'graphic_eq',
      },
      {
        title: 'Spatial data',
        detail: 'Handle PostGIS geometry and geography with typed parameters and EF translation.',
        route: '/documentation/extensions/reference#postgis',
        icon: 'public',
      },
      {
        title: 'Time series',
        detail: 'Access TimescaleDB helpers and migration operations.',
        route: '/documentation/extensions/reference#timescaledb',
        icon: 'timeline',
      },
    ],
  },
  'data-services': {
    title: 'Build on durable application state',
    summary:
      'Choose a service for a specific state or execution contract, then operate its tables and workers according to its guide.',
    links: [
      {
        title: 'Durable workflows',
        detail: 'Coordinate activities, signals, and timers that can resume after a restart.',
        route: '/documentation/services/workflows',
        icon: 'account_tree',
      },
      {
        title: 'Schema and SQL',
        detail: 'Detect catalog drift and validate typed queries against a reviewed schema.',
        route: '/documentation/services/schema',
        icon: 'schema',
      },
      {
        title: 'Offline synchronization',
        detail: 'Maintain an authorized cache and reconcile local changes when devices reconnect.',
        route: '/use-cases/offline-apps',
        icon: 'cloud_sync',
      },
    ],
  },
};
