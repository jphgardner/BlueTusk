import { Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { USE_CASES, type UseCase } from '../content/use-cases';
import { DependencyExplorer } from './dependency-explorer';

@Component({
  selector: 'bt-use-cases',
  imports: [DependencyExplorer, MatButtonModule, MatIconModule, RouterLink],
  template: `
    @if (current(); as item) {
      <header class="product-hero story-hero">
        <div class="site-width story-layout">
          <div>
            <nav class="breadcrumb" aria-label="Breadcrumb">
              <a routerLink="/use-cases">Use cases</a><span>{{ item.name }}</span>
            </nav>
            <h1>{{ item.headline }}</h1>
            <p>{{ item.summary }}</p>
            <a mat-flat-button [routerLink]="[]" fragment="implementation"
              >Explore the implementation <mat-icon iconPositionEnd>arrow_downward</mat-icon></a
            >
          </div>
          @if (item.image) {
            <img [src]="item.image" alt="" width="1100" height="620" fetchpriority="high" />
          }
        </div>
      </header>

      <section class="site-width section-space" id="implementation">
        <div class="section-heading"><h2>How it works</h2></div>
        <ol class="workflow-stages">
          @for (step of item.stages; track step.title) {
            <li>
              <mat-icon class="product-icon">{{ step.icon }}</mat-icon>
              <h3>{{ step.title }}</h3>
              <p>{{ step.detail }}</p>
              <a class="text-link" [routerLink]="'/documentation/' + step.guide"
                >Read the guide <mat-icon>arrow_forward</mat-icon></a
              >
            </li>
          }
        </ol>
      </section>

      @if (item.slug === 'service-topology') {
        <section
          class="site-width section-space example-application"
          aria-labelledby="explorer-heading"
        >
          <h2 id="explorer-heading">Explore a service map</h2>
          <bt-dependency-explorer />
        </section>
      }

      <section class="surface-section">
        <div class="site-width section-space grid gap-8 lg:grid-cols-[0.75fr_1fr]">
          <div>
            <h2>Try this in a test environment</h2>
            <p class="section-copy">
              Use your application’s PostgreSQL roles and package set when reproducing the workflow.
            </p>
            <a mat-stroked-button [href]="sourceLink(item)"
              >View source <mat-icon iconPositionEnd>arrow_outward</mat-icon></a
            >
          </div>
          <ul class="check-list">
            @for (check of item.checks; track check) {
              <li><mat-icon>check_circle</mat-icon> {{ check }}</li>
            }
          </ul>
        </div>
      </section>

      <section class="site-width section-space">
        <h2>Explore another use case</h2>
        <div class="grid gap-4 mt-5 sm:grid-cols-2 lg:grid-cols-3">
          @for (other of items; track other.slug) {
            @if (item.slug !== other.slug) {
              <a class="feature-tile linked-tile" [routerLink]="['/use-cases', other.slug]">
                <mat-icon class="product-icon">{{ other.icon }}</mat-icon>
                <h3>{{ other.name }}</h3>
                <p>{{ other.summary }}</p>
              </a>
            }
          }
        </div>
      </section>
    } @else {
      <header class="product-hero">
        <div class="site-width">
          <h1>See BlueTusk in an application.</h1>
          <p>
            Trace a workflow from PostgreSQL to the application UI. Each example includes the
            components, authorization boundaries, and behavior to verify.
          </p>
          <a mat-stroked-button routerLink="/architecture"
            >Explore the architecture <mat-icon iconPositionEnd>arrow_forward</mat-icon></a
          >
        </div>
      </header>
      <section class="site-width section-space" aria-labelledby="stories-title">
        <h2 id="stories-title">Choose a workflow</h2>
        <div class="grid gap-5 mt-5 md:grid-cols-2">
          @for (item of items; track item.slug) {
            <a class="story-card" [routerLink]="['/use-cases', item.slug]">
              @if (item.image) {
                <img [src]="item.image" alt="" width="1100" height="620" loading="lazy" />
              } @else {
                <div class="story-icon">
                  <mat-icon>{{ item.icon }}</mat-icon>
                </div>
              }
              <div class="story-card-content">
                <h3>{{ item.name }}</h3>
                <p>{{ item.summary }}</p>
                <span class="text-link"
                  >Explore {{ item.name.toLowerCase() }} <mat-icon>arrow_forward</mat-icon></span
                >
              </div>
            </a>
          }
        </div>
      </section>
    }
  `,
})
export class UseCasesPage {
  private readonly data = toSignal(inject(ActivatedRoute).data);
  protected readonly items = USE_CASES;
  protected readonly current = computed(() => this.data()?.['useCase'] as UseCase | undefined);

  protected sourceLink(item: UseCase): string {
    return 'https://github.com/jphgardner/BlueTusk/tree/main/' + item.source;
  }
}
