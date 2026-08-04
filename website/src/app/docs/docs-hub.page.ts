import { Component, computed, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { GUIDES } from '../../generated/guides.generated';

@Component({
  selector: 'bt-docs-hub-page',
  imports: [RouterLink, MatButtonModule, MatIconModule],
  template: `
    <section class="page-hero docs-hero">
      <div>
        <span class="eyebrow"><i class="live-dot"></i> COMPLETE V1 HANDBOOK</span>
        <h1>Documentation that follows the <em>implementation.</em></h1>
        <p>
          Every repository guide is transformed at build time, cross-linked, searchable, and checked
          for drift. Start with a learning path or search the full technical record.
        </p>
        <label class="docs-search"
          ><mat-icon>search</mat-icon
          ><input
            autofocus
            [value]="query()"
            (input)="updateQuery($any($event.target).value)"
            placeholder="Search authentication, COPY, SQL/PGQ, replay…"
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
