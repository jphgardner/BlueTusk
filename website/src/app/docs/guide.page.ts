import {
  AfterViewChecked,
  Component,
  ElementRef,
  Inject,
  PLATFORM_ID,
  ViewChild,
  computed,
  signal,
} from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { Meta, Title } from '@angular/platform-browser';
import { DOCUMENT } from '@angular/common';
import { GUIDES } from '../../generated/guides.generated';

@Component({
  selector: 'bt-guide-page',
  imports: [RouterLink, MatIconModule],
  template: `
    @if (guide(); as current) {
      <div class="guide-shell">
        <aside class="guide-sidebar">
          <a routerLink="/documentation" class="back-link"
            ><mat-icon>arrow_back</mat-icon>All documentation</a
          >
          <small class="guide-desktop-index">{{ current.categoryLabel }}</small>
          <nav class="guide-desktop-index" [attr.aria-label]="current.categoryLabel + ' guides'">
            @for (item of categoryGuides(); track item.slug) {
              <a
                [routerLink]="['/documentation', item.category, item.slug]"
                [class.active]="item.slug === current.slug"
                >{{ item.title }}</a
              >
            }
          </nav>
          <details class="guide-mobile-index">
            <summary>
              <span
                ><small>IN THIS SECTION</small><strong>{{ current.title }}</strong></span
              >
              <mat-icon>expand_more</mat-icon>
            </summary>
            <nav [attr.aria-label]="current.categoryLabel + ' guides'">
              @for (item of categoryGuides(); track item.slug) {
                <a
                  [routerLink]="['/documentation', item.category, item.slug]"
                  [class.active]="item.slug === current.slug"
                  >{{ item.title }}</a
                >
              }
            </nav>
          </details>
        </aside>
        <main class="guide-main">
          <header>
            <span>{{ current.categoryLabel }}</span>
            <h1>{{ current.title }}</h1>
            <p>{{ current.summary }}</p>
            <div class="guide-meta">
              <span>{{ current.readMinutes }} min read</span
              ><span>Maintained in the repository</span>
            </div>
            <a [href]="current.sourceUrl" target="_blank" rel="noreferrer"
              ><mat-icon>code</mat-icon>View source on GitHub<mat-icon>open_in_new</mat-icon></a
            >
          </header>
          @if (current.headings.length) {
            <details class="guide-mobile-toc">
              <summary><span>ON THIS PAGE</span><mat-icon>expand_more</mat-icon></summary>
              <nav>
                @for (heading of current.headings; track heading.id) {
                  @if (heading.level <= 3 && heading.level > 1) {
                    <a [routerLink]="[]" [fragment]="heading.id">{{ heading.text }}</a>
                  }
                }
              </nav>
            </details>
          }
          <article #guideContent class="guide-content">
            @for (block of current.blocks; track $index) {
              @if (block.kind === 'html') {
                <div [innerHTML]="block.html"></div>
              } @else {
                <div class="guide-code">
                  <button
                    type="button"
                    class="copy-code"
                    (click)="copyCode(block.code, $event)"
                    aria-label="Copy code block"
                  >
                    Copy
                  </button>
                  <pre><code class="hljs" [class]="'hljs' + (block.language ? ' language-' + block.language : '')" [innerHTML]="block.highlighted"></code></pre>
                </div>
              }
            }
          </article>
          <nav class="guide-pagination" aria-label="Guide pagination">
            @if (previous(); as item) {
              <a [routerLink]="['/documentation', item.category, item.slug]"
                ><small>PREVIOUS</small
                ><strong><mat-icon>arrow_back</mat-icon>{{ item.title }}</strong></a
              >
            } @else {
              <span></span>
            }
            @if (next(); as item) {
              <a [routerLink]="['/documentation', item.category, item.slug]"
                ><small>NEXT</small
                ><strong>{{ item.title }}<mat-icon>arrow_forward</mat-icon></strong></a
              >
            }
          </nav>
        </main>
        <aside class="guide-toc">
          <small>ON THIS PAGE</small>
          <nav>
            @for (heading of current.headings; track heading.id) {
              @if (heading.level <= 3 && heading.level > 1) {
                <a [routerLink]="[]" [fragment]="heading.id" [class.nested]="heading.level === 3">{{
                  heading.text
                }}</a>
              }
            }
          </nav>
          <a [href]="current.sourceUrl" target="_blank" rel="noreferrer"
            >Edit source <mat-icon>open_in_new</mat-icon></a
          >
        </aside>
      </div>
    } @else {
      <section class="not-found">
        <mat-icon>find_in_page</mat-icon>
        <h1>Guide not found</h1>
        <p>The guide may have moved with its repository source.</p>
        <a routerLink="/documentation">Return to documentation</a>
      </section>
    }
  `,
})
export class GuidePage implements AfterViewChecked {
  @ViewChild('guideContent') private guideContent?: ElementRef<HTMLElement>;
  private readonly category = signal('');
  private readonly slug = signal('');
  private readonly pendingFragment = signal<string | null>(null);
  private readonly isBrowser: boolean;
  protected readonly guide = computed(() =>
    GUIDES.find((x) => x.category === this.category() && x.slug === this.slug()),
  );
  protected readonly categoryGuides = computed(() =>
    GUIDES.filter(
      (x) => x.category === this.category() && (this.guide()?.listed ? x.listed : true),
    ).sort((a, b) => a.order - b.order),
  );
  protected readonly currentIndex = computed(() =>
    this.categoryGuides().findIndex((x) => x.slug === this.slug()),
  );
  protected readonly previous = computed(() => this.categoryGuides()[this.currentIndex() - 1]);
  protected readonly next = computed(() => this.categoryGuides()[this.currentIndex() + 1]);
  constructor(
    route: ActivatedRoute,
    private title: Title,
    private meta: Meta,
    @Inject(DOCUMENT) private document: Document,
    @Inject(PLATFORM_ID) platformId: object,
  ) {
    this.isBrowser = isPlatformBrowser(platformId);
    route.paramMap.subscribe((params) => {
      this.category.set(params.get('category') ?? '');
      this.slug.set(params.get('slug') ?? '');
      const guide = this.guide();
      if (guide) this.updatePageMetadata(guide.title, guide.summary, guide.category, guide.slug);
      if (this.isBrowser && !route.snapshot.fragment) {
        window.scrollTo({ top: 0, behavior: 'auto' });
      }
    });
    route.fragment.subscribe((fragment) => this.pendingFragment.set(fragment));
  }

