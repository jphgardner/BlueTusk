import { A11yModule } from '@angular/cdk/a11y';
import { Component, HostListener, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { filter, map } from 'rxjs';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { SITE_SEARCH } from './content/catalog';
import { searchGuides } from './content/documentation-navigation';
import type { SearchRecord } from './content/models';

@Component({
  selector: 'app-root',
  imports: [A11yModule, RouterLink, RouterLinkActive, RouterOutlet, MatButtonModule, MatIconModule],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App {
  protected readonly searchOpen = signal(false);
  protected readonly mobileOpen = signal(false);
  protected readonly searchQuery = signal('');
  private readonly guideSearch = signal<readonly SearchRecord[]>([]);
  protected readonly searchStatus = signal<'idle' | 'loading' | 'ready' | 'failed'>('idle');

  protected readonly navItems = [
    { label: 'Platform', href: '/platform' },
    { label: 'Use cases', href: '/use-cases' },
    { label: 'Learn', href: '/learn' },
    { label: 'Documentation', href: '/documentation' },
    { label: 'Community', href: '/community' },
  ];

  protected readonly results = computed(() =>
    searchGuides([...SITE_SEARCH, ...this.guideSearch()], this.searchQuery()).slice(0, 12),
  );

  private readonly router = inject(Router);
  protected readonly currentUrl = toSignal(
    this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
      map((event) => event.urlAfterRedirects.split('#')[0]),
    ),
    { initialValue: this.router.url.split('#')[0] },
  );

  protected openSearch(): void {
    this.mobileOpen.set(false);
    this.searchOpen.set(true);
    if (this.searchStatus() === 'ready' || this.searchStatus() === 'loading') return;
    this.searchStatus.set('loading');
    void import('../generated/guide-search.generated').then(
      ({ GUIDE_SEARCH }) => {
        this.guideSearch.set(GUIDE_SEARCH);
        this.searchStatus.set('ready');
      },
      () => this.searchStatus.set('failed'),
    );
  }

  protected closeSearch(): void {
    this.searchOpen.set(false);
  }

  protected closeMobile(): void {
    this.mobileOpen.set(false);
  }

  protected searchUpdate(event: Event): void {
    this.searchQuery.set((event.target as HTMLInputElement).value);
  }

  protected openResult(): void {
    const first = this.results()[0];
    if (!first) return;
    this.closeSearch();
    void this.router.navigateByUrl(first.route);
  }

  @HostListener('document:keydown', ['$event'])
  protected onShortcut(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      if (this.searchOpen()) this.closeSearch();
      else if (this.mobileOpen()) this.closeMobile();
    } else if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
      event.preventDefault();
      if (!this.searchOpen()) this.openSearch();
    }
  }
}
