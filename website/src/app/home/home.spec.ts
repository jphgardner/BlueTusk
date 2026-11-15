import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Home } from './home';
import { PRODUCTS } from '../content/catalog';

describe('Home', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [Home], providers: [provideRouter([])] });
  });

  it('links the first query and all products from the landing page', () => {
    const fixture = TestBed.createComponent(Home);
    fixture.detectChanges();
    const page: HTMLElement = fixture.nativeElement;
    expect(page.querySelector('h1')?.textContent).toMatch(/PostgreSQL.*\.NET/);
    expect(page.querySelector('a[href="/documentation/getting-started/quickstart"]')).toBeTruthy();
    expect(page.querySelectorAll('.product-tile')).toHaveLength(PRODUCTS.length);
    expect(page.querySelectorAll('[role="tab"]')).toHaveLength(2);
  });

  it('gives decorative hero art a stable aspect ratio and early fetch priority', () => {
    const fixture = TestBed.createComponent(Home);
    fixture.detectChanges();
    const art = (fixture.nativeElement as HTMLElement).querySelector('.hero-art');
    expect(art?.getAttribute('src')).toBe('/platform-layers.webp');
    expect(art?.getAttribute('width')).toBe('1200');
    expect(art?.getAttribute('height')).toBe('675');
    expect(art?.getAttribute('fetchpriority')).toBe('high');
    expect(art?.getAttribute('alt')).toBe('');
  });
});
