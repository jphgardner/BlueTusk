import { Component, computed, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { GUIDES } from '../../generated/guides.generated';
import { DOC_TOPICS, guideRoute, searchGuides } from '../content/documentation-navigation';
import { DocsNav } from './docs-nav';

@Component({
  selector: 'bt-docs-hub',
  imports: [DocsNav, RouterLink, MatButtonModule, MatIconModule],
  template: `
    <div class="docs-layout">
      <aside class="docs-sidebar" aria-label="Guide navigation"><bt-docs-nav /></aside>
      <div class="docs-index">
        <header class="docs-intro">
          <h1>Documentation</h1>
          <p>
            Start with a first query, find a guide for the feature you’re building, or look up a
            production issue.
          </p>
          <div class="flex flex-wrap gap-3">
            <a mat-flat-button routerLink="/documentation/getting-started/quickstart"
              >Run your first query <mat-icon iconPositionEnd>arrow_forward</mat-icon></a
            >
            <a mat-stroked-button routerLink="/documentation/getting-started/install"
              >Choose packages</a
            >
          </div>
        </header>

        <div class="search-field docs-filter">
          <mat-icon>search</mat-icon>
          <input
            type="search"
            placeholder="Filter guides by topic, package, or task"
            aria-label="Filter guides"
            [value]="query()"
            (input)="updateFilter($event)"
          />
        </div>

        @if (query().trim()) {
          <div class="guide-list" aria-label="Matching guides">
            <p role="status">{{ matches().length }} matching guides</p>
            @for (result of matches(); track result.route) {
              <a [routerLink]="result.route"
                ><span
                  ><strong>{{ result.title }}</strong
                  ><small>{{ result.description }}</small></span
                ><mat-icon>arrow_forward</mat-icon></a
              >
            }
          </div>
        } @else {
          <nav class="docs-topic-grid" aria-label="Browse by topic">
            @for (topic of topics; track topic.id) {
              <a routerLink="/documentation" [fragment]="topic.id">
                <mat-icon class="product-icon">{{ topic.icon }}</mat-icon>
                <span
                  ><strong>{{ topic.label }}</strong
                  ><small>{{ topic.description }}</small></span
                >
              </a>
            }
          </nav>

          @for (topic of topics; track topic.id) {
            <section class="docs-topic-section" [id]="topic.id">
              <h2>{{ topic.label }}</h2>
              <div class="guide-list">
                @for (guide of topic.guides; track guide.slug) {
                  <a [routerLink]="guideRoute(guide)">
                    <span
                      ><strong>{{ guide.title }}</strong
                      ><small>{{ guide.summary }}</small></span
                    >
                    <mat-icon>arrow_forward</mat-icon>
                  </a>
                }
              </div>
            </section>
          }
        }
      </div>
    </div>
  `,
})
export class DocsHubPage {
  protected readonly query = signal('');
  protected readonly guideRoute = guideRoute;
  protected readonly topics = DOC_TOPICS.map((topic) => ({
    ...topic,
    guides: GUIDES.filter((guide) => guide.category === topic.id),
  }));
  private readonly records = GUIDES.map((guide) => ({
    title: guide.title,
    description: guide.summary,
    route: guideRoute(guide),
    group: guide.categoryLabel,
    keywords: [...guide.keywords, ...guide.headings.map((heading) => heading.text)].join(' '),
  }));
  protected readonly matches = computed(() => searchGuides(this.records, this.query()));

  protected updateFilter(event: Event): void {
    this.query.set((event.target as HTMLInputElement).value);
  }
}
