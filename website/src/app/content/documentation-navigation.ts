import type { GuideManifestEntry } from './models';

export interface DocumentationSection {
  readonly id: 'start' | 'data-access' | 'real-time' | 'operations' | 'reference';
  readonly label: string;
  readonly title: string;
  readonly description: string;
  readonly icon: string;
}

export interface DocumentationJourneyStep {
  readonly label: string;
  readonly title: string;
  readonly route: string;
}

export interface DocumentationJourney {
  readonly id: string;
  readonly eyebrow: string;
  readonly title: string;
  readonly description: string;
  readonly icon: string;
  readonly steps: readonly DocumentationJourneyStep[];
}

export const DOCUMENTATION_SECTIONS: readonly DocumentationSection[] = [
  {
    id: 'start',
    label: 'Start here',
    title: 'Learn the essentials',
    description:
      'Choose packages, run your first query, and learn the few concepts used everywhere else.',
    icon: 'flag',
  },
  {
    id: 'data-access',
    label: 'Build with .NET',
    title: 'Connect applications to PostgreSQL',
    description:
      'Use ADO.NET, EF Core, PostgreSQL types, extensions, and high-throughput data paths.',
    icon: 'data_object',
  },
  {
    id: 'real-time',
    label: 'Build real-time',
    title: 'React to committed database changes',
    description:
      'Capture changes, synchronize systems, update clients, and maintain graph results.',
    icon: 'stream',
  },
  {
    id: 'operations',
    label: 'Run in production',
    title: 'Deploy and operate with confidence',
    description:
      'Secure, size, observe, troubleshoot, benchmark, upgrade, and recover a deployment.',
    icon: 'monitor_heart',
  },
  {
    id: 'reference',
    label: 'Engineering reference',
    title: 'Contracts, decisions, evidence, and release records',
    description: 'Deep technical material for maintainers, reviewers, and incident investigations.',
    icon: 'library_books',
  },
] as const;

export const DOCUMENTATION_JOURNEYS: readonly DocumentationJourney[] = [
  {
    id: 'connect-dotnet',
    eyebrow: 'APPLICATION DEVELOPMENT',
    title: 'Connect a .NET application',
    description:
      'Install the provider, create one shared data source, then choose ADO.NET or EF Core.',
    icon: 'terminal',
    steps: [
      {
        label: 'Start',
        title: 'Install packages',
        route: '/documentation/getting-started/install',
      },
      {
        label: 'Next',
        title: 'Run the first query',
        route: '/documentation/getting-started/quickstart',
      },
      {
        label: 'Choose',
        title: 'ADO.NET or EF Core',
        route: '/documentation/getting-started/provider-overview',
      },
    ],
  },
  {
    id: 'stream-changes',
    eyebrow: 'CHANGE DATA CAPTURE',
    title: 'Stream committed changes',
    description:
      'Start with the delivery model, then build a recoverable snapshot-and-stream pipeline.',
    icon: 'sync_alt',
    steps: [
      { label: 'Start', title: 'Real-time overview', route: '/documentation/real-time/platform' },
      { label: 'Build', title: 'Streams', route: '/documentation/real-time/streams' },
      {
        label: 'Harden',
        title: 'Snapshot and replay',
        route: '/documentation/real-time/snapshot-bootstrap',
      },
    ],
  },
  {
    id: 'sync-systems',
    eyebrow: 'DATA DELIVERY',
    title: 'Keep another system in sync',
    description:
      'Deliver whole source transactions to PostgreSQL, Redis, NATS, OpenSearch, Kafka, or S3.',
    icon: 'multiple_stop',
    steps: [
      { label: 'Start', title: 'Delivery guarantees', route: '/documentation/real-time/contracts' },
      { label: 'Build', title: 'Sync destinations', route: '/documentation/real-time/sync' },
      {
        label: 'Operate',
        title: 'Recovery and rebuilds',
        route: '/documentation/real-time/operations',
      },
    ],
  },
  {
    id: 'live-apps',
    eyebrow: 'USER EXPERIENCES',
    title: 'Push live updates to users',
    description:
      'Register an authorized query and deliver bounded updates to web and .NET clients.',
    icon: 'bolt',
    steps: [
      { label: 'Start', title: 'Live queries', route: '/documentation/real-time/live' },
      { label: 'Secure', title: 'Security model', route: '/documentation/operations/security' },
      {
        label: 'Operate',
        title: 'Observability',
        route: '/documentation/operations/observability',
      },
    ],
  },
  {
    id: 'graph-data',
    eyebrow: 'CONNECTED DATA',
    title: 'Query and maintain a graph',
    description: 'Model relationships with SQL/PGQ, then keep authorized graph results current.',
    icon: 'hub',
    steps: [
      { label: 'Start', title: 'SQL/PGQ graph queries', route: '/documentation/graph/sql-pgq' },
      {
        label: 'Build',
        title: 'Continuous Graph',
        route: '/documentation/real-time/continuous-graph',
      },
      {
        label: 'Operate',
        title: 'Fallbacks and metrics',
        route: '/documentation/real-time/operations',
      },
    ],
  },
  {
    id: 'production',
    eyebrow: 'PRODUCTION OPERATIONS',
    title: 'Take a service to production',
    description:
      'Work through deployment, security, capacity, telemetry, recovery, and rollback in order.',
    icon: 'rocket_launch',
    steps: [
      {
        label: 'Start',
        title: 'Production checklist',
        route: '/documentation/operations/production-checklist',
      },
      { label: 'Deploy', title: 'Deployment guide', route: '/documentation/operations/deployment' },
      {
        label: 'Run',
        title: 'Troubleshooting',
        route: '/documentation/operations/troubleshooting',
      },
    ],
  },
] as const;

export function guideRoute(guide: Pick<GuideManifestEntry, 'category' | 'slug'>): string {
  return `/documentation/${guide.category}/${guide.slug}`;
}

export function documentationSectionFor(
  guide: Pick<GuideManifestEntry, 'category' | 'listed'>,
): DocumentationSection {
  if (!guide.listed) return DOCUMENTATION_SECTIONS[4];
  if (guide.category === 'getting-started') return DOCUMENTATION_SECTIONS[0];
  if (['provider', 'ef-core', 'extensions'].includes(guide.category)) {
    return DOCUMENTATION_SECTIONS[1];
  }
  if (['real-time', 'graph'].includes(guide.category)) return DOCUMENTATION_SECTIONS[2];
  return DOCUMENTATION_SECTIONS[3];
}

const CATEGORY_ORDER: Readonly<Record<string, number>> = {
  'getting-started': 0,
  provider: 10,
  'ef-core': 20,
  extensions: 30,
  'real-time': 40,
  graph: 50,
  operations: 60,
};

export function compareDocumentationGuides(
  left: Pick<GuideManifestEntry, 'category' | 'order' | 'title'>,
  right: Pick<GuideManifestEntry, 'category' | 'order' | 'title'>,
): number {
  return (
    (CATEGORY_ORDER[left.category] ?? 999) - (CATEGORY_ORDER[right.category] ?? 999) ||
    left.order - right.order ||
    left.title.localeCompare(right.title)
  );
}
