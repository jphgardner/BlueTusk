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
import type { GuideManifestEntry } from '../content/models';
import {
  DOCUMENTATION_JOURNEYS,
  DOCUMENTATION_SECTIONS,
  documentationPrimerFor,
  documentationSectionFor,
  guideRoute,
} from '../content/documentation-navigation';

@Component({
  selector: 'bt-guide-page',
  imports: [RouterLink, MatIconModule],
  template: `
    @if (guide(); as current) {
      <div class="guide-shell">
        <aside class="guide-sidebar">
          <a routerLink="/documentation" class="back-link"
            ><mat-icon>arrow_back</mat-icon>Documentation home</a
          >
          <small class="guide-desktop-index">BROWSE DOCUMENTATION</small>
          <nav class="guide-desktop-index guide-section-index" aria-label="Documentation sections">
            @for (section of sections; track section.id) {
              <a
                routerLink="/documentation"
                [fragment]="section.id"
                [class.active]="section.id === currentSection().id"
              >
                <mat-icon>{{ section.icon }}</mat-icon>
                <span>{{ section.label }}</span>
              </a>
            }
          </nav>
          <small class="guide-desktop-index guide-topic-label">THIS TOPIC</small>
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
            <nav class="guide-breadcrumb" aria-label="Breadcrumb">
              <a routerLink="/documentation">Documentation</a>
              <mat-icon>chevron_right</mat-icon>
              <a routerLink="/documentation" [fragment]="currentSection().id">{{
                currentSection().label
              }}</a>
              <mat-icon>chevron_right</mat-icon>
              <span>{{ current.categoryLabel }}</span>
            </nav>
            <h1>{{ current.title }}</h1>
            <p>{{ current.summary }}</p>
            <div class="guide-meta">
              <span>{{ current.readMinutes }} min read</span
              ><span>{{ currentSection().label }}</span>
            </div>
            <a [href]="current.sourceUrl" target="_blank" rel="noreferrer"
              ><mat-icon>code</mat-icon>View source on GitHub<mat-icon>open_in_new</mat-icon></a
            >
          </header>
          <section class="guide-orientation" aria-label="How to use this guide">
            <div>
              <small>USE THIS GUIDE WHEN</small>
              <p>{{ primer().useWhen }}</p>
            </div>
            <div>
              <small>BEFORE YOU START</small>
              <p>{{ primer().beforeYouStart }}</p>
            </div>
            @if (quickHeadings().length) {
              <nav aria-label="Fast path through this guide">
                <small>FAST PATH</small>
                @for (heading of quickHeadings(); track heading.id) {
                  <a [routerLink]="[]" [fragment]="heading.id"
                    >{{ heading.text }}<mat-icon>arrow_downward</mat-icon></a
                  >
                }
              </nav>
            }
          </section>
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
          @if (journey(); as path) {
            <aside class="guide-journey" aria-labelledby="guide-journey-title">
              <header>
                <div>
                  <small>GUIDED PATH</small>
                  <h2 id="guide-journey-title">{{ path.title }}</h2>
                </div>
                <mat-icon>{{ path.icon }}</mat-icon>
              </header>
              <p>{{ path.description }}</p>
              <ol>
                @for (step of path.steps; track step.route; let stepIndex = $index) {
                  <li [class.current]="step.route === currentRoute()">
                    @if (step.route === currentRoute()) {
                      <span>
                        <small>{{ stepIndex + 1 }} · YOU ARE HERE</small>
                        <strong>{{ step.title }}</strong>
                      </span>
                    } @else {
                      <a [routerLink]="step.route">
                        <small>{{ stepIndex + 1 }} · {{ step.label }}</small>
                        <strong>{{ step.title }}</strong>
                        <mat-icon>arrow_forward</mat-icon>
                      </a>
                    }
                  </li>
                }
              </ol>
            </aside>
          }
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
  protected readonly guide = signal<GuideManifestEntry | undefined>(undefined);
  protected readonly sections = DOCUMENTATION_SECTIONS;
  protected readonly currentSection = computed(() => {
    const current = this.guide();
    return current ? documentationSectionFor(current) : DOCUMENTATION_SECTIONS[0];
  });
  protected readonly currentRoute = computed(() => {
    const current = this.guide();
    return current ? guideRoute(current) : '';
  });
  protected readonly primer = computed(() => {
    const current = this.guide();
    return documentationPrimerFor(current ?? { category: 'getting-started', listed: true });
  });
  protected readonly quickHeadings = computed(() =>
    (this.guide()?.headings ?? []).filter((heading) => heading.level === 2).slice(0, 3),
  );
  protected readonly journey = computed(() =>
    DOCUMENTATION_JOURNEYS.find((path) =>
      path.steps.some((step) => step.route === this.currentRoute()),
    ),
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
      if (this.isBrowser && !route.snapshot.fragment) {
        window.scrollTo({ top: 0, behavior: 'auto' });
      }
    });
    route.data.subscribe((data) => {
      const guide = data['guide'] as GuideManifestEntry | undefined;
      this.guide.set(guide);
      if (guide) this.updatePageMetadata(guide.title, guide.summary, guide.category, guide.slug);
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
