import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { GuidePage } from './guide.page';

describe('GuidePage', () => {
  beforeEach(() => {
    vi.spyOn(window, 'scrollTo').mockImplementation(() => undefined);
    Object.defineProperty(HTMLElement.prototype, 'scrollIntoView', {
      configurable: true,
      value: vi.fn(),
    });
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          {
            path: 'documentation/:category/:slug',
            component: GuidePage,
          },
        ]),
      ],
    });
  });

  it('keeps on-page links on the current guide route', async () => {
    const harness = await RouterTestingHarness.create('/documentation/ef-core/overview');
    harness.fixture.detectChanges();
    await harness.fixture.whenStable();

    const links = Array.from(
      harness.routeNativeElement?.querySelectorAll<HTMLAnchorElement>('.guide-toc nav a') ?? [],
    );
    const link = links.find((candidate) => candidate.textContent?.trim() === 'Configure a context');

    expect(link).toBeTruthy();
    expect(link?.getAttribute('href')).toBe('/documentation/ef-core/overview#configure-a-context');

    link?.click();
    await harness.fixture.whenStable();

    expect(TestBed.inject(Router).url).toBe('/documentation/ef-core/overview#configure-a-context');
  });

  it('renders collapsible section and page indexes for small screens', async () => {
    const harness = await RouterTestingHarness.create('/documentation/ef-core/overview');
    harness.fixture.detectChanges();
    await harness.fixture.whenStable();

    const sectionIndex = harness.routeNativeElement?.querySelector('.guide-mobile-index');
    const pageIndex = harness.routeNativeElement?.querySelector('.guide-mobile-toc');

    expect(sectionIndex?.textContent).toContain('IN THIS SECTION');
    expect(sectionIndex?.textContent).toContain('Entity Framework Core');
    expect(pageIndex?.textContent).toContain('ON THIS PAGE');
    expect(pageIndex?.textContent).toContain('Configure a context');
  });

  it('publishes guide-specific crawler metadata', async () => {
    const harness = await RouterTestingHarness.create('/documentation/getting-started/quickstart');
    harness.fixture.detectChanges();
    await harness.fixture.whenStable();

    expect(document.title).toBe('Quickstart: run the first query — BlueTusk');
    expect(document.head.querySelector<HTMLLinkElement>('link[rel="canonical"]')?.href).toBe(
      'https://bluetusk.io/documentation/getting-started/quickstart',
    );
    expect(document.head.querySelector<HTMLMetaElement>('meta[property="og:url"]')?.content).toBe(
      'https://bluetusk.io/documentation/getting-started/quickstart',
    );
  });
});
