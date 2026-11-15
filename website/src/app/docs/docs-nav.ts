import { Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { GUIDES } from '../../generated/guides.generated';
import { DOC_TOPICS, guideRoute } from '../content/documentation-navigation';

@Component({
  selector: 'bt-docs-nav',
  imports: [RouterLink, MatIconModule],
  template: `
    <nav aria-label="Documentation topics">
      <a class="docs-nav-home" routerLink="/documentation">Documentation home</a>
      @for (topic of topics; track topic.id) {
        @if (category() === topic.id) {
          <div class="docs-nav-current">
            <a class="docs-nav-topic" routerLink="/documentation" [fragment]="topic.id"
              ><mat-icon>{{ topic.icon }}</mat-icon> {{ topic.label }}</a
            >
            @for (guide of guides; track guide.slug + guide.category) {
              @if (guide.category === topic.id) {
                <a
                  class="docs-nav-guide"
                  [attr.aria-current]="guide.slug === activeSlug() ? 'page' : null"
                  [routerLink]="guideRoute(guide)"
                  >{{ guide.title }}</a
                >
              }
            }
          </div>
        } @else {
          <a class="docs-nav-topic" routerLink="/documentation" [fragment]="topic.id"
            ><mat-icon>{{ topic.icon }}</mat-icon> {{ topic.label }}</a
          >
        }
      }
    </nav>
  `,
})
export class DocsNav {
  readonly category = input('');
  readonly activeSlug = input('');
  protected readonly topics = DOC_TOPICS;
  protected readonly guides = GUIDES;
  protected readonly guideRoute = guideRoute;
}
