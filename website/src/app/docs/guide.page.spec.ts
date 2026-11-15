import { TestBed } from '@angular/core/testing';
import { provideRouter, Router, TitleStrategy } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from '../app.routes';
import { PageMetadata } from '../page-metadata';

describe('GuidePage', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter(routes), { provide: TitleStrategy, useClass: PageMetadata }],
    });
  });

  it('renders a real anchor and copies its URL into the table of contents', async () => {
    const harness = await RouterTestingHarness.create('/documentation/getting-started/quickstart');
    const page = harness.routeNativeElement!;
    const heading = page.querySelector('[id="4-replace-programcs"]');
    expect(heading?.tagName).toBe('H2');
    const link = page.querySelector<HTMLAnchorElement>(
      '.desktop-toc a[href="/documentation/getting-started/quickstart#4-replace-programcs"]',
    );
    expect(link).toBeTruthy();
    link?.click();
    await harness.fixture.whenStable();
    expect(TestBed.inject(Router).url).toBe(
      '/documentation/getting-started/quickstart#4-replace-programcs',
    );
  });

  it('updates page metadata and marks a missing guide as noindex', async () => {
    const harness = await RouterTestingHarness.create('/documentation/getting-started/quickstart');
    expect(document.title).toBe('Run your first query · BlueTusk');
    expect(document.querySelector('link[rel="canonical"]')?.getAttribute('href')).toBe(
      'https://bluetusk.io/documentation/getting-started/quickstart',
    );
    await harness.navigateByUrl('/documentation/operations/release-readiness');
    expect(harness.routeNativeElement?.querySelector('h1')?.textContent).toBe('Guide not found');
    expect(document.querySelector('meta[name="robots"]')?.getAttribute('content')).toBe(
      'noindex, follow',
    );
    expect(document.querySelector('link[rel="canonical"]')).toBeNull();
  });
});
