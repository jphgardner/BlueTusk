import { PACKAGES, RELEASE_VERSION } from '../../generated/packages.generated';

export type PackageKind = 'library' | 'tool' | 'template' | 'browser';

export interface PackageRecord {
  readonly id: string;
  readonly family: string;
  readonly kind: PackageKind;
  readonly description?: string;
  readonly source: string;
}

export const PACKAGE_KIND_LABELS: Readonly<Record<PackageKind, string>> = {
  library: '.NET library',
  tool: '.NET tool',
  template: '.NET template',
  browser: 'Browser client',
};

export const PACKAGE_FAMILIES = [...new Set(PACKAGES.map(({ family }) => family))];

export function installCommand(pkg: PackageRecord): string {
  switch (pkg.kind) {
    case 'browser':
      return 'npm install --save-exact ' + pkg.id + '@' + RELEASE_VERSION;
    case 'tool':
      return 'dotnet tool install --global ' + pkg.id + ' --version ' + RELEASE_VERSION;
    case 'template':
      return 'dotnet new install ' + pkg.id + '::' + RELEASE_VERSION;
    case 'library':
      return 'dotnet add package ' + pkg.id + ' --version ' + RELEASE_VERSION;
  }
}

export function packageRegistry(pkg: PackageRecord): string {
  return pkg.kind === 'browser'
    ? 'https://www.npmjs.com/package/' + pkg.id
    : 'https://www.nuget.org/packages/' + pkg.id;
}