  private updatePageMetadata(
    title: string,
    description: string,
    category: string,
    slug: string,
  ): void {
    const pageTitle = `${title} — BlueTusk`;
    const canonicalUrl = `https://bluetusk.io/documentation/${category}/${slug}`;
    this.title.setTitle(pageTitle);
    this.meta.updateTag({ name: 'description', content: description });
    this.meta.updateTag({ name: 'robots', content: 'index, follow, max-snippet:-1' });
    this.meta.updateTag({ property: 'og:type', content: 'article' });
    this.meta.updateTag({ property: 'og:title', content: pageTitle });
    this.meta.updateTag({ property: 'og:description', content: description });
    this.meta.updateTag({ property: 'og:url', content: canonicalUrl });
    this.meta.updateTag({ name: 'twitter:title', content: pageTitle });
    this.meta.updateTag({ name: 'twitter:description', content: description });

    let canonical = this.document.head.querySelector<HTMLLinkElement>('link[rel="canonical"]');
    if (!canonical) {
      canonical = this.document.createElement('link');
      canonical.rel = 'canonical';
      this.document.head.appendChild(canonical);
    }
    canonical.href = canonicalUrl;
  }

  ngAfterViewChecked(): void {
    if (!this.isBrowser) return;
    const guide = this.guide();
    const container = this.guideContent?.nativeElement;
    if (!guide || !container) return;
    const headings = container.querySelectorAll<HTMLElement>('h1, h2, h3, h4, h5, h6');
    headings.forEach((heading, index) => {
      const id = guide.headings[index]?.id;
      if (id && heading.id !== id) heading.id = id;
    });

    const fragment = this.pendingFragment();
    const target = fragment ? document.getElementById(fragment) : null;
    if (!fragment || !target) return;

    this.pendingFragment.set(null);
    window.requestAnimationFrame(() => target.scrollIntoView({ block: 'start', behavior: 'auto' }));
  }

  protected async copyCode(code: string, event: MouseEvent): Promise<void> {
    const button = event.currentTarget as HTMLButtonElement;
    await navigator.clipboard.writeText(code);
    button.textContent = 'Copied';
    window.setTimeout(() => (button.textContent = 'Copy'), 1500);
  }
}
