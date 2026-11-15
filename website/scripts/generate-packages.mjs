import { readFile } from 'node:fs/promises';
import path from 'node:path';

function metadata(xml, key) {
  const value = new RegExp('<' + key + '>([^<]+)</' + key + '>').exec(xml)?.[1];
  return value?.trim();
}

export async function readPackageCatalog(repository) {
  const read = (file) => readFile(path.join(repository, file), 'utf8');
  const release = JSON.parse(await read('eng/release-tracks.json')).releaseVersion;
  const websiteVersion = JSON.parse(await read('website/package.json')).version;
  if (websiteVersion !== release) {
    throw new Error('The website version must match the BlueTusk release ' + release);
  }
  const { families } = JSON.parse(await read('eng/product-families.json'));
  const packages = [];
  const identities = new Set();

  function add(pkg) {
    if (!pkg.id || identities.has(pkg.id.toLowerCase())) {
      throw new Error('Duplicate or missing public package ID: ' + (pkg.id ?? pkg.source));
    }
    identities.add(pkg.id.toLowerCase());
    packages.push(pkg);
  }

  for (const [family, definition] of Object.entries(families)) {
    const versionFile = await read(definition.versionFile);
    const version = metadata(versionFile, 'VersionPrefix');
    const suffix = metadata(versionFile, 'VersionSuffix');
    if (version !== release || suffix) {
      throw new Error(family + ' package version must match BlueTusk ' + release);
    }

    for (const source of definition.packages) {
      const xml = await read(source);
      const id = metadata(xml, 'PackageId') ?? path.posix.basename(source, '.csproj');
      const configuredVersion = metadata(xml, 'PackageVersion') ?? metadata(xml, 'Version');
      if (configuredVersion && configuredVersion !== release) {
        throw new Error(id + ' declares a conflicting package version.');
      }
      const kind =
        metadata(xml, 'PackAsTool') === 'true'
          ? 'tool'
          : metadata(xml, 'PackageType') === 'Template'
            ? 'template'
            : 'library';
      const description = metadata(xml, 'Description')?.replace(/&amp;/g, '&');
      add({ id, family, kind, description, source });
    }

    for (const folder of definition.npmPackages ?? []) {
      const source = folder + '/package.json';
      const { name, version, description } = JSON.parse(await read(source));
      if (version !== release)
        throw new Error(name + ' package version must match BlueTusk ' + release);
      add({ id: name, family, kind: 'browser', description, source });
    }
  }

  return { release, packages };
}
