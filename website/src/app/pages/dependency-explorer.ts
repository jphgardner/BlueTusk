import { Component, computed, signal } from '@angular/core';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatSelectModule } from '@angular/material/select';

const services = ['Storefront', 'Checkout', 'Payments', 'Inventory', 'PostgreSQL'] as const;
type Service = (typeof services)[number];
type Direction = 'dependencies' | 'dependents';
const edges: readonly [Service, Service][] = [
  ['Storefront', 'Checkout'],
  ['Checkout', 'Payments'],
  ['Checkout', 'Inventory'],
  ['Payments', 'PostgreSQL'],
  ['Inventory', 'PostgreSQL'],
];

@Component({
  selector: 'bt-dependency-explorer',
  imports: [MatFormFieldModule, MatIconModule, MatSelectModule],
  template: `
    <div class="dependency-explorer">
      <div>
        <h3>Try a dependency traversal</h3>
        <p>
          Each edge points from a service to a dependency. Select a service to see what it uses or
          what relies on it.
        </p>
        <ul class="example-edges" aria-label="Example connections">
          @for (edge of connections; track edge[0] + edge[1]) {
            <li>
              {{ edge[0] }} <mat-icon>arrow_forward</mat-icon>
              <span class="sr-only">depends on</span> {{ edge[1] }}
            </li>
          }
        </ul>
      </div>
      <div class="dependency-query">
        <div class="dependency-controls">
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Service</mat-label>
            <mat-select [value]="anchor()" (selectionChange)="anchor.set($event.value)">
              @for (service of services; track service) {
                <mat-option [value]="service">{{ service }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Direction</mat-label>
            <mat-select [value]="direction()" (selectionChange)="direction.set($event.value)">
              <mat-option value="dependencies">Dependencies</mat-option>
              <mat-option value="dependents">Dependents</mat-option>
            </mat-select>
          </mat-form-field>
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Maximum depth</mat-label>
            <mat-select [value]="depth()" (selectionChange)="depth.set($event.value)">
              @for (number of depths; track number) {
                <mat-option [value]="number">{{ number }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        </div>
        <div aria-live="polite">
          <h4>Reachable from {{ anchor() }}</h4>
          <ul class="traversal-results">
            @for (row of reachable(); track row.service) {
              <li>
                <strong>{{ row.service }}</strong
                ><span>{{ row.hops }} {{ row.hops === 1 ? 'hop' : 'hops' }}</span>
              </li>
            } @empty {
              <li>No services are reachable in this direction.</li>
            }
          </ul>
        </div>
      </div>
    </div>
  `,
})
export class DependencyExplorer {
  protected readonly connections = edges;
  protected readonly services = services;
  protected readonly depths = [1, 2, 3];
  protected readonly anchor = signal<Service>('Storefront');
  protected readonly direction = signal<Direction>('dependencies');
  protected readonly depth = signal(3);

  protected readonly reachable = computed(() => {
    const start = this.anchor();
    const visited = new Set<Service>([start]);
    const queue: { service: Service; hops: number }[] = [{ service: start, hops: 0 }];
    const [source, target] = this.direction() === 'dependencies' ? [0, 1] : [1, 0];
    for (let index = 0; index < queue.length; index++) {
      const { service, hops } = queue[index];
      if (hops >= this.depth()) continue;
      for (const edge of edges) {
        const neighbor = edge[target];
        if (edge[source] !== service || visited.has(neighbor)) continue;
        visited.add(neighbor);
        queue.push({ service: neighbor, hops: hops + 1 });
      }
    }
    return queue.slice(1);
  });
}
