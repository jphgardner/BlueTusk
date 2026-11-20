import { Component, computed, inject, linkedSignal, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { map } from 'rxjs';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatSelectModule } from '@angular/material/select';
import { PACKAGES, RELEASE_VERSION } from '../../generated/packages.generated';
import {
  PACKAGE_FAMILIES,
  PACKAGE_KIND_LABELS,
  installCommand,
  packageRegistry,
} from '../content/packages';

@Component({
  selector: 'bt-packages',
  imports: [MatButtonModule, MatFormFieldModule, MatIconModule, MatSelectModule, RouterLink],
  template: `
    <header class="product-hero">
      <div class="site-width">
        <h1>BlueTusk {{ version }} packages</h1>
        <p>
          Find the .NET libraries, tools, and browser clients for your application. Copy a command
          to install the matching version from your package source.
        </p>
        <a mat-stroked-button routerLink="/compatibility"
          >Check compatibility <mat-icon iconPositionEnd>arrow_forward</mat-icon></a
        >
      </div>
    </header>

    <section class="site-width section-space" aria-labelledby="catalog-title">
      <h2 id="catalog-title">Find a package</h2>
      <div class="catalog-filters">
        <label class="search-field catalog-search">
          <mat-icon>search</mat-icon>
          <span class="sr-only">Search packages</span>
          <input
            type="search"
            [value]="query()"
            (input)="query.set($any($event.target).value)"
            placeholder="Search packages by name or feature"
          />
        </label>
        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>Product family</mat-label>
          <mat-select [value]="family()" (selectionChange)="family.set($event.value)">
            <mat-option value="">All families</mat-option>
            @for (name of families; track name) {
              <mat-option [value]="name">{{ name }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>Package type</mat-label>
          <mat-select [value]="kind()" (selectionChange)="kind.set($event.value)">
            <mat-option value="">All package types</mat-option>
            @for (entry of kinds; track entry.key) {
              <mat-option [value]="entry.key">{{ entry.label }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      </div>

      <p class="catalog-count" role="status">{{ matches().length }} packages</p>
      <span class="sr-only" role="status">{{ feedback() }}</span>
      <div class="catalog-list">
        @for (pkg of matches(); track pkg.id) {
          <article class="catalog-row">
            <div>
              <div class="catalog-type">{{ pkg.family }} · {{ labels[pkg.kind] }}</div>
              <h3>
                <a [href]="packageRegistry(pkg)">{{ pkg.id }} <mat-icon>arrow_outward</mat-icon></a>
              </h3>
              @if (pkg.description) {
                <p>{{ pkg.description }}</p>
              }
            </div>
            <details class="catalog-install">
              <summary>Install <mat-icon>expand_more</mat-icon></summary>
              <div class="example-panel">
                <div class="code-toolbar">
                  <span>{{ version }}</span>
                  <button type="button" mat-button (click)="copy(installCommand(pkg), pkg.id)">
                    {{ copied() === pkg.id ? 'Copied' : 'Copy command' }}
                  </button>
                </div>
                <pre tabindex="0"><code>{{ installCommand(pkg) }}</code></pre>
              </div>
            </details>
          </article>
        } @empty {
          <div class="notice">
            <p>No package matches these filters.</p>
            <button type="button" mat-stroked-button (click)="clear()">Reset filters</button>
          </div>
        }
      </div>
    </section>
  `,
})
export class PackagesPage {
  private readonly route = inject(ActivatedRoute);
  protected readonly version = RELEASE_VERSION;
  protected readonly families = PACKAGE_FAMILIES;
  protected readonly labels = PACKAGE_KIND_LABELS;
  protected readonly kinds = Object.entries(PACKAGE_KIND_LABELS).map(([key, label]) => ({
    key,
    label,
  }));
  private readonly selectedFamily = toSignal(
    this.route.queryParamMap.pipe(map((params) => params.get('family') ?? '')),
    { initialValue: '' },
  );
  protected readonly family = linkedSignal(() =>
    this.families.includes(this.selectedFamily()) ? this.selectedFamily() : '',
  );
  protected readonly kind = signal('');
  protected readonly query = signal('');
  protected readonly copied = signal('');
  protected readonly feedback = signal('');
  protected readonly installCommand = installCommand;
  protected readonly packageRegistry = packageRegistry;

  protected readonly matches = computed(() => {
    const family = this.family();
    const kind = this.kind();
    const words = this.query().trim().toLowerCase().split(/\s+/).filter(Boolean);
    return PACKAGES.filter((pkg) => {
      if ((family && family !== pkg.family) || (kind && kind !== pkg.kind)) return false;
      const content = [pkg.id, pkg.family, pkg.description, this.labels[pkg.kind]]
        .join(' ')
        .toLowerCase();
      return words.every((word) => content.includes(word));
    });
  });

  protected clear(): void {
    this.query.set('');
    this.family.set('');
    this.kind.set('');
  }

  protected async copy(command: string, id: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(command);
      this.copied.set(id);
      this.feedback.set('Install command copied.');
    } catch {
      this.feedback.set('Clipboard unavailable. Select the command to copy it.');
    }
  }
}
