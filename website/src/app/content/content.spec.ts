import { EVIDENCE, EXTENSION_CAPABILITIES, PRODUCT_STATUSES, SITE_SEARCH } from './catalog';
import { GUIDES } from '../../generated/guides.generated';

describe('website content integrity', () => {
  it('keeps product maturity and pending gates explicit', () => {
    expect(PRODUCT_STATUSES.find((item) => item.id === 'provider')?.version).toBe('0.3.0 preview');
    expect(PRODUCT_STATUSES.find((item) => item.id === 'ef-core')?.version).toBe('0.3.0 preview');
    expect(PRODUCT_STATUSES.find((item) => item.id === 'streams')?.limitations.join(' ')).toContain(
      '72-hour',
    );
    expect(PRODUCT_STATUSES.find((item) => item.id === 'sync')?.limitations.join(' ')).toContain(
      'exact candidate',
    );
  });

  it('records the documented specification and compatibility evidence', () => {
    expect(EVIDENCE.find((item) => item.id === 'pg-matrix')?.value).toBe('15–19');
    expect(EVIDENCE.find((item) => item.id === 'ef-suite')?.value).toBe('1,987 / 2,111');
    expect(EVIDENCE.filter((item) => item.status === 'pending').map((item) => item.id)).toEqual(
      expect.arrayContaining([
        'fuzzing',
        'canonical-package-set',
        'website-field-vitals',
        'streams-endurance',
        'sync-endurance',
        'endurance-disturbances',
        'secret-scanner-triage',
      ]),
    );
    expect(EVIDENCE.find((item) => item.id === 'allocations')?.value).toBe('37');
    expect(EVIDENCE.find((item) => item.id === 'website-delivery')?.status).toBe('passed');
    expect(EVIDENCE.find((item) => item.id === 'canonical-package-set')?.status).toBe('pending');
    expect(EVIDENCE.find((item) => item.id === 'test-credential-boundary')?.value).toBe(
      '22 scoped',
    );
  });

  it('publishes all seven first-party extension families', () => {
    expect(EXTENSION_CAPABILITIES.map((item) => item.feature)).toEqual([
      'citext',
      'pgvector',
      'hstore',
      'ltree',
      'pg_trgm',
      'PostGIS',
      'TimescaleDB',
    ]);
  });

  it('generates unique, source-linked documentation routes and headings', () => {
    expect(GUIDES.length).toBeGreaterThanOrEqual(90);
    const routes = GUIDES.map((guide) => `${guide.category}/${guide.slug}`);
    expect(new Set(routes).size).toBe(routes.length);
    for (const guide of GUIDES) {
      expect(guide.sourceUrl).toContain('github.com/jphgardner/BlueTusk/blob/main/');
      expect(guide.wordCount).toBeGreaterThan(20);
      expect(guide.readMinutes).toBeGreaterThan(0);
      expect(guide.searchText.length).toBeGreaterThan(0);
      expect(new Set(guide.headings.map((heading) => heading.id)).size).toBe(guide.headings.length);
      expect(
        guide.blocks
          .filter((block) => block.kind === 'html')
          .map((block) => block.html)
          .join('')
          .toLowerCase(),
      ).not.toContain('<script');
    }
  });

  it('keeps every flagship page in global search', () => {
    expect(SITE_SEARCH.map((item) => item.route)).toEqual(
      expect.arrayContaining([
        '/platform',
        '/provider',
        '/ef-core',
        '/real-time',
        '/extensions',
        '/graph',
        '/evidence',
        '/documentation',
        '/community',
      ]),
    );
  });
});
