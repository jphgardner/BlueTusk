import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Home } from './home';

describe('Home', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [Home],
      providers: [provideRouter([])],
    }).compileComponents();
  });

  it('renders the current 1.1 RC results and remaining stable checks', () => {
    const fixture = TestBed.createComponent(Home);
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';

    expect(text).toContain('13,056');
    expect(text).toContain('46');
    expect(text).toContain('Memory limits checked');
    expect(text).toContain('65 / 65');
    expect(text).toContain('Public RC packages');
  });

  it('prioritizes the architecture LCP image with a stable aspect ratio', () => {
    const fixture = TestBed.createComponent(Home);
    fixture.detectChanges();
    const image = (fixture.nativeElement as HTMLElement).querySelector<HTMLImageElement>(
      '.architecture-frame img',
    );

    expect(image?.getAttribute('loading')).toBe('eager');
    expect(image?.getAttribute('fetchpriority')).toBe('high');
    expect(image?.getAttribute('decoding')).toBe('async');
    expect(image?.getAttribute('width')).toBe('1376');
    expect(image?.getAttribute('height')).toBe('768');
  });

  it('provides a readable architecture flow for small screens', () => {
    const fixture = TestBed.createComponent(Home);
    fixture.detectChanges();

    const flow = (fixture.nativeElement as HTMLElement).querySelector('.architecture-mobile-flow');
    expect(flow?.getAttribute('aria-label')).toBe('How data moves through BlueTusk');
    expect(flow?.textContent).toContain('BlueTusk Provider');
    expect(flow?.textContent).toContain('Sync · Live · Graph');
  });
});
