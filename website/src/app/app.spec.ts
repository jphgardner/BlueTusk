import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';

describe('App', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [App], providers: [provideRouter([])] });
  });

  it('offers a skip link and keyboard-accessible documentation search', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const host: HTMLElement = fixture.nativeElement;
    expect(host.querySelector('a.skip-link[href$="#main-content"]')).toBeTruthy();
    expect(host.querySelector('#main-content')).toBeTruthy();

    document.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'k', ctrlKey: true, cancelable: true }),
    );
    fixture.detectChanges();
    await fixture.whenStable();
    expect(host.querySelector('[role="dialog"] input[type="search"]')).toBeTruthy();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    fixture.detectChanges();
    expect(host.querySelector('[role="dialog"]')).toBeNull();
  });
});
