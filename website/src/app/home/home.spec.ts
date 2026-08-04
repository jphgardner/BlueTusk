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

  it('renders the current V1 evidence values and pending endurance boundary', () => {
    const fixture = TestBed.createComponent(Home);
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';

    expect(text).toContain('12,975');
    expect(text).toContain('37');
    expect(text).toContain('Allocation budgets');
    expect(text).toContain('72h / 24h');
    expect(text).toContain('Evidence still pending');
  });

  it('defers the below-fold architecture image with a stable aspect ratio', () => {
    const fixture = TestBed.createComponent(Home);
    fixture.detectChanges();
    const image = (fixture.nativeElement as HTMLElement).querySelector<HTMLImageElement>(
      '.architecture-frame img',
    );

    expect(image?.getAttribute('loading')).toBe('lazy');
    expect(image?.getAttribute('decoding')).toBe('async');
    expect(image?.getAttribute('width')).toBe('1376');
    expect(image?.getAttribute('height')).toBe('768');
  });
});
