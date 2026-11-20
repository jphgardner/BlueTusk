import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';

@Component({
  selector: 'bt-not-found',
  imports: [RouterLink, MatButtonModule],
  template: `
    <div class="site-width section-space">
      <h1>Page not found</h1>
      <p class="my-4">Use documentation search or browse the guide index to find the topic.</p>
      <a mat-flat-button routerLink="/documentation">Browse documentation</a>
    </div>
  `,
})
export class NotFoundPage {}
