import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

@Component({
  selector: 'bt-community-page',
  imports: [RouterLink, MatButtonModule, MatIconModule],
  template: `
    <header class="product-hero">
      <div class="site-width">
        <h1>Build with BlueTusk</h1>
        <p>Read the source, get help with an issue, or contribute an improvement.</p>
        <a mat-flat-button href="https://github.com/jphgardner/BlueTusk"
          >View source <mat-icon iconPositionEnd>arrow_outward</mat-icon></a
        >
      </div>
    </header>
    <section class="site-width section-space">
      <h2>Get help</h2>
      <div class="task-list">
        <a routerLink="/documentation/operations/troubleshooting"
          ><div>
            <h3>Troubleshoot a problem</h3>
            <p>Check connection, query, and replication symptoms before reporting an issue.</p>
          </div>
          <mat-icon>arrow_forward</mat-icon></a
        >
        <a href="https://github.com/jphgardner/BlueTusk/issues"
          ><div>
            <h3>Report a bug</h3>
            <p>
              Include the package versions, PostgreSQL version, and a minimal reproduction. Remove
              credentials and personal data.
            </p>
          </div>
          <mat-icon>arrow_outward</mat-icon></a
        >
        <a href="https://github.com/jphgardner/BlueTusk/blob/main/SECURITY.md"
          ><div>
            <h3>Report a security issue privately</h3>
            <p>Follow the project’s vulnerability disclosure instructions.</p>
          </div>
          <mat-icon>arrow_outward</mat-icon></a
        >
        <a href="https://github.com/jphgardner/BlueTusk/blob/main/CONTRIBUTING.md"
          ><div>
            <h3>Contribute</h3>
            <p>Set up a development checkout and propose a change.</p>
          </div>
          <mat-icon>arrow_outward</mat-icon></a
        >
      </div>
    </section>
  `,
})
export class CommunityPage {}
