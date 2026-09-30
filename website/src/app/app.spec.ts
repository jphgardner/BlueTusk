import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter([])],
    }).compileComponents();
  });

  it('should create the app', () => {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    expect(app).toBeTruthy();
  });

  it('should render the BlueTusk navigation shell', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await fixture.whenStable();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('.brand')?.textContent).toContain('BlueTusk');
    expect(compiled.querySelector('.desktop-nav')?.textContent).toContain('Provider');
  });

  it('opens the global search with the keyboard shortcut', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', ctrlKey: true }));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('[role="dialog"]')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('.search-results')?.textContent).toContain(
      'Platform',
    );
  });

  it('loads documentation results when search opens', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('[aria-label="Search BlueTusk"]')
      ?.click();
    await fixture.whenStable();
    fixture.detectChanges();
    const input = (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>(
      '.search-field',
    );
    expect(input).toBeTruthy();
    input!.value = 'quickstart';
    input!.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(
        (fixture.nativeElement as HTMLElement).querySelector(
          'a[href="/documentation/getting-started/quickstart"]',
        ),
      ).toBeTruthy();
    });
  });

  it('opens and closes mobile navigation', () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('[aria-label="Open navigation"]')
      ?.click();
    fixture.detectChanges();
    expect(
      (fixture.nativeElement as HTMLElement).querySelector('#mobile-navigation a'),
    ).toBeTruthy();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('#mobile-navigation')).toBeNull();
  });
});
