import { Component, ElementRef, HostListener, ViewChild, computed, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { SITE_SEARCH } from './content/catalog';
import type { SearchRecord } from './content/models';

interface NavigationItem {
  label: string;
  href: string;
  description: string;
  icon: string;
}

@Component({
  selector: 'app-root',
  imports: [
    RouterLink,
    RouterLinkActive,
    RouterOutlet,
    MatButtonModule,
    MatIconModule,
    MatTooltipModule,
  ],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App {
  @ViewChild('searchTrigger') private searchTrigger?: ElementRef<HTMLButtonElement>;
  @ViewChild('searchField') private searchField?: ElementRef<HTMLInputElement>;
  @ViewChild('mobileNavTrigger') private mobileNavTrigger?: ElementRef<HTMLButtonElement>;
  @ViewChild('mobileNavFirst') private mobileNavFirst?: ElementRef<HTMLAnchorElement>;
  protected readonly searchOpen = signal(false);
  protected readonly searchQuery = signal('');
  private readonly guideSearch = signal<readonly SearchRecord[]>([]);
  protected readonly guideSearchUnavailable = signal(false);
  protected readonly mobileNavOpen = signal(false);

  protected readonly navItems: readonly NavigationItem[] = [
    {
      label: 'Platform',
      href: '/platform',
      description: 'The complete BlueTusk ecosystem and architecture.',
      icon: 'hub',
    },
    {
      label: 'Provider',
      href: '/provider',
      description: 'ADO.NET, pooling, COPY, notifications, and replication.',
      icon: 'storage',
    },
    {
      label: 'EF Core',
      href: '/ef-core',
      description: 'PostgreSQL-native queries, mappings, and migrations.',
      icon: 'data_object',
    },
    {
      label: 'Real Time',
      href: '/real-time',
      description: 'Streams, Sync, Live, relay, and control plane.',
      icon: 'stream',
    },
    {
      label: 'Extensions',
      href: '/extensions',
      description: 'PostGIS, pgvector, TimescaleDB, and more.',
      icon: 'extension',
    },
    {
      label: 'Graph',
      href: '/graph',
      description: 'PostgreSQL 19 SQL/PGQ and Continuous Graph.',
      icon: 'share',
    },
    {
      label: 'Evidence',
      href: '/evidence',
      description: 'V1 compatibility, security, provenance, performance, and open gates.',
      icon: 'verified_user',
    },
    {
      label: 'Documentation',
      href: '/documentation',
      description: 'The complete source-synchronized V1 engineering handbook.',
      icon: 'menu_book',
    },
    {
      label: 'Community',
      href: '/community',
      description: 'Contribute, report issues, and become a design partner.',
      icon: 'groups',
    },
  ];

  protected readonly filteredSearchItems = computed(() => {
    const query = this.searchQuery().trim().toLowerCase();
    const sections = SITE_SEARCH.map((item) => ({ ...item, icon: 'web' }));
    const guides = this.guideSearch().map((guide) => ({ ...guide, icon: 'description' }));
    const all = [...sections, ...guides];
    if (!query) return all.slice(0, 8);
    return all
      .map((item) => ({
        item,
        score:
          (item.title.toLowerCase().includes(query) ? 10 : 0) +
          (item.keywords.toLowerCase().includes(query) ? 5 : 0) +
          (item.description.toLowerCase().includes(query) ? 3 : 0) +
          (item.group.toLowerCase().includes(query) ? 1 : 0),
      }))
      .filter(({ score }) => score > 0)
      .sort((left, right) => right.score - left.score)
      .slice(0, 10)
      .map(({ item }) => item);
  });

  protected openSearch(): void {
    this.mobileNavOpen.set(false);
    this.searchQuery.set('');
    this.searchOpen.set(true);
    void import('../generated/guide-search.generated')
      .then(({ GUIDE_SEARCH }) => {
        this.guideSearch.set(GUIDE_SEARCH);
        this.guideSearchUnavailable.set(false);
      })
      .catch(() => {
        this.guideSearchUnavailable.set(true);
      });
    window.setTimeout(() => this.searchField?.nativeElement?.focus());
  }

  protected closeSearch(): void {
    const trigger =
      this.searchTrigger?.nativeElement ??
      document.querySelector<HTMLButtonElement>('[aria-label="Search BlueTusk"]');
    this.searchOpen.set(false);
    window.requestAnimationFrame(() => window.requestAnimationFrame(() => trigger?.focus()));
  }

  protected updateSearch(event: Event): void {
    this.searchQuery.set((event.target as HTMLInputElement).value);
  }

  protected openMobileNav(): void {
    this.mobileNavOpen.set(true);
    window.setTimeout(() => this.mobileNavFirst?.nativeElement?.focus());
  }

  protected closeMobileNav(restoreFocus = true): void {
    this.mobileNavOpen.set(false);
    if (restoreFocus) {
      window.requestAnimationFrame(() => this.mobileNavTrigger?.nativeElement?.focus());
    }
  }

  @HostListener('document:keydown.escape')
  protected onEscape(): void {
    if (this.searchOpen()) this.closeSearch();
    else if (this.mobileNavOpen()) this.closeMobileNav();
  }

  @HostListener('document:keydown', ['$event'])
  protected onSearchShortcut(event: KeyboardEvent): void {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
      event.preventDefault();
      this.openSearch();
    }
  }
}
