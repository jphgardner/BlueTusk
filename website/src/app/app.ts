import { Component, ElementRef, HostListener, ViewChild, computed, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatTooltipModule } from '@angular/material/tooltip';
import { GUIDE_SEARCH } from '../generated/guide-search.generated';
import { SITE_SEARCH } from './content/catalog';

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
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
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
  protected readonly mobileNavOpen = signal(false);

  protected readonly navItems: readonly NavigationItem[] = [
    {
      label: 'Platform',
      href: '/platform',
      description: 'See how the BlueTusk products work together.',
      icon: 'hub',
    },
    {
      label: 'Provider',
      href: '/provider',
      description: 'Connect .NET applications directly to PostgreSQL.',
      icon: 'storage',
    },
    {
      label: 'EF Core',
      href: '/ef-core',
      description: 'Use PostgreSQL through familiar EF Core workflows.',
      icon: 'data_object',
    },
    {
      label: 'Real Time',
      href: '/real-time',
      description: 'Move changes, sync data, and update users live.',
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
      description: 'Query relationships and keep results up to date.',
      icon: 'share',
    },
    {
      label: 'Documentation',
      href: '/documentation',
      description: 'Find tutorials, guides, and production help.',
      icon: 'menu_book',
    },
  ];

  protected readonly filteredSearchItems = computed(() => {
    const query = this.searchQuery().trim().toLowerCase();
    const sections = SITE_SEARCH.map((item) => ({ ...item, icon: 'web' }));
    const guides = GUIDE_SEARCH.map((guide) => ({ ...guide, icon: 'description' }));
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
    if (this.searchOpen()) {
      this.closeSearch();
    } else if (this.mobileNavOpen()) {
      this.closeMobileNav();
    }
  }

  @HostListener('document:keydown', ['$event'])
  protected onSearchShortcut(event: KeyboardEvent): void {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
      event.preventDefault();
      this.openSearch();
    }
  }
}
