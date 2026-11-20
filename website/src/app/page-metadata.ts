import { DOCUMENT } from '@angular/common';
import { Injectable, inject } from '@angular/core';
import { Meta, Title } from '@angular/platform-browser';
import { RouterStateSnapshot, TitleStrategy } from '@angular/router';
import { guideRoute } from './content/documentation-navigation';
import type { GuideManifestEntry } from './content/models';

@Injectable()
export class PageMetadata extends TitleStrategy {
  private readonly document = inject(DOCUMENT);
  private readonly title = inject(Title);
  private readonly meta = inject(Meta);

  override updateTitle(snapshot: RouterStateSnapshot): void {
    let route = snapshot.root;
    while (route.firstChild) route = route.firstChild;
    const guide = route.data['guide'] as GuideManifestEntry | undefined;
    const missing =
      (route.routeConfig?.path === 'documentation/:category/:slug' && !guide) ||
      route.routeConfig?.path === '**';
    const title = guide
      ? guide.title + ' · BlueTusk'
      : missing
        ? 'Page not found · BlueTusk'
        : (this.buildTitle(snapshot) ?? 'BlueTusk');
    const description = guide?.summary ?? route.data['description'] ?? 'Find a BlueTusk guide.';
    this.title.setTitle(title);
    this.meta.updateTag({ name: 'description', content: description });
    this.meta.updateTag({ name: 'robots', content: missing ? 'noindex, follow' : 'index, follow' });
    this.meta.updateTag({ property: 'og:title', content: title });
    this.meta.updateTag({ property: 'og:description', content: description });

    const previous = this.document.querySelector('link[rel="canonical"]');
    if (missing) {
      previous?.remove();
      this.meta.removeTag('property="og:url"');
      return;
    }
    const pathname = guide ? guideRoute(guide) : snapshot.url.split(/[?#]/)[0];
    const url = 'https://bluetusk.io' + pathname;
    const canonical = previous ?? this.document.createElement('link');
    canonical.setAttribute('rel', 'canonical');
    canonical.setAttribute('href', url);
    if (!previous) this.document.head.appendChild(canonical);
    this.meta.updateTag({ property: 'og:url', content: url });
  }
}
