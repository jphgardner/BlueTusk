import { GUIDES } from '../../generated/guides.generated';
import { GUIDE_SEARCH } from '../../generated/guide-search.generated';
import { loadGuide } from '../../generated/guide-loader.generated';
import { PRODUCTS } from './catalog';
import { DOC_TOPICS, guideRoute, searchGuides } from './documentation-navigation';

describe('public documentation', () => {
  it('includes every advertised product and task', () => {
    const routes = new Set(GUIDES.map(guideRoute));
    expect(routes.size).toBe(GUIDES.length);
    for (const topic of DOC_TOPICS) {
      expect(GUIDES.some((guide) => guide.category === topic.id)).toBe(true);
    }
    for (const product of PRODUCTS) {
      for (const guide of [product.start, ...product.tasks.map((task) => task.guide)]) {
        expect(routes.has('/documentation/' + guide), guide).toBe(true);
      }
    }
    expect(GUIDE_SEARCH.map(({ route }) => route)).toEqual([...routes]);
  });

  it('never loads the old release process or engineering evidence routes', async () => {
    for (const slug of [
      'release-readiness',
      'release-tracks',
      'v1-release-readiness',
      'release-process',
    ]) {
      expect(await loadGuide('operations', slug)).toBeUndefined();
    }
    expect(
      GUIDE_SEARCH.some(({ keywords }) => /release gates?\b|publication gates?\b/i.test(keywords)),
    ).toBe(false);
    expect(
      searchGuides(GUIDE_SEARCH, 'shortest path').some(({ route }) =>
        route.endsWith('/graph/traversals'),
      ),
    ).toBe(true);
  });

  it('resolves bookmarked public guides after a move', async () => {
    expect((await loadGuide('graph', 'sql-pgq'))?.slug).toBe('property-graphs');
    expect((await loadGuide('getting-started', 'provider-overview'))?.slug).toBe('overview');
  });

  it('creates safe, uniquely anchored content for every public guide', async () => {
    for (const guide of GUIDES) {
      const content = await loadGuide(guide.category, guide.slug);
      expect(content?.title).toBe(guide.title);
      expect(content?.blocks.length).toBeGreaterThan(0);
      const headings = content?.blocks.flatMap((block) =>
        block.kind === 'heading' ? [block.heading] : [],
      );
      expect(headings).toEqual(guide.headings);
      expect(new Set(headings?.map(({ id }) => id)).size).toBe(guide.headings.length);
      const html = content?.blocks
        .map((block) => (block.kind === 'html' ? block.html : ''))
        .join('');
      expect(html).not.toMatch(/<script\b|href="javascript:|release gates?\b/i);
    }
  });
});
