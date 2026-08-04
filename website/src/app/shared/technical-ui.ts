import { Component, input, signal } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

@Component({
  selector: 'bt-status',
  imports: [MatIconModule],
  template: `<span class="bt-status" [attr.data-stage]="stage()"><i></i>{{ label() }}</span>`,
})
export class StatusPill {
  readonly label = input.required<string>();
  readonly stage = input<string>('preview');
}

@Component({
  selector: 'bt-source-link',
  imports: [MatIconModule],
  template: `
    <a class="source-link" [href]="href()" target="_blank" rel="noreferrer">
      <mat-icon>description</mat-icon>{{ label() }}<mat-icon>open_in_new</mat-icon>
    </a>
  `,
})
export class SourceLink {
  readonly href = input.required<string>();
  readonly label = input('View source evidence');
}

@Component({
  selector: 'bt-code-panel',
  imports: [MatIconModule],
  template: `
    <section class="mini-console" aria-label="Code example">
      <header>
        <span><i></i><i></i><i></i></span><strong>{{ file() }}</strong>
        <button type="button" (click)="copy()" [attr.aria-label]="'Copy ' + file()">
          <mat-icon>{{ copied() ? 'check' : 'content_copy' }}</mat-icon
          >{{ copied() ? 'Copied' : 'Copy' }}
        </button>
      </header>
      <pre><code>{{ code() }}</code></pre>
    </section>
  `,
})
export class CodePanel {
  readonly file = input.required<string>();
  readonly code = input.required<string>();
  readonly copied = signal(false);

  async copy(): Promise<void> {
    await navigator.clipboard.writeText(this.code());
    this.copied.set(true);
    window.setTimeout(() => this.copied.set(false), 1500);
  }
}
