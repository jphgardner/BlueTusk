import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { sourceUrl } from '../content/catalog';
import { SourceLink } from '../shared/technical-ui';

@Component({
  selector: 'bt-community-page',
  imports: [RouterLink, MatButtonModule, MatIconModule, SourceLink],
  template: `
    <section class="page-hero split-hero community-hero">
      <div>
        <span class="eyebrow"><i class="live-dot"></i> CONTRIBUTE</span>
        <h1>Bring a reproducer.<br /><em>Leave the project clearer.</em></h1>
        <p>
          Start with observable PostgreSQL behavior: what you ran, what happened, what you expected,
          and the smallest case that shows the difference. Discuss substantial changes before
          implementation.
        </p>
        <div class="hero-actions">
          <a
            mat-flat-button
            href="https://github.com/jphgardner/BlueTusk/issues"
            target="_blank"
            rel="noreferrer"
            class="primary-action"
            >Open GitHub Issues</a
          ><a
            mat-stroked-button
            href="https://github.com/jphgardner/BlueTusk/blob/main/CONTRIBUTING.md"
            target="_blank"
            rel="noreferrer"
            class="secondary-action"
            >Read CONTRIBUTING.md</a
          >
        </div>
      </div>
      <aside class="contribution-terminal">
        <small>LOCAL CONTRIBUTOR LOOP</small>
        @for (line of commands; track line.command) {
          <div>
            <span>{{ line.step }}</span
            ><code>{{ line.command }}</code>
          </div>
        }
        <p>Start with an issue. Keep secrets out of tests and commits.</p>
      </aside>
    </section>

    <section class="page-section">
      <header class="section-head">
        <div>
          <span>CONTRIBUTOR LOOP</span>
          <h2>Make the change easy to review.</h2>
        </div>
        <bt-source-link [href]="source('CONTRIBUTING.md')" label="CONTRIBUTING.md" />
      </header>
      <div class="timeline-flow contribution-flow">
        @for (step of flow; track step.title; let i = $index) {
          <article>
            <span>0{{ i + 1 }}</span
            ><mat-icon>{{ step.icon }}</mat-icon>
            <h3>{{ step.title }}</h3>
            <p>{{ step.body }}</p>
          </article>
        }
      </div>
    </section>

    <section class="page-section contribution-areas">
      <header class="section-head">
        <div>
          <span>START FROM EXPERIENCE</span>
          <h2>Work where you can prove behavior.</h2>
        </div>
        <p>Each area leads to its current product boundary and technical documentation.</p>
      </header>
      <div>
        @for (area of areas; track area.title) {
          <article>
            <mat-icon>{{ area.icon }}</mat-icon
            ><small>{{ area.kicker }}</small>
            <h3>{{ area.title }}</h3>
            <p>{{ area.body }}</p>
            <a [routerLink]="area.route">{{ area.action }} <mat-icon>arrow_forward</mat-icon></a>
          </article>
        }
      </div>
    </section>

    <section class="page-section contributor-contract">
      <div>
        <span class="section-kicker">REVIEW CONTRACT</span>
        <h2>Evidence travels with the change.</h2>
        <p>
          Keep the architecture clear, protect existing APIs, follow PostgreSQL behavior, and add
          tests that match the risk of the change.
        </p>
      </div>
      <div class="contract-list">
        @for (rule of rules; track rule.title) {
          <article>
            <mat-icon>check_circle</mat-icon>
            <div>
              <strong>{{ rule.title }}</strong
              ><span>{{ rule.body }}</span>
            </div>
          </article>
        }
      </div>
    </section>

    <section class="security-report">
      <mat-icon>security</mat-icon>
      <div>
        <span>VULNERABILITY REPORTING</span>
        <h2>Report security issues privately.</h2>
        <p>
          Do not open a public issue for a suspected vulnerability. Follow the repository security
          policy and contact the maintainers privately through the repository hosting platform. No
          unverified email or community channel is advertised.
        </p>
      </div>
      <a
        mat-stroked-button
        href="https://github.com/jphgardner/BlueTusk/blob/main/SECURITY.md"
        target="_blank"
        rel="noreferrer"
        class="secondary-action"
        >Read SECURITY.md</a
      >
    </section>
  `,
})
export class CommunityPage {
  protected readonly source = sourceUrl;
  protected readonly commands = [
    { step: '01', command: 'git clone https://github.com/jphgardner/BlueTusk.git' },
    { step: '02', command: 'dotnet build BlueTusk.slnx' },
    { step: '03', command: 'dotnet test BlueTusk.slnx' },
  ] as const;
  protected readonly flow = [
    {
      icon: 'search',
      title: 'Check and discuss',
      body: 'Search existing issues, then open one before substantial implementation work.',
    },
    {
      icon: 'account_tree',
      title: 'Keep the architecture clear',
      body: 'Put code in the right project and avoid breaking existing public APIs.',
    },
    {
      icon: 'science',
      title: 'Add the right tests',
      body: 'Add fast tests and, when needed, prove the behavior against a real PostgreSQL server.',
    },
    {
      icon: 'description',
      title: 'Update the supporting material',
      body: 'Keep examples, documentation, API records, and compatibility notes up to date.',
    },
  ] as const;
  protected readonly areas = [
    {
      icon: 'storage',
      kicker: 'PROVIDER',
      title: 'PostgreSQL behavior',
      body: 'Reproduce a connection, type, pooling, login, or data-handling problem.',
      route: '/provider',
      action: 'Explore provider',
    },
    {
      icon: 'extension',
      kicker: 'EXTENSIONS',
      title: 'Specialized packages',
      body: 'Add support for a PostgreSQL extension in ADO.NET, EF Core, and live tests.',
      route: '/extensions',
      action: 'Explore extensions',
    },
    {
      icon: 'stream',
      kicker: 'REAL TIME',
      title: 'Operational workloads',
      body: 'Test initial loads, replay, destination recovery, or long-running workloads.',
      route: '/real-time',
      action: 'Explore real time',
    },
    {
      icon: 'code',
      kicker: 'SAMPLES',
      title: 'Executable scenarios',
      body: 'Turn a real application pattern into a clear, maintainable example project.',
      route: '/documentation',
      action: 'Read guides',
    },
  ] as const;
  protected readonly rules = [
    {
      title: 'PostgreSQL is the specification',
      body: 'PostgreSQL behavior and its official documentation decide what is correct.',
    },
    {
      title: 'Public API changes are deliberate',
      body: 'API records and compatibility guidance must be updated with intentional public changes.',
    },
    {
      title: 'Tests match the risk',
      body: 'Choose unit, live, security, load, or long-running tests based on what could go wrong.',
    },
    {
      title: 'Secrets never enter the repository',
      body: 'Environment variables and isolated test infrastructure carry credentials.',
    },
  ] as const;
}
