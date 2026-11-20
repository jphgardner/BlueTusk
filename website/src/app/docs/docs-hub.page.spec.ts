import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { DOC_TOPICS } from '../content/documentation-navigation';
import { DocsHubPage } from './docs-hub.page';

describe('DocsHubPage', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter([{ path: 'documentation', component: DocsHubPage }])],
    });
  });

  it('provides a first-query link and a section for each topic', async () => {
    const harness = await RouterTestingHarness.create('/documentation');
    const page = harness.routeNativeElement;
    expect(page?.querySelector('a[href="/documentation/getting-started/quickstart"]')).toBeTruthy();
    expect([...page!.querySelectorAll('.docs-topic-section')].map((element) => element.id)).toEqual(
      DOC_TOPICS.map(({ id }) => id),
    );
  });

  it('finds the traversal guide for a multi-word query', async () => {
    const harness = await RouterTestingHarness.create('/documentation');
    const input =
      harness.routeNativeElement!.querySelector<HTMLInputElement>('input[type="search"]')!;
    input.value = 'shortest path';
    input.dispatchEvent(new Event('input'));
    harness.fixture.detectChanges();
    expect(
      harness.routeNativeElement?.querySelector(
        '[aria-label="Matching guides"] a[href="/documentation/graph/traversals"]',
      ),
    ).toBeTruthy();
  });
});
