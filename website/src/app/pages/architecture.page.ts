import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTabsModule } from '@angular/material/tabs';

@Component({
  selector: 'bt-architecture',
  imports: [MatButtonModule, MatIconModule, MatTabsModule, RouterLink],
  template: `
    <header class="product-hero">
      <div class="site-width">
        <h1>How BlueTusk fits together</h1>
        <p>
          Follow a query, a committed transaction, or a graph refresh. PostgreSQL supplies storage
          and authorization. The .NET application chooses the data and delivery paths it needs.
        </p>
        <a mat-stroked-button routerLink="/documentation/getting-started/architecture"
          >Read the architecture guide <mat-icon iconPositionEnd>arrow_forward</mat-icon></a
        >
      </div>
    </header>
    <section class="site-width section-space" aria-labelledby="architecture-flows">
      <h2 id="architecture-flows">Trace a data path</h2>
      <div class="architecture-flows">
        <mat-tab-group animationDuration="0ms">
          @for (flow of flows; track flow.name) {
            <mat-tab [label]="flow.name">
              <div class="flow-panel">
                <p>{{ flow.description }}</p>
                <ol class="workflow-stages">
                  @for (step of flow.steps; track step.title) {
                    <li>
                      <mat-icon class="product-icon">{{ step.icon }}</mat-icon>
                      <h3>{{ step.title }}</h3>
                      <p>{{ step.detail }}</p>
                    </li>
                  }
                </ol>
                <a class="text-link" [routerLink]="flow.href"
                  >{{ flow.action }} <mat-icon>arrow_forward</mat-icon></a
                >
              </div>
            </mat-tab>
          }
        </mat-tab-group>
      </div>
    </section>
    <section class="surface-section">
      <div class="site-width section-space">
        <h2>Separate execution and delivery</h2>
        <div class="grid gap-4 mt-5 md:grid-cols-3">
          <a class="feature-tile linked-tile" routerLink="/documentation/provider/pooling"
            ><mat-icon class="product-icon">storage</mat-icon>
            <h3>Connection ownership</h3>
            <p>
              The application owns a long-lived data source. Commands and logical connections borrow
              a physical session as needed.
            </p></a
          >
          <a class="feature-tile linked-tile" routerLink="/documentation/real-time/contracts"
            ><mat-icon class="product-icon">save</mat-icon>
            <h3>Durable progress</h3>
            <p>Persist destination work or replay before acknowledging a source delivery.</p></a
          >
          <a class="feature-tile linked-tile" routerLink="/documentation/operations/security"
            ><mat-icon class="product-icon">lock</mat-icon>
            <h3>Authorization</h3>
            <p>
              Derive subscriber scope in the application and enforce database policies on
              authoritative reads.
            </p></a
          >
        </div>
      </div>
    </section>
  `,
})
export class ArchitecturePage {
  protected readonly flows = [
    {
      name: 'Query a database',
      description:
        'Use the same PostgreSQL database from ADO.NET commands or EF Core queries and migrations.',
      href: '/provider',
      action: 'Explore the provider',
      steps: [
        {
          title: '.NET application',
          detail: 'Build a parameterized command or configure a DbContext.',
          icon: 'terminal',
        },
        {
          title: 'BlueTusk provider',
          detail: 'Resolve PostgreSQL types and borrow a session from the data source.',
          icon: 'sync_alt',
        },
        {
          title: 'PostgreSQL',
          detail: 'Execute against the application role and return typed results.',
          icon: 'storage',
        },
      ],
    },
    {
      name: 'Deliver a commit',
      description:
        'Capture a complete source transaction and persist the downstream effect before advancing.',
      href: '/real-time/streams',
      action: 'Explore Streams',
      steps: [
        {
          title: 'Application write',
          detail: 'Commit relational changes in PostgreSQL.',
          icon: 'edit_square',
        },
        {
          title: 'Streams',
          detail: 'Decode logical replication and deliver the transaction in order.',
          icon: 'sensors',
        },
        {
          title: 'Destination',
          detail: 'Apply the transaction and durably record its checkpoint.',
          icon: 'layers',
        },
      ],
    },
    {
      name: 'Refresh a graph',
      description: 'Maintain an authorized result after relevant vertex or edge changes.',
      href: '/documentation/real-time/continuous-graph',
      action: 'Explore Continuous Graph',
      steps: [
        {
          title: 'Relevant commit',
          detail: 'A tracked vertex or edge changes in PostgreSQL.',
          icon: 'hub',
        },
        {
          title: 'Authorized evaluation',
          detail: 'Read affected keys when proven safe, or repair from the full authorized result.',
          icon: 'verified_user',
        },
        {
          title: 'Live update',
          detail: 'Persist replay and emit a keyed change to the subscriber.',
          icon: 'notifications_active',
        },
      ],
    },
  ];
}
