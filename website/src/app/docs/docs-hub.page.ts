import { Component, computed, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { GUIDES } from '../../generated/guides.generated';

@Component({
  selector: 'bt-docs-hub-page',
  imports: [RouterLink, MatButtonModule, MatIconModule],
  styleUrl: './docs-hub.page.scss',
  template: `
    <section class="page-hero docs-hero">
      <div>
        <span class="eyebrow"><i class="live-dot"></i> 1.2.0-RC.1 · SOURCE-SYNCHRONIZED</span>
        <h1>Production documentation, from <em>first install to incident.</em></h1>
        <p>
          Choose an outcome, follow the steps, and keep the operational detail close at hand. Every
          guide is built from the repository source, searchable, cross-linked, and checked for
          drift.
        </p>
        <label class="docs-search"
          ><mat-icon>search</mat-icon
          ><input
            autofocus
            [value]="query()"
            (input)="updateQuery($any($event.target).value)"
            placeholder="Search guides and topics…"
            aria-label="Search documentation"
          /><kbd>/</kbd></label
        >
        <div class="docs-stats">
          <span
            ><strong>{{ guides.length }}</strong> guides</span
          ><span
            ><strong>{{ categories().length }}</strong> categories</span
          ><span
            ><strong>{{ totalWords().toLocaleString() }}</strong> documented words</span
          >
        </div>
      </div>
    </section>

    <section class="docs-onramp" aria-labelledby="docs-onramp-title">
      <header>
        <div>
          <span>START WITH AN OUTCOME</span>
          <h2 id="docs-onramp-title">From zero to an operable service.</h2>
        </div>
        <a routerLink="/documentation/getting-started/release-1-1-rc1">
          <span><i></i> 1.1.0-rc.1 is public</span>
          <small>65 packages · clean install checks passed</small>
          <mat-icon>arrow_forward</mat-icon>
        </a>
      </header>
      <div class="docs-path-grid">
        @for (path of paths; track path.title; let index = $index) {
          <a [routerLink]="path.route">
            <span>0{{ index + 1 }}</span>
            <mat-icon>{{ path.icon }}</mat-icon>
            <small>{{ path.kicker }}</small>
            <h3>{{ path.title }}</h3>
            <p>{{ path.body }}</p>
            <strong>{{ path.action }} <mat-icon>arrow_forward</mat-icon></strong>
          </a>
        }
      </div>
    </section>

    <section class="docs-layout">
      <aside class="docs-categories">
        <small>CATEGORIES</small
        ><a routerLink="/documentation" [queryParams]="query() ? { q: query() } : {}" fragment="all"
          >All guides <span>{{ guides.length }}</span></a
        >
        @for (category of categories(); track category.id) {
          <a
            routerLink="/documentation"
            [queryParams]="query() ? { q: query() } : {}"
            [fragment]="category.id"
            >{{ category.label }}<span>{{ category.guides.length }}</span></a
          >
        }
      </aside>
      <main class="docs-results">
        @if (query()) {
          <header class="result-summary">
            <span>SEARCH RESULTS</span
            ><strong>{{ filtered().length }} matches for “{{ query() }}”</strong>
          </header>
          <div class="guide-list">
            @for (guide of filtered(); track guide.sourcePath) {
              <a [routerLink]="['/documentation', guide.category, guide.slug]"
                ><div>
                  <small>{{ guide.categoryLabel }} · {{ guide.readMinutes }} min</small>
                  <h2>{{ guide.title }}</h2>
                  <p>{{ guide.summary }}</p>
                </div>
                <mat-icon>arrow_forward</mat-icon></a
              >
            } @empty {
              <div class="empty-state">
                <mat-icon>search_off</mat-icon>
                <h2>No guide matches that search.</h2>
                <p>Try a product, PostgreSQL capability, or operational concept.</p>
              </div>
            }
          </div>
        } @else {
          @for (category of categories(); track category.id) {
            <section class="docs-category" [id]="category.id">
              <header>
                <div>
                  <span>{{ category.label }}</span>
                  <h2>{{ category.title }}</h2>
                </div>
                <strong>{{ category.guides.length }}</strong>
              </header>
              <div class="guide-list">
                @for (guide of category.guides; track guide.sourcePath) {
                  <a [routerLink]="['/documentation', guide.category, guide.slug]"
                    ><div>
                      <small>{{ guide.readMinutes }} min · {{ guide.sourcePath }}</small>
                      <h3>{{ guide.title }}</h3>
                      <p>{{ guide.summary }}</p>
                    </div>
                    <mat-icon>arrow_forward</mat-icon></a
                  >
                }
              </div>
            </section>
          }
        }
      </main>
    </section>
  `,
})
export class DocsHubPage {
  protected readonly guides = GUIDES;
  protected readonly paths = [
    {
      kicker: 'EVALUATE',
      title: 'Install the right packages',
      body: 'Choose stable or release candidate, install only what you need, and confirm it restores.',
      action: 'Installation guide',
      icon: 'download',
      route: '/documentation/getting-started/install',
    },
    {
      kicker: 'BUILD',
      title: 'Run the first query',
      body: 'Start PostgreSQL, create one shared data source, and run a safe parameterized query.',
      action: 'Developer quickstart',
      icon: 'terminal',
      route: '/documentation/getting-started/quickstart',
    },
    {
      kicker: 'SHIP',
      title: 'Design for production',
      body: 'Plan security, limits, monitoring, rollout, backups, and recovery before launch.',
      action: 'Production checklist',
      icon: 'rocket_launch',
      route: '/documentation/operations/production-checklist',
    },
    {
      kicker: 'OPERATE',
      title: 'Diagnose with evidence',
      body: 'Follow a problem from the first connection through streaming, delivery, and recovery.',
      action: 'Troubleshooting',
      icon: 'monitor_heart',
      route: '/documentation/operations/troubleshooting',
    },
  ] as const;
  protected readonly query = signal('');
  protected readonly totalWords = computed(() =>
    this.guides.reduce((total, guide) => total + guide.wordCount, 0),
  );
  protected readonly categories = computed(() => {
    const titles: Record<string, string> = {
      'getting-started': 'Orient yourself and run the first provider sample.',
      provider: 'Own every PostgreSQL data path through ADO.NET.',
      'ef-core': 'Keep PostgreSQL features inside familiar EF workflows.',
      'real-time': 'Move committed changes through explicit delivery contracts.',
      extensions: 'Compose optional PostgreSQL-native capabilities.',
      graph: 'Query and project connected data.',
      architecture:
        'Understand ownership, dependency rules, decisions, and performance constraints.',
      operations: 'Build, test, secure, and evolve the repository.',
    };
    const ids = [...new Set(this.guides.map((x) => x.category))];
    return ids.map((id) => {
      const guides = this.guides.filter((x) => x.category === id).sort((a, b) => a.order - b.order);
      return {
        id,
        label: guides[0]?.categoryLabel ?? id,
        title: titles[id] ?? 'Technical guides',
        guides,
      };
    });
  });
  protected readonly filtered = computed(() => {
    const q = this.query().trim().toLowerCase();
    if (!q) return this.guides;
    return this.guides
      .map((guide) => ({ guide, score: this.score(guide, q) }))
      .filter((x) => x.score > 0)
      .sort((a, b) => b.score - a.score || a.guide.order - b.guide.order)
      .map((x) => x.guide);
  });
  constructor(
    private route: ActivatedRoute,
    private router: Router,
  ) {
    this.query.set(this.route.snapshot.queryParamMap.get('q') ?? '');
  }
  protected updateQuery(value: string): void {
    this.query.set(value);
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { q: value || null },
      replaceUrl: true,
    });
  }
  private score(guide: (typeof GUIDES)[number], query: string): number {
    let score = 0;
    if (guide.title.toLowerCase().includes(query)) score += 10;
    if (guide.keywords.join(' ').toLowerCase().includes(query)) score += 6;
    if (guide.summary.toLowerCase().includes(query)) score += 4;
    if (guide.searchText.toLowerCase().includes(query)) score += 3;
    if (guide.categoryLabel.toLowerCase().includes(query)) score += 2;
    if (guide.headings.some((x) => x.text.toLowerCase().includes(query))) score += 2;
    return score;
  }
}
