import { NgTemplateOutlet } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { GUIDES } from '../../generated/guides.generated';
import { guideRoute } from '../content/documentation-navigation';
import type { GuideManifestEntry } from '../content/models';
import { DocsNav } from './docs-nav';

@Component({
  selector: 'bt-guide-page',
  imports: [DocsNav, NgTemplateOutlet, RouterLink, MatButtonModule, MatIconModule],
  template: `
    @if (guide(); as current) {
      <div class="docs-layout article-layout">
        <aside class="docs-sidebar" aria-label="Guide navigation">
          <bt-docs-nav [category]="current.category" [activeSlug]="current.slug" />
        </aside>
        <article class="docs-article">
          <details class="mobile-docs-nav">
            <summary>Browse documentation <mat-icon>expand_more</mat-icon></summary>
            <bt-docs-nav [category]="current.category" [activeSlug]="current.slug" />
          </details>
          <header class="article-header">
            <nav class="breadcrumb" aria-label="Breadcrumb">
              <a routerLink="/documentation">Documentation</a>
              <a routerLink="/documentation" [fragment]="current.category">{{
                current.categoryLabel
              }}</a>
            </nav>
            <h1>{{ current.title }}</h1>
            <p>{{ current.summary }}</p>
          </header>
          @if (headings().length) {
            <details class="mobile-toc">
              <summary>On this page <mat-icon>expand_more</mat-icon></summary>
              <ng-container [ngTemplateOutlet]="tocLinks" />
            </details>
          }
          <div class="guide-prose" (click)="followLocalLink($event)">
            @for (block of current.blocks; track $index) {
              @switch (block.kind) {
                @case ('html') {
                  <div class="guide-html" [innerHTML]="block.html"></div>
                }
                @case ('heading') {
                  @switch (block.heading.level) {
                    @case (2) {
                      <h2 [id]="block.heading.id">{{ block.heading.text }}</h2>
                    }
                    @case (3) {
                      <h3 [id]="block.heading.id">{{ block.heading.text }}</h3>
                    }
                    @case (4) {
                      <h4 [id]="block.heading.id">{{ block.heading.text }}</h4>
                    }
                    @case (5) {
                      <h5 [id]="block.heading.id">{{ block.heading.text }}</h5>
                    }
                    @default {
                      <h6 [id]="block.heading.id">{{ block.heading.text }}</h6>
                    }
                  }
                }
                @case ('code') {
                  <section
                    class="example-panel"
                    [attr.aria-label]="(block.language || 'Text') + ' example'"
                  >
                    <div class="code-toolbar">
                      <span>{{ block.language || 'Text' }}</span>
                      <button mat-button type="button" (click)="copy(block.code, $index)">
                        {{ copied() === $index ? 'Copied' : 'Copy' }}
                      </button>
                    </div>
                    <pre tabindex="0"><code [innerHTML]="block.highlighted"></code></pre>
                  </section>
                }
              }
            }
          </div>
          <span class="sr-only" aria-live="polite">{{ copyMessage() }}</span>
          <footer class="article-footer">
            <a class="text-link" [href]="current.sourceUrl"
              >View Markdown <mat-icon>arrow_outward</mat-icon></a
            >
            <nav aria-label="Adjacent guides">
              @if (adjacent().previous; as item) {
                <a [routerLink]="guideRoute(item)"><small>Previous</small>{{ item.title }}</a>
              }
              @if (adjacent().next; as item) {
                <a [routerLink]="guideRoute(item)"><small>Next</small>{{ item.title }}</a>
              }
            </nav>
          </footer>
        </article>
        <aside class="desktop-toc" aria-label="On this page">
          @if (headings().length) {
            <strong>On this page</strong>
            <ng-container [ngTemplateOutlet]="tocLinks" />
          }
        </aside>
      </div>
    } @else {
      <div class="site-width section-space">
        <h1>Guide not found</h1>
        <p class="my-4">Find the topic in the documentation index.</p>
        <a mat-flat-button routerLink="/documentation">Browse documentation</a>
      </div>
    }

    <ng-template #tocLinks>
      <nav aria-label="Sections">
        @for (heading of headings(); track heading.id) {
          <a
            [class.toc-subheading]="heading.level === 3"
            [routerLink]="[]"
            [fragment]="heading.id"
            >{{ heading.text }}</a
          >
        }
      </nav>
    </ng-template>
  `,
})
export class GuidePage {
  private readonly routeData = toSignal(inject(ActivatedRoute).data);
  private readonly router = inject(Router);
  protected readonly guide = computed(
    () => this.routeData()?.['guide'] as GuideManifestEntry | undefined,
  );
  protected readonly headings = computed(
    () => this.guide()?.headings.filter(({ level }) => level === 2 || level === 3) ?? [],
  );
  protected readonly copied = signal<number | null>(null);
  protected readonly copyMessage = signal('');
  protected readonly guideRoute = guideRoute;
  protected readonly adjacent = computed(() => {
    const current = this.guide();
    const items = GUIDES.filter((guide) => guide.category === current?.category);
    const index = items.findIndex((guide) => guide.slug === current?.slug);
    return { previous: items[index - 1], next: items[index + 1] };
  });

  protected async copy(code: string, index: number): Promise<void> {
    try {
      await navigator.clipboard.writeText(code);
      this.copied.set(index);
      this.copyMessage.set('Code copied.');
    } catch {
      this.copied.set(null);
      this.copyMessage.set('Clipboard access is unavailable. Select the example to copy it.');
    }
  }

  protected followLocalLink(event: MouseEvent): void {
    if (
      event.defaultPrevented ||
      event.button !== 0 ||
      event.ctrlKey ||
      event.metaKey ||
      event.shiftKey ||
      event.altKey
    )
      return;
    const link = (event.target as Element).closest('a');
    const href = link?.getAttribute('href');
    if (!href?.startsWith('/documentation/')) return;
    event.preventDefault();
    void this.router.navigateByUrl(href);
  }
}
