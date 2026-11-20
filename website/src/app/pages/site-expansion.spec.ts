import { TestBed } from '@angular/core/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { MatSelectHarness } from '@angular/material/select/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { PACKAGES, RELEASE_VERSION } from '../../generated/packages.generated';
import { installCommand } from '../content/packages';
import { routes } from '../app.routes';
import { DependencyExplorer } from './dependency-explorer';

describe('BlueTusk 1.2 website', () => {
  it('uses the same release version for each installation command', () => {
    expect(RELEASE_VERSION).toBe('1.2.0');
    expect(PACKAGES.filter(({ kind }) => kind === 'browser').length).toBe(6);
    expect(PACKAGES.filter(({ kind }) => kind !== 'browser').length).toBeGreaterThanOrEqual(99);
    expect(PACKAGES.every((pkg) => installCommand(pkg).endsWith(RELEASE_VERSION))).toBe(true);
  });

  it('opens a package family from a linked URL and filters its results', async () => {
    TestBed.configureTestingModule({ providers: [provideRouter(routes)] });
    const harness = await RouterTestingHarness.create('/packages?family=Live');
    const host = harness.routeNativeElement!;
    const expected = PACKAGES.filter(({ family }) => family === 'Live');
    expect(host.querySelectorAll('.catalog-row').length).toBe(expected.length);
    expect(host.querySelector('h1')?.textContent).toContain('1.2.0');

    const field = host.querySelector<HTMLInputElement>('input[type="search"]')!;
    field.value = 'svelte';
    field.dispatchEvent(new Event('input'));
    harness.fixture.detectChanges();
    expect(
      [...host.querySelectorAll('.catalog-row h3')].map((element) => element.textContent?.trim()),
    ).toEqual([expect.stringContaining('@bluetusk/live-svelte')]);
  });

  it('calculates shortest hop counts and lets a user traverse in either direction', async () => {
    TestBed.configureTestingModule({ imports: [DependencyExplorer] });
    const fixture = TestBed.createComponent(DependencyExplorer);
    fixture.detectChanges();
    const results = () =>
      Array.from(
        (fixture.nativeElement as HTMLElement).querySelectorAll('.traversal-results li'),
      ).map(({ textContent }) => textContent?.trim());
    expect(results()).toHaveLength(4);
    expect(results().at(-1)).toContain('PostgreSQL');
    expect(results().at(-1)).toContain('3 hops');

    const loader = TestbedHarnessEnvironment.loader(fixture);
    const [anchor, direction, depth] = await loader.getAllHarnesses(MatSelectHarness);
    await anchor.clickOptions({ text: 'PostgreSQL' });
    await direction.clickOptions({ text: 'Dependents' });
    await depth.clickOptions({ text: '1' });
    expect(results()).toHaveLength(2);
    expect(results()[0]).toContain('Payments');
    expect(results()[1]).toContain('Inventory');
  });
});
