import type { GuideIndexEntry, SearchRecord } from './models';

export const DOC_TOPICS = [
  {
    id: 'getting-started',
    label: 'Getting started',
    icon: 'rocket_launch',
    description: 'Install BlueTusk and run your first PostgreSQL query.',
  },
  {
    id: 'provider',
    label: 'ADO.NET',
    icon: 'storage',
    description: 'Manage connections, execute SQL, and use PostgreSQL types.',
  },
  {
    id: 'ef-core',
    label: 'EF Core',
    icon: 'data_object',
    description: 'Set up a DbContext, write LINQ queries, and manage migrations.',
  },
  {
    id: 'real-time',
    label: 'Real-time data',
    icon: 'sensors',
    description: 'Stream commits, synchronize destinations, and deliver live results.',
  },
  {
    id: 'graph',
    label: 'Graphs',
    icon: 'hub',
    description: 'Model relationships, traverse edges, and watch for changes.',
  },
  {
    id: 'extensions',
    label: 'Extensions',
    icon: 'extension',
    description:
      'Use vector search, geospatial types, time series, and other PostgreSQL extensions.',
  },
  {
    id: 'services',
    label: 'Data services',
    icon: 'layers',
    description: 'Build with events, documents, jobs, workflows, search, and more.',
  },
  {
    id: 'operations',
    label: 'Operations',
    icon: 'settings',
    description: 'Deploy, secure, monitor, and recover BlueTusk applications.',
  },
] as const;

export function guideRoute(guide: Pick<GuideIndexEntry, 'category' | 'slug'>): string {
  return '/documentation/' + guide.category + '/' + guide.slug;
}

export function searchGuides(records: readonly SearchRecord[], query: string): SearchRecord[] {
  const terms = query.trim().toLowerCase().split(/\s+/).filter(Boolean);
  if (!terms.length) return [];

  return records
    .map((item) => {
      const title = item.title.toLowerCase();
      const body = [item.group, item.description, item.keywords].join(' ').toLowerCase();
      const points = terms.map((term) => (title.includes(term) ? 12 : body.includes(term) ? 3 : 0));
      const score = points.every(Boolean)
        ? points.reduce<number>((total, point) => total + point, 0)
        : 0;
      return { item, score };
    })
    .filter(({ score }) => score > 0)
    .sort((left, right) => right.score - left.score)
    .map(({ item }) => item);
}
