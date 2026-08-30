import { brotliCompressSync, constants as zlibConstants } from 'node:zlib';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFile, readdir, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const websiteRoot = path.resolve(scriptDirectory, '..');
const contractPath = path.join(websiteRoot, 'production-contract.json');
const contract = JSON.parse(await readFile(contractPath, 'utf8'));

if (contract.schemaVersion !== 1) {
  throw new Error(
    `Expected website production contract schema 1; found ${contract.schemaVersion}.`,
  );
}

const distributionRoot = path.join(websiteRoot, contract.distributionRoot);
const indexPath = path.join(distributionRoot, 'index.html');
const index = await readFile(indexPath, 'utf8');

if (index.includes('__SITE_ORIGIN__')) {
  throw new Error('The production index contains an unresolved site-origin placeholder.');
}
for (const required of contract.requiredMetadata) {
  if (!index.includes(required)) {
    throw new Error(`The production index is missing required metadata: ${required}`);
  }
}
if (/\sonload=/i.test(index) || /<link[^>]+rel="stylesheet"[^>]+media="print"/i.test(index)) {
  throw new Error(
    'Production stylesheets must not depend on inline load handlers that violate the site CSP.',
  );
}

async function collectFiles(directory, relative = '') {
  const results = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const relativePath = path.posix.join(relative, entry.name);
    const absolutePath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      results.push(...(await collectFiles(absolutePath, relativePath)));
    } else if (entry.isFile()) {
      results.push({
        relativePath,
        absolutePath,
        bytes: (await stat(absolutePath)).size,
      });
    }
  }
  return results;
}

const files = (await collectFiles(distributionRoot))
  .filter((file) => file.relativePath !== 'production-metrics.json')
  .sort((left, right) => left.relativePath.localeCompare(right.relativePath));
const fileMap = new Map(files.map((file) => [file.relativePath, file]));
const prerenderedRouteCount = files.filter(
  (file) => file.relativePath === 'index.html' || file.relativePath.endsWith('/index.html'),
).length;
if (prerenderedRouteCount < (contract.minimumPrerenderedRoutes ?? 0)) {
  throw new Error(
    `Production output contains ${prerenderedRouteCount} prerendered routes; ` +
      `${contract.minimumPrerenderedRoutes} are required.`,
  );
}
for (const requiredAsset of contract.requiredAssets) {
  if (!fileMap.has(requiredAsset)) {
    throw new Error(`Required production website asset is missing: ${requiredAsset}`);
  }
}
const robots = await readFile(fileMap.get('robots.txt').absolutePath, 'utf8');
const sitemap = await readFile(fileMap.get('sitemap.xml').absolutePath, 'utf8');
const llmsIndex = await readFile(fileMap.get('llms.txt').absolutePath, 'utf8');
const llmsFull = await readFile(fileMap.get('llms-full.txt').absolutePath, 'utf8');
if (!robots.includes('Sitemap: https://bluetusk.io/sitemap.xml')) {
  throw new Error('robots.txt does not advertise the production sitemap.');
}
for (const crawler of ['OAI-SearchBot', 'ChatGPT-User', 'GPTBot']) {
  if (!robots.includes(`User-agent: ${crawler}`)) {
    throw new Error(`robots.txt does not explicitly allow ${crawler}.`);
  }
}
if (!sitemap.includes('<loc>https://bluetusk.io/documentation/real-time/continuous-graph</loc>')) {
  throw new Error('The production sitemap is missing a representative documentation route.');
}
if (
  !llmsIndex.includes('https://bluetusk.io/documentation/getting-started/quickstart') ||
  !llmsIndex.includes('https://bluetusk.io/llms-full.txt')
) {
  throw new Error('llms.txt does not advertise the quickstart and curated guide set.');
}
if (
  !llmsFull.includes('# BlueTusk curated documentation') ||
  !llmsFull.includes('# Quickstart: run the first query') ||
  llmsFull.includes('# Independent V1 release review handoff')
) {
  throw new Error('llms-full.txt does not contain the curated BlueTusk guide set.');
}

for (const route of contract.requiredPrerenderedRoutes ?? []) {
  const relativePath = route === '/' ? 'index.html' : `${route.slice(1)}/index.html`;
  const file = fileMap.get(relativePath);
  if (!file) {
    throw new Error(`Required prerendered route is missing: ${route}`);
  }
  const html = await readFile(file.absolutePath, 'utf8');
  if (!/<app-root[^>]*>[\s\S]*?<h1[\s>]/i.test(html)) {
    throw new Error(`Prerendered route does not contain readable page content: ${route}`);
  }
}

const sourceMaps = files.filter((file) => file.relativePath.endsWith('.map'));
if (sourceMaps.length !== 0) {
  throw new Error(`Production output contains ${sourceMaps.length} source map(s).`);
}

