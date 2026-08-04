import { Type } from '@angular/core';
import { routes } from './app.routes';

describe('application routes', () => {
  const flagshipPaths = [
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
  ];

  it('exposes every flagship route as a lazy standalone component', async () => {
    for (const path of flagshipPaths) {
      const route = routes.find((candidate) => candidate.path === path);
      expect(route, `missing route: ${path || '/'}`).toBeTruthy();
      expect(route?.loadComponent, `route is not lazy: ${path || '/'}`).toBeTypeOf('function');
      const component = await (route?.loadComponent as () => Promise<Type<unknown>>)();
      expect(component).toBeTruthy();
    }
  });

  it('exposes nested documentation guides before the wildcard route', () => {
    const guideIndex = routes.findIndex((route) => route.path === 'documentation/:category/:slug');
    const wildcardIndex = routes.findIndex((route) => route.path === '**');
    expect(guideIndex).toBeGreaterThan(-1);
    expect(guideIndex).toBeLessThan(wildcardIndex);
  });
});
