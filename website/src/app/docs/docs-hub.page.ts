import { Component, ElementRef, HostListener, ViewChild, computed, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { GUIDES } from '../../generated/guides.generated';
import {
  DOCUMENTATION_JOURNEYS,
  DOCUMENTATION_SECTIONS,
  compareDocumentationGuides,
  documentationSectionFor,
} from '../content/documentation-navigation';

@Component({
  selector: 'bt-docs-hub-page',
  imports: [RouterLink, MatIconModule],
  template: `
    <section class="page-hero docs-hero">
      <div>
        <span class="eyebrow"><i class="live-dot"></i> BLUETUSK DOCUMENTATION</span>
        <h1>From first query to <em>production.</em></h1>
        <p>
          Tell us what you want to build. We’ll give you a short path through the guides, with the
          deeper technical reference there when you need it.
        </p>
        <label class="docs-search">
          <mat-icon>search</mat-icon>
          <input
            #docsSearch
            [value]="query()"
            (input)="updateQuery($any($event.target).value)"
            placeholder="Search setup, pooling, Streams, errors…"
            aria-label="Search all BlueTusk documentation"
          />
          <kbd>/</kbd>
        </label>
        <div class="docs-stats" aria-label="Documentation coverage">
          <span
            ><strong>{{ coreGuides.length }}</strong> practical guides</span
          >
          <span
            ><strong>{{ allGuides.length }}</strong> pages searchable</span
          >
          <span><strong>4</strong> clear sections</span>
        </div>
      </div>
    </section>

    @if (query()) {
      <section class="docs-search-page page-section">
        <header class="result-summary">
          <span>SEARCHING ALL DOCUMENTATION</span>
          <strong>{{ filtered().length }} results for “{{ query() }}”</strong>
          <button type="button" (click)="updateQuery('')">Clear search</button>
        </header>
        <div class="guide-list search-guide-list">
          @for (guide of filtered(); track guide.sourcePath) {
            <a [routerLink]="['/documentation', guide.category, guide.slug]">
              <div>
                <small>
                  {{ sectionFor(guide).label }} ·
                  {{ guide.listed ? 'Guide' : 'Engineering reference' }} ·
                  {{ guide.readMinutes }} min
                </small>
                <h2>{{ guide.title }}</h2>
                <p>{{ guide.summary }}</p>
              </div>
              <mat-icon>arrow_forward</mat-icon>
            </a>
          } @empty {
            <div class="empty-state">
              <mat-icon>search_off</mat-icon>
              <h2>No documentation matches that search.</h2>
              <p>Try the task you are doing, an error message, or a product name.</p>
            </div>
          }
        </div>
      </section>
    } @else {
      <section class="docs-onramp" aria-labelledby="docs-onramp-title">
        <header>
          <div>
            <span>NEW TO BLUETUSK?</span>
            <h2 id="docs-onramp-title">Use this path once, in order.</h2>
            <p>Four short steps take you from package choice to a production plan.</p>
          </div>
          <a routerLink="/documentation/getting-started/quickstart" class="onramp-action">
            Start the quickstart <mat-icon>arrow_forward</mat-icon>
          </a>
        </header>
        <ol class="docs-path-grid">
          @for (path of starterPath; track path.route; let index = $index) {
            <li>
              <a [routerLink]="path.route">
                <span>0{{ index + 1 }}</span>
                <mat-icon>{{ path.icon }}</mat-icon>
                <small>{{ path.kicker }}</small>
                <h3>{{ path.title }}</h3>
                <p>{{ path.body }}</p>
                <strong>{{ path.action }} <mat-icon>arrow_forward</mat-icon></strong>
              </a>
            </li>
          }
        </ol>
      </section>

      <section class="docs-journeys page-section" aria-labelledby="docs-journeys-title">
        <header class="section-heading docs-section-heading">
          <div>
            <span class="eyebrow">CHOOSE YOUR GOAL</span>
            <h2 id="docs-journeys-title">What are you trying to build?</h2>
            <p>
              Each path starts with context, moves into implementation, and ends with operations.
            </p>
          </div>
        </header>
        <div class="journey-grid">
          @for (journey of journeys; track journey.id) {
            <article class="journey-card">
              <header>
                <mat-icon>{{ journey.icon }}</mat-icon>
                <div>
                  <small>{{ journey.eyebrow }}</small>
                  <h3>{{ journey.title }}</h3>
                </div>
              </header>
              <p>{{ journey.description }}</p>
              <ol>
                @for (step of journey.steps; track step.route; let stepIndex = $index) {
                  <li>
                    <a [routerLink]="step.route">
                      <span>{{ stepIndex + 1 }}</span>
                      <small>{{ step.label }}</small>
                      <strong>{{ step.title }}</strong>
                      <mat-icon>arrow_forward</mat-icon>
                    </a>
                  </li>
                }
              </ol>
            </article>
          }
        </div>
      </section>

      <section class="docs-library page-section" aria-labelledby="docs-library-title">
        <header class="section-heading docs-section-heading">
          <div>
            <span class="eyebrow">DOCUMENTATION LIBRARY</span>
            <h2 id="docs-library-title">Browse by where you are in the work.</h2>
            <p>
              The main library contains practical application guides. Deep project records are
              separate below.
            </p>
          </div>
        </header>
        <div class="docs-layout">
          <aside class="docs-categories" aria-label="Documentation sections">
            <small>JUMP TO</small>
            @for (section of sections(); track section.id) {
              <a routerLink="/documentation" [fragment]="section.id">
                <mat-icon>{{ section.icon }}</mat-icon>
                <span
                  ><strong>{{ section.label }}</strong
                  ><small>{{ section.guides.length }} guides</small></span
                >
              </a>
            }
            <a routerLink="/documentation" fragment="reference">
              <mat-icon>library_books</mat-icon>
              <span
                ><strong>Engineering reference</strong
                ><small>{{ referenceGuides.length }} pages</small></span
              >
            </a>
          </aside>
          <main class="docs-results">
            @for (section of sections(); track section.id) {
              <section class="docs-category" [id]="section.id">
                <header>
                  <div>
                    <span>{{ section.label }}</span>
                    <h2>{{ section.title }}</h2>
                    <p>{{ section.description }}</p>
                  </div>
                  <strong>{{ section.guides.length }}</strong>
                </header>
                @for (category of section.categories; track category.id) {
                  <section class="docs-category-group">
                    <h3>{{ category.label }}</h3>
                    <div class="guide-list">
                      @for (guide of category.guides; track guide.sourcePath) {
                        <a [routerLink]="['/documentation', guide.category, guide.slug]">
                          <div>
                            <small>{{ guide.readMinutes }} min read</small>
                            <h4>{{ guide.title }}</h4>
                            <p>{{ guide.summary }}</p>
                          </div>
                          <mat-icon>arrow_forward</mat-icon>
                        </a>
                      }
                    </div>
                  </section>
                }
              </section>
            }

            <details class="reference-library" id="reference">
              <summary>
                <span>
                  <small>ENGINEERING REFERENCE</small>
                  <strong>Contracts, decisions, evidence, and release records</strong>
                  <p>
                    Useful for maintainers and investigations; not required for the normal learning
                    path.
                  </p>
                </span>
                <span class="reference-count">{{ referenceGuides.length }}</span>
                <mat-icon>expand_more</mat-icon>
              </summary>
              <div class="reference-groups">
                @for (category of referenceCategories(); track category.id) {
                  <section>
                    <h3>{{ category.label }}</h3>
                    <nav [attr.aria-label]="category.label + ' reference pages'">
                      @for (guide of category.guides; track guide.sourcePath) {
                        <a [routerLink]="['/documentation', guide.category, guide.slug]">
                          <span>{{ guide.title }}</span
                          ><small>{{ guide.readMinutes }} min</small>
                        </a>
                      }
                    </nav>
                  </section>
                }
              </div>
            </details>
          </main>
        </div>
      </section>
    }
  `,
})
export class DocsHubPage {
  @ViewChild('docsSearch') private docsSearch?: ElementRef<HTMLInputElement>;
  protected readonly allGuides = GUIDES;
  protected readonly coreGuides = GUIDES.filter((guide) => guide.listed);
  protected readonly referenceGuides = GUIDES.filter((guide) => !guide.listed);
  protected readonly journeys = DOCUMENTATION_JOURNEYS;
  protected readonly query = signal('');
  protected readonly starterPath = [
    {
      kicker: 'CHOOSE',
      title: 'Install only what you need',
      body: 'Pick the stable or preview channel and the smallest package set for your application.',
      action: 'Choose packages',
      icon: 'download',
      route: '/documentation/getting-started/install',
    },
    {
      kicker: 'TRY',
      title: 'Run your first query',
      body: 'Create one shared data source and execute a safe parameterized PostgreSQL query.',
      action: 'Follow the quickstart',
      icon: 'terminal',
      route: '/documentation/getting-started/quickstart',
    },
    {
      kicker: 'UNDERSTAND',
      title: 'Learn the core model',
      body: 'Understand ownership, sessions, checkpoints, and capabilities before adding more layers.',
      action: 'Learn the concepts',
      icon: 'school',
      route: '/documentation/getting-started/concepts',
    },
    {
      kicker: 'SHIP',
      title: 'Prepare for production',
      body: 'Work through security, limits, monitoring, recovery, deployment, and rollback.',
      action: 'Use the checklist',
      icon: 'rocket_launch',
      route: '/documentation/operations/production-checklist',
    },
  ] as const;

  protected readonly sections = computed(() =>
    DOCUMENTATION_SECTIONS.filter((section) => section.id !== 'reference').map((section) => {
      const guides = this.coreGuides
        .filter((guide) => documentationSectionFor(guide).id === section.id)
        .sort(compareDocumentationGuides);
      return { ...section, guides, categories: this.groupByCategory(guides) };
    }),
  );

  protected readonly referenceCategories = computed(() => {
    const categoryIds = [...new Set(this.referenceGuides.map((guide) => guide.category))];
    return categoryIds.map((id) => {
      const guides = this.referenceGuides
        .filter((guide) => guide.category === id)
        .sort((left, right) => left.title.localeCompare(right.title));
      return { id, label: guides[0]?.categoryLabel ?? id, guides };
    });
  });

  protected readonly filtered = computed(() => {
    const query = this.query().trim().toLowerCase();
    if (!query) return [];
    return this.allGuides
      .map((guide) => ({ guide, score: this.score(guide, query) }))
      .filter((result) => result.score > 0)
      .sort(
        (left, right) =>
          right.score - left.score ||
          Number(right.guide.listed) - Number(left.guide.listed) ||
          left.guide.title.localeCompare(right.guide.title),
      )
      .slice(0, 60)
      .map((result) => result.guide);
  });

  constructor(
    private route: ActivatedRoute,
    private router: Router,
  ) {
    this.query.set(this.route.snapshot.queryParamMap.get('q') ?? '');
  }

  protected sectionFor = documentationSectionFor;

  protected updateQuery(value: string): void {
    this.query.set(value);
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { q: value || null },
      replaceUrl: true,
    });
  }

  @HostListener('document:keydown', ['$event'])
  protected focusSearch(event: KeyboardEvent): void {
    const target = event.target as HTMLElement | null;
    if (event.key !== '/' || target?.matches('input, textarea, [contenteditable="true"]')) return;
    event.preventDefault();
    this.docsSearch?.nativeElement.focus();
  }

  private score(guide: (typeof GUIDES)[number], query: string): number {
    let score = 0;
    if (guide.title.toLowerCase().includes(query)) score += 12;
    if (guide.keywords.join(' ').toLowerCase().includes(query)) score += 8;
    if (guide.summary.toLowerCase().includes(query)) score += 5;
    if (guide.searchText.toLowerCase().includes(query)) score += 3;
    if (guide.categoryLabel.toLowerCase().includes(query)) score += 2;
    if (guide.headings.some((heading) => heading.text.toLowerCase().includes(query))) score += 2;
    if (guide.listed) score += 1;
    return score;
  }

  private groupByCategory(guides: readonly (typeof GUIDES)[number][]) {
    const categoryIds = [...new Set(guides.map((guide) => guide.category))];
    return categoryIds.map((id) => {
      const categoryGuides = guides.filter((guide) => guide.category === id);
      return { id, label: categoryGuides[0]?.categoryLabel ?? id, guides: categoryGuides };
    });
  }
}
