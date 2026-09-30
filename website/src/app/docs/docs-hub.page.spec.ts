import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { DocsHubPage } from './docs-hub.page';

describe('DocsHubPage', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter([{ path: 'documentation', component: DocsHubPage }])],
    });
  });

  it('starts with an ordered onboarding path and goal-based journeys', async () => {
    const harness = await RouterTestingHarness.create('/documentation');
    harness.fixture.detectChanges();
    await harness.fixture.whenStable();

    const page = harness.routeNativeElement;
    expect(page?.querySelectorAll('.docs-path-grid > li')).toHaveLength(4);
    expect(page?.querySelectorAll('.journey-card')).toHaveLength(6);
    expect(page?.textContent).toContain('What are you trying to build?');
    expect(page?.textContent).toContain('Take a service to production');
  });

  it('separates practical guides from engineering reference records', async () => {
    const harness = await RouterTestingHarness.create('/documentation');
    harness.fixture.detectChanges();
    await harness.fixture.whenStable();

    const reference = harness.routeNativeElement?.querySelector('.reference-library');
    expect(reference).toBeTruthy();
    expect(reference?.textContent).toContain('not required for the normal learning path');
    expect(
      Array.from(
        harness.routeNativeElement?.querySelectorAll('#data-access .docs-category-group > h3') ??
          [],
      ).map((heading) => heading.textContent?.trim()),
    ).toEqual(['Provider', 'EF Core', 'Extensions']);
  });

  it('searches engineering reference as well as the practical guide index', async () => {
    const harness = await RouterTestingHarness.create('/documentation');
    harness.fixture.detectChanges();
    await harness.fixture.whenStable();

    const input = harness.routeNativeElement?.querySelector<HTMLInputElement>('.docs-search input');
    expect(input).toBeTruthy();
    if (!input) return;

    input.value = 'release readiness';
    input.dispatchEvent(new Event('input'));
    harness.fixture.detectChanges();
    await harness.fixture.whenStable();

    expect(harness.routeNativeElement?.querySelector('.result-summary')?.textContent).toContain(
      'results for “release readiness”',
    );
    expect(harness.routeNativeElement?.textContent).toContain('Engineering reference');
  });
});
