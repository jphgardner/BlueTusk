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
        <span class="eyebrow"><i class="live-dot"></i> BUILD IN THE OPEN</span>
        <h1>Put BlueTusk under a <em>real workload.</em></h1>
        <p>
          The useful contribution is evidence: a reproducible issue, a missing PostgreSQL behavior,
          an extension contract, a workload sample, or a gate that makes confidence measurable.
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
            routerLink="/documentation/operations/contributing"
            class="secondary-action"
            >Contribution guide</a
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
          <span>CONTRIBUTION PATH</span>
          <h2>Move from observation to evidence.</h2>
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
          <span>WHERE WORK HELPS</span>
          <h2>Contribute at a real boundary.</h2>
        </div>
        <p>These areas map to implemented repository surfaces and explicit roadmap work.</p>
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
        <span class="section-kicker">ENGINEERING CONTRACT</span>
        <h2>Changes carry their proof.</h2>
        <p>
          Dependency direction, API compatibility, native PostgreSQL behavior, and test scope are
          repository rules—not review-time surprises.
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
      title: 'Respect the layers',
      body: 'Keep project dependency direction and public API compatibility explicit.',
    },
    {
      icon: 'science',
      title: 'Add focused evidence',
      body: 'Pair the change with fast tests and live PostgreSQL coverage where behavior requires it.',
    },
    {
      icon: 'description',
      title: 'Update the contract',
      body: 'Keep samples, documentation, freezes, and compatibility records synchronized.',
    },
  ] as const;
  protected readonly areas = [
    {
      icon: 'storage',
      kicker: 'PROVIDER',
      title: 'PostgreSQL behavior',
      body: 'Reproduce a protocol, type, pooling, auth, or data-path gap.',
      route: '/provider',
      action: 'Explore provider',
    },
    {
      icon: 'extension',
      kicker: 'EXTENSIONS',
      title: 'Specialized packages',
      body: 'Add a codec, descriptor, translation, and live gate through the extension SDK.',
      route: '/extensions',
      action: 'Explore extensions',
    },
    {
      icon: 'stream',
      kicker: 'REAL TIME',
      title: 'Operational workloads',
      body: 'Exercise snapshots, replay, destination recovery, or endurance harnesses.',
      route: '/real-time',
      action: 'Explore real time',
    },
    {
      icon: 'code',
      kicker: 'SAMPLES',
      title: 'Executable scenarios',
      body: 'Turn a real application shape into a minimal, maintainable repository sample.',
      route: '/documentation',
      action: 'Read guides',
    },
  ] as const;
  protected readonly rules = [
    {
      title: 'PostgreSQL is the specification',
      body: 'Server behavior and documented wire semantics decide correctness.',
    },
    {
      title: 'Public API changes are deliberate',
      body: 'Freeze files and compatibility guidance move with intentional surface changes.',
    },
    {
      title: 'Tests match the risk',
      body: 'Unit, live, topology, security, stress, or endurance coverage is selected by behavior.',
    },
    {
      title: 'Secrets never enter the repository',
      body: 'Environment variables and isolated test infrastructure carry credentials.',
    },
  ] as const;
}
