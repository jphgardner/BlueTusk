import { PrerenderFallback, RenderMode, ServerRoute } from '@angular/ssr';
import { GUIDES } from '../generated/guides.generated';

const publicRoutes = [
  '',
  'platform',
  'provider',
  'ef-core',
  'real-time',
  'extensions',
  'graph',
  'evidence',
  'documentation',
  'community',
] as const;

export const serverRoutes: ServerRoute[] = [
  {
    path: 'documentation/:category/:slug',
    renderMode: RenderMode.Prerender,
    fallback: PrerenderFallback.Client,
    getPrerenderParams: async () =>
      GUIDES.map((guide) => ({ category: guide.category, slug: guide.slug })),
  },
  ...publicRoutes.map((path) => ({ path, renderMode: RenderMode.Prerender }) as const),
  { path: '**', renderMode: RenderMode.Client },
];
