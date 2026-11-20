import { routes } from './app.routes';
import { PRODUCTS } from './content/catalog';

describe('application routes', () => {
  it('lazily loads each published product, guide and community page', () => {
    const paths = [
      '',
      'platform',
      ...PRODUCTS.map(({ path }) => path),
      'documentation',
      'documentation/:category/:slug',
      'community',
    ];
    for (const path of paths) {
      const route = routes.find((item) => item.path === path);
      expect(route?.loadComponent, path).toBeTypeOf('function');
    }
    expect(routes.some((item) => item.path === 'evidence')).toBe(false);
    expect(routes.at(-1)?.path).toBe('**');
  });
});
