import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { loadGuide } from '../../generated/guide-loader.generated';
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
            resolve: {
              guide: (route: ActivatedRouteSnapshot) =>
                loadGuide(route.paramMap.get('category') ?? '', route.paramMap.get('slug') ?? ''),
            },
          },
        ]),
      ],
    });
  });

  it('keeps on-page links on the current guide route', async () => {
    const harness = await RouterTestingHarness.create('/documentation/ef-core/quickstart');
    harness.fixture.detectChanges();
    await harness.fixture.whenStable();

    const links = Array.from(
      harness.routeNativeElement?.querySelectorAll<HTMLAnchorElement>('.guide-toc nav a') ?? [],
    );
    const link = links.find(
      (candidate) => candidate.textContent?.trim() === '2. Create the app and add packages',
    );

    expect(link).toBeTruthy();
    expect(link?.getAttribute('href')).toBe(
      '/documentation/ef-core/quickstart#2-create-the-app-and-add-packages',
    );

    link?.click();
    await harness.fixture.whenStable();

    expect(TestBed.inject(Router).url).toBe(
      '/documentation/ef-core/quickstart#2-create-the-app-and-add-packages',
    );
  });

  it('renders collapsible section and page indexes for small screens', async () => {
    const harness = await RouterTestingHarness.create('/documentation/ef-core/quickstart');
    harness.fixture.detectChanges();
    await harness.fixture.whenStable();

    const sectionIndex = harness.routeNativeElement?.querySelector('.guide-mobile-index');
    const pageIndex = harness.routeNativeElement?.querySelector('.guide-mobile-toc');

    expect(sectionIndex?.textContent).toContain('IN THIS SECTION');
    expect(sectionIndex?.textContent).toContain('EF Core quick start');
    expect(pageIndex?.textContent).toContain('ON THIS PAGE');
    expect(pageIndex?.textContent).toContain('2. Create the app and add packages');
  });

  it('keeps readers oriented within the library and a guided path', async () => {
    const harness = await RouterTestingHarness.create('/documentation/getting-started/quickstart');
    harness.fixture.detectChanges();
    await harness.fixture.whenStable();

    const page = harness.routeNativeElement;
    expect(page?.querySelector('.guide-breadcrumb')?.textContent).toContain('Documentation');
    expect(page?.querySelector('.guide-breadcrumb')?.textContent).toContain('Start here');
    expect(page?.querySelector('.guide-orientation')?.textContent).toContain('USE THIS GUIDE WHEN');
    expect(page?.querySelector('.guide-orientation')?.textContent).toContain('BEFORE YOU START');
    expect(page?.querySelector('.guide-orientation')?.textContent).toContain('FAST PATH');
    expect(page?.querySelector('.guide-journey')?.textContent).toContain(
      'Connect a .NET application',
    );
    expect(page?.querySelector('.guide-journey .current')?.textContent).toContain('YOU ARE HERE');
  });

  it('publishes guide-specific crawler metadata', async () => {
    const harness = await RouterTestingHarness.create('/documentation/getting-started/quickstart');
    harness.fixture.detectChanges();
    await harness.fixture.whenStable();

    expect(document.title).toBe('5-minute first app — BlueTusk');
    expect(document.head.querySelector<HTMLLinkElement>('link[rel="canonical"]')?.href).toBe(
      'https://bluetusk.io/documentation/getting-started/quickstart',
    );
    expect(document.head.querySelector<HTMLMetaElement>('meta[property="og:url"]')?.content).toBe(
      'https://bluetusk.io/documentation/getting-started/quickstart',
    );
  });
});
