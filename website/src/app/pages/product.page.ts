import { Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { PRODUCTS } from '../content/catalog';
import { PRODUCT_DETAILS } from '../content/product-details';

@Component({
  selector: 'bt-product-page',
  imports: [RouterLink, MatButtonModule, MatIconModule],
  template: `
    @if (current(); as product) {
      <header class="product-hero">
        <div class="site-width">
          <nav class="breadcrumb" aria-label="Breadcrumb">
            <a routerLink="/platform">Platform</a><span>{{ product.name }}</span>
          </nav>
          <h1>{{ product.title }}</h1>
          <p>{{ product.description }}</p>
          <div class="flex flex-wrap items-center gap-4">
            <a mat-flat-button [routerLink]="'/documentation/' + product.start"
              >Open the guide <mat-icon iconPositionEnd>arrow_forward</mat-icon></a
            >
            @if (product.package) {
              <code class="package-label">{{ product.package }}</code>
            }
          </div>
        </div>
      </header>
      <section class="site-width section-space" aria-labelledby="product-tasks">
        <h2 id="product-tasks">What you can build</h2>
        <div class="task-list">
          @for (task of product.tasks; track task.guide) {
            <a [routerLink]="'/documentation/' + task.guide">
              <div>
                <h3>{{ task.title }}</h3>
                <p>{{ task.description }}</p>
              </div>
              <mat-icon>arrow_forward</mat-icon>
            </a>
          }
        </div>
        <a class="text-link" routerLink="/documentation" [fragment]="product.category"
          >Browse {{ product.name }} guides <mat-icon>arrow_forward</mat-icon></a
        >
      </section>
      @if (details(); as extra) {
        <section class="surface-section">
          <div class="site-width section-space">
            <h2>{{ extra.title }}</h2>
            <p class="section-copy">{{ extra.summary }}</p>
            <div class="grid gap-4 lg:grid-cols-3">
              @for (item of extra.links; track item.title) {
                <a
                  class="feature-tile linked-tile"
                  [routerLink]="item.route.split('#')[0]"
                  [fragment]="item.route.split('#')[1]"
                >
                  <mat-icon class="product-icon">{{ item.icon }}</mat-icon>
                  <h3>{{ item.title }}</h3>
                  <p>{{ item.detail }}</p>
                </a>
              }
            </div>
          </div>
        </section>
      }
      <section class="site-width section-space flex flex-wrap items-center justify-between gap-5">
        <div>
          <h2>See the pieces in an application</h2>
          <p class="section-copy">
            Follow the data and authorization boundaries in an example workflow.
          </p>
        </div>
        <a mat-stroked-button routerLink="/use-cases"
          >Browse use cases <mat-icon iconPositionEnd>arrow_forward</mat-icon></a
        >
      </section>
    } @else {
      <header class="product-hero">
        <div class="site-width">
          <h1>The BlueTusk platform</h1>
          <p>
            Start with PostgreSQL access from .NET. Add change delivery, graphs, and data services
            as your application needs them.
          </p>
          <a mat-flat-button routerLink="/packages"
            >Choose packages <mat-icon iconPositionEnd>arrow_forward</mat-icon></a
          >
        </div>
      </header>
      <section
        class="site-width section-space grid gap-4 sm:grid-cols-2 lg:grid-cols-3"
        aria-label="Products"
      >
        @for (product of products; track product.path) {
          <a class="feature-tile" [routerLink]="['/', product.path]">
            <mat-icon class="product-icon">{{ product.icon }}</mat-icon>
            <h2>{{ product.name }}</h2>
            <p>{{ product.description }}</p>
            <span class="text-link"
              >Explore {{ product.name }} <mat-icon>arrow_forward</mat-icon></span
            >
          </a>
        }
      </section>
      <section class="surface-section">
        <div class="site-width section-space flex flex-wrap items-center justify-between gap-5">
          <div>
            <h2>Follow a query or commit</h2>
            <p class="section-copy">
              Explore connection ownership, change delivery, and the authorized path from PostgreSQL
              to a client.
            </p>
          </div>
          <a mat-flat-button routerLink="/architecture"
            >Trace the architecture <mat-icon iconPositionEnd>arrow_forward</mat-icon></a
          >
        </div>
      </section>
    }
  `,
})
export class ProductPage {
  private readonly data = toSignal(inject(ActivatedRoute).data);
  protected readonly products = PRODUCTS;
  protected readonly details = computed(() => PRODUCT_DETAILS[this.current()?.path ?? '']);
  protected readonly current = computed(
    () => this.data()?.['product'] as (typeof PRODUCTS)[number] | undefined,
  );
}
