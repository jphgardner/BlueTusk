import { PrerenderFallback, RenderMode, ServerRoute } from '@angular/ssr';
import { GUIDES } from '../generated/guides.generated';
import { routes } from './app.routes';

export const serverRoutes: ServerRoute[] = [
  {
    path: 'documentation/:category/:slug',
    renderMode: RenderMode.Prerender,
    fallback: PrerenderFallback.Client,
    getPrerenderParams: async () => GUIDES.map(({ category, slug }) => ({ category, slug })),
  },
  ...routes.flatMap(({ path }) =>
    path !== undefined && path !== '**' && !path.includes(':')
      ? [{ path, renderMode: RenderMode.Prerender } as const]
      : [],
  ),
  { path: '**', renderMode: RenderMode.Client },
];