const initialReferences = [...index.matchAll(/(?:src|href)="([^"]+\.(?:js|css))"/g)].map((match) =>
  match[1].replace(/^\//, ''),
);
const initialAssets = [...new Set(initialReferences)].map((relativePath) => {
  const file = fileMap.get(relativePath);
  if (!file) {
    throw new Error(`Initial asset referenced by index.html is missing: ${relativePath}`);
  }
  if (!/-[A-Za-z0-9_-]{8,}\.(?:js|css)$/.test(relativePath)) {
    throw new Error(`Initial production asset is not content hashed: ${relativePath}`);
  }
  return file;
});
if (
  initialAssets.filter((file) => file.relativePath.endsWith('.js')).length < 1 ||
  initialAssets.filter((file) => file.relativePath.endsWith('.css')).length !== 1
) {
  throw new Error(
    'Production index must reference at least one initial JavaScript asset and exactly one CSS asset.',
  );
}

function brotliBytes(file) {
  return brotliCompressSync(file, {
    params: {
      [zlibConstants.BROTLI_PARAM_QUALITY]: 11,
    },
  }).length;
}

const measuredAssets = new Map();
for (const file of files.filter((candidate) => /\.(?:js|css)$/.test(candidate.relativePath))) {
  const content = await readFile(file.absolutePath);
  measuredAssets.set(file.relativePath, {
    ...file,
    brotliBytes: brotliBytes(content),
  });
}

const measuredInitial = initialAssets.map((file) => measuredAssets.get(file.relativePath));
const initialRawBytes = measuredInitial.reduce((total, file) => total + file.bytes, 0);
const initialBrotliBytes = measuredInitial.reduce((total, file) => total + file.brotliBytes, 0);
const lazyAssets = [...measuredAssets.values()].filter(
  (file) => !initialAssets.some((initial) => initial.relativePath === file.relativePath),
);
const largestLazy = lazyAssets.sort((left, right) => right.brotliBytes - left.brotliBytes)[0];
const totalDistributionBytes = files.reduce((total, file) => total + file.bytes, 0);

const limits = contract.limits;
const violations = [
  [initialRawBytes, limits.initialRawBytes, 'initial raw bytes'],
  [initialBrotliBytes, limits.initialBrotliBytes, 'initial Brotli bytes'],
  [largestLazy?.brotliBytes ?? 0, limits.largestLazyBrotliBytes, 'largest lazy Brotli bytes'],
  [totalDistributionBytes, limits.totalDistributionBytes, 'total distribution bytes'],
].filter(([actual, limit]) => actual > limit);
if (violations.length !== 0) {
  throw new Error(
    `Website production budget exceeded: ${violations
      .map(([actual, limit, label]) => `${label} ${actual}/${limit}`)
      .join('; ')}.`,
  );
}

let sourceCommit = 'unknown';
try {
  sourceCommit = execFileSync('git', ['rev-parse', 'HEAD'], {
    cwd: path.resolve(websiteRoot, '..'),
    encoding: 'utf8',
  }).trim();
} catch {}
if (!/^[0-9a-f]{40}$/i.test(sourceCommit)) {
  throw new Error(`A full Git source commit is required; found ${sourceCommit}.`);
}

const report = {
  schemaVersion: 1,
  sourceCommit,
  assetCount: files.length,
  files: await Promise.all(
    files.map(async (file) => ({
      path: file.relativePath,
      bytes: file.bytes,
      sha256: createHash('sha256')
        .update(await readFile(file.absolutePath))
        .digest('hex'),
    })),
  ),
  initialAssets: measuredInitial.map((file) => ({
    path: file.relativePath,
    rawBytes: file.bytes,
    brotliBytes: file.brotliBytes,
  })),
  metrics: {
    prerenderedRouteCount,
    initialRawBytes,
    initialBrotliBytes,
    largestLazyAsset: largestLazy?.relativePath ?? null,
    largestLazyBrotliBytes: largestLazy?.brotliBytes ?? 0,
    totalDistributionBytes,
  },
  limits,
};
await writeFile(
  path.join(distributionRoot, 'production-metrics.json'),
  `${JSON.stringify(report, null, 2)}\n`,
  'utf8',
);

console.log(
  `Verified production website: ${initialRawBytes}/${limits.initialRawBytes} initial raw bytes, ` +
    `${initialBrotliBytes}/${limits.initialBrotliBytes} initial Brotli bytes, ` +
    `${largestLazy?.brotliBytes ?? 0}/${limits.largestLazyBrotliBytes} largest lazy Brotli bytes, ` +
    `${totalDistributionBytes}/${limits.totalDistributionBytes} total bytes, no source maps, ` +
    'hashed assets and complete metadata.',
);
