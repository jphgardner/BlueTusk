import { EVIDENCE, EXTENSION_CAPABILITIES, PRODUCT_STATUSES, SITE_SEARCH } from './catalog';
import { GUIDES } from '../../generated/guides.generated';
import { GUIDE_SEARCH } from '../../generated/guide-search.generated';
import { GUIDES as ARCHITECTURE_GUIDES } from '../../generated/guide-content/architecture.generated';
import { GUIDES as EF_CORE_GUIDES } from '../../generated/guide-content/ef-core.generated';
import { GUIDES as EXTENSION_GUIDES } from '../../generated/guide-content/extensions.generated';
import { GUIDES as GETTING_STARTED_GUIDES } from '../../generated/guide-content/getting-started.generated';
import { GUIDES as GRAPH_GUIDES } from '../../generated/guide-content/graph.generated';
import { GUIDES as OPERATIONS_GUIDES } from '../../generated/guide-content/operations.generated';
import { GUIDES as PROVIDER_GUIDES } from '../../generated/guide-content/provider.generated';
import { GUIDES as REAL_TIME_GUIDES } from '../../generated/guide-content/real-time.generated';
import {
  DOCUMENTATION_JOURNEYS,
  DOCUMENTATION_SECTIONS,
  documentationSectionFor,
} from './documentation-navigation';

const CONTENT_GUIDES = [
  ...GETTING_STARTED_GUIDES,
  ...PROVIDER_GUIDES,
  ...EF_CORE_GUIDES,
  ...REAL_TIME_GUIDES,
  ...EXTENSION_GUIDES,
  ...GRAPH_GUIDES,
  ...ARCHITECTURE_GUIDES,
  ...OPERATIONS_GUIDES,
];

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
    }
    expect(CONTENT_GUIDES).toHaveLength(GUIDES.length);
    for (const guide of CONTENT_GUIDES) {
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

  it('keeps practical product entry points concise and deep manuals in reference', () => {
    const practical = GUIDES.filter((guide) => guide.listed);
    expect(Math.max(...practical.map((guide) => guide.readMinutes))).toBeLessThanOrEqual(12);

    for (const sourcePath of [
      'docs/ef-core/reference.md',
      'docs/sync/reference.md',
      'docs/live/reference.md',
      'docs/control-plane/reference.md',
      'docs/continuous-graph/reference.md',
      'docs/extensions/reference.md',
      'docs/graph/reference.md',
    ]) {
      expect(GUIDES.find((guide) => guide.sourcePath === sourcePath)?.listed).toBe(false);
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

  it('keeps every guide in one clear documentation section', () => {
    const sectionIds = DOCUMENTATION_SECTIONS.map((section) => section.id);
    expect(new Set(sectionIds).size).toBe(sectionIds.length);

    for (const guide of GUIDES) {
      const section = documentationSectionFor(guide);
      expect(sectionIds).toContain(section.id);
      expect(section.id === 'reference').toBe(!guide.listed);
    }
  });

  it('keeps every guided journey short and linked to a generated guide', () => {
    const generatedRoutes = new Set(
      GUIDES.map((guide) => `/documentation/${guide.category}/${guide.slug}`),
    );

    expect(DOCUMENTATION_JOURNEYS.length).toBeGreaterThanOrEqual(6);
    for (const journey of DOCUMENTATION_JOURNEYS) {
      expect(journey.steps).toHaveLength(3);
      for (const step of journey.steps) expect(generatedRoutes.has(step.route)).toBe(true);
    }
  });
});
