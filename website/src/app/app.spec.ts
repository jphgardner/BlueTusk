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
    expect(compiled.querySelector<HTMLImageElement>('.brand img')?.src).toContain(
      'bluetusk-mark.png',
    );
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

  it('opens a descriptive mobile navigation', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;

    compiled.querySelector<HTMLButtonElement>('.mobile-menu-trigger')?.click();
    fixture.detectChanges();
    await fixture.whenStable();

    const dialog = compiled.querySelector<HTMLElement>('#mobile-navigation');
    expect(dialog?.getAttribute('aria-modal')).toBe('true');
    expect(dialog?.textContent).toContain('Choose what you want to build.');
    expect(dialog?.textContent).toContain('Connect .NET applications directly to PostgreSQL.');
  });
});
