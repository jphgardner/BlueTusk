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
});
