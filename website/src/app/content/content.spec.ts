import { EVIDENCE, EXTENSION_CAPABILITIES, PRODUCT_STATUSES, SITE_SEARCH } from './catalog';
import { GUIDES } from '../../generated/guides.generated';
import { GUIDE_SEARCH } from '../../generated/guide-search.generated';

describe('website content integrity', () => {
  it('keeps product maturity and pending gates explicit', () => {
    expect(PRODUCT_STATUSES.find((item) => item.id === 'provider')?.version).toBe('1.1.0-rc.1');
    expect(PRODUCT_STATUSES.find((item) => item.id === 'ef-core')?.version).toBe('1.1.0-rc.1');
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
        'website-field-vitals',
        'streams-endurance',
        'sync-endurance',
        'endurance-disturbances',
        'secret-scanner-triage',
        'operational-approvals',
      ]),
    );
    expect(EVIDENCE.find((item) => item.id === 'allocations')?.value).toBe('46');
    expect(EVIDENCE.find((item) => item.id === 'website-delivery')?.status).toBe('passed');
    expect(EVIDENCE.find((item) => item.id === 'canonical-package-set')?.status).toBe('passed');
    expect(EVIDENCE.find((item) => item.id === 'rc-publication')?.value).toBe('65 / 65');
    expect(EVIDENCE.find((item) => item.id === 'test-credential-boundary')?.value).toBe(
      '22 scoped',
    );
  });

  it('publishes seven V1 extension families and the isolated pg_durable preview', () => {
    expect(EXTENSION_CAPABILITIES.map((item) => item.feature)).toEqual([
      'citext',
      'pgvector',
      'hstore',
      'ltree',
      'pg_trgm',
      'pg_durable',
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

  it('keeps the task-oriented guide index separate from project records', () => {
    const listed = GUIDES.filter((guide) => guide.listed);
    expect(listed.length).toBeGreaterThan(35);
    expect(listed.length).toBeLessThan(60);
    expect(GUIDES.find((guide) => guide.sourcePath === 'docs/release-readiness.md')?.listed).toBe(
      false,
    );
    expect(GUIDES.find((guide) => guide.sourcePath === 'docs/ado-net/README.md')?.listed).toBe(
      true,
    );
    expect(GUIDE_SEARCH.map((guide) => guide.route).sort()).toEqual(
      listed.map((guide) => `/documentation/${guide.category}/${guide.slug}`).sort(),
    );
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
