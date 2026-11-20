import { Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import type { Resource } from '../content/resources';

@Component({
  selector: 'bt-resource',
  imports: [MatButtonModule, MatIconModule, RouterLink],
  template: `
    <header class="product-hero">
      <div class="site-width">
        <h1>{{ resource().title }}</h1>
        <p>{{ resource().description }}</p>
        <a
          mat-flat-button
          [routerLink]="path(resource().action.href)"
          [queryParams]="params(resource().action.href)"
        >
          {{ resource().action.label }} <mat-icon iconPositionEnd>arrow_forward</mat-icon>
        </a>
      </div>
    </header>
    <div class="site-width section-space">
      @for (section of resource().sections; track section.title) {
        <section class="resource-row">
          <div>
            <h2>{{ section.title }}</h2>
            <p>{{ section.description }}</p>
          </div>
          <ul>
            @for (link of section.links; track link.href) {
              <li>
                <a
                  [routerLink]="path(link.href)"
                  [fragment]="fragment(link.href)"
                  [queryParams]="params(link.href)"
                >
                  {{ link.label }} <mat-icon>arrow_forward</mat-icon>
                </a>
              </li>
            }
          </ul>
        </section>
      }
    </div>
  `,
})
export class ResourcePage {
  private readonly data = toSignal(inject(ActivatedRoute).data, { requireSync: true });
  protected readonly resource = computed(() => this.data()['resource'] as Resource);
  protected path(href: string): string {
    return href.split(/[?#]/)[0];
  }
  protected fragment(href: string): string | undefined {
    return href.split('#')[1];
  }
  protected params(href: string): Readonly<Record<string, string>> {
    const query = href.split('#')[0].split('?')[1];
    return Object.fromEntries(new URLSearchParams(query));
  }
}
