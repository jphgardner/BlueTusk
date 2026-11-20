import { mkdir, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import MarkdownIt from 'markdown-it';
import hljs from 'highlight.js';
import { readPackageCatalog } from './generate-packages.mjs';

const website = path.resolve(import.meta.dirname, '..');
const repository = path.resolve(website, '..');
const github = 'https://github.com/jphgardner/BlueTusk';
const packageCatalog = await readPackageCatalog(repository);
const origin = 'https://bluetusk.io';
const check = process.argv.includes('--check');
const contentDirectory = 'src/generated/guide-content';
const labels = {
  'getting-started': 'Getting started',
  provider: 'ADO.NET',
  'ef-core': 'EF Core',
  'real-time': 'Real-time data',
  graph: 'Graphs',
  extensions: 'Extensions',
  services: 'Data services',
  operations: 'Operations',
};
const productRoutes = [
  '/',
  '/platform',
  '/provider',
  '/ef-core',
  '/real-time',
  '/graph',
  '/extensions',
  '/data-services',
  '/documentation',
  '/community',
  '/packages',
  '/architecture',
  '/use-cases',
  '/use-cases/order-fulfilment',
  '/use-cases/service-topology',
  '/use-cases/fraud-investigation',
  '/use-cases/offline-apps',
  '/learn',
  '/compatibility',
  '/production',
  '/whats-new',
  '/real-time/streams',
  '/real-time/sync',
  '/real-time/live',
];

const manifest = JSON.parse(
  await readFile(path.join(import.meta.dirname, 'docs-manifest.json'), 'utf8'),
);
const sources = new Map();
const aliases = new Map();
const routes = new Set(productRoutes);
const priorBlueTuskVersion =
  /\b0\.1\.0-preview(?:\.\d+)?\b|\b1\.[01]\.0-rc\.\d+\b|\b(?:BlueTusk(?:\.[\w.]+)?|Provider|--version)\s*\x60?1\.[01]\.0\b/i;
const internalContent =
  /\brelease gates?\b|\b(?:ship|stable|publication|product) gates?\b|\/evidence\b|(?:release-(?:readiness|tracks|endurance|review-handoff|qualification|process)|(?:approval|package)-evidence|performance-leadership-1\.1|v1-release-readiness|completion-audit)\.md\b/i;

for (const { category, slug, source, summary, order, aliases: legacy = [] } of manifest) {
  const route = '/documentation/' + category + '/' + slug;
  const validSource = /^(?:docs\/(?:[\w-]+\/)*[\w.-]+\.md|SECURITY\.md)$/.test(source);
  if (
    !Object.hasOwn(labels, category) ||
    !/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(slug) ||
    !validSource ||
    !summary?.trim() ||
    !Number.isInteger(order) ||
    sources.has(source) ||
    routes.has(route) ||
    internalContent.test(source + ' ' + summary)
  ) {
    throw new Error(
      'Invalid or duplicate public documentation entry: ' +
        JSON.stringify({ category, slug, source }),
    );
  }
  sources.set(source, route);
  routes.add(route);
  for (const alias of legacy) {
    if (
      !/^[a-z0-9-]+\/[a-z0-9-]+$/.test(alias) ||
      aliases.has(alias) ||
      internalContent.test(alias)
    ) {
      throw new Error('Invalid legacy guide route: ' + alias);
    }
    aliases.set(alias, category + '/' + slug);
  }
}
for (const alias of aliases.keys()) {
  if (routes.has('/documentation/' + alias))
    throw new Error('Alias shadows a public guide: ' + alias);
}

function resolveLink(href, source) {
  if (href === origin) return '/';
  if (href?.startsWith(origin + '/')) return href.slice(origin.length);
  if (!href || href.startsWith('#') || /^[a-z][a-z\d+.-]*:/i.test(href)) return href;
  const [relative, anchor] = href.split('#');
  const target = relative.startsWith('/')
    ? relative.slice(1)
    : path.posix.normalize(path.posix.join(path.posix.dirname(source), relative));
  const route = sources.get(target);
  const hash = anchor ? '#' + anchor : '';
  if (route) return route + hash;
  if (routes.has(href.split('#')[0])) return href;
  if (target === '..' || target.startsWith('../'))
    throw new Error(source + ': link leaves the repository: ' + href);
  return github + (path.posix.extname(target) ? '/blob/main/' : '/tree/main/') + target + hash;
}

const md = new MarkdownIt({ html: false, typographer: true });
const renderToken = (tokens, index, options, _env, self) =>
  self.renderToken(tokens, index, options);
const defaultLink = md.renderer.rules.link_open ?? renderToken;
md.renderer.rules.link_open = (tokens, index, options, env, self) => {
  const token = tokens[index];
  token.attrSet('href', resolveLink(token.attrGet('href'), env.source));
  return defaultLink(tokens, index, options, env, self);
};
const defaultImage = md.renderer.rules.image;
md.renderer.rules.image = (tokens, index, options, env, self) => {
  const token = tokens[index];
  const url = resolveLink(token.attrGet('src'), env.source);
  token.attrSet(
    'src',
    url.replace(
      github + '/blob/main/',
      'https://raw.githubusercontent.com/jphgardner/BlueTusk/main/',
    ),
  );
  return defaultImage(tokens, index, options, env, self);
};

const renderHeading = (tokens, index, options, env, self) => {
  const token = tokens[index];
  return token.level === 0
    ? env.headingMarkers.get(index)
    : renderToken(tokens, index, options, env, self);
};
md.renderer.rules.heading_open = renderHeading;
md.renderer.rules.heading_close = (tokens, index, options, env, self) =>
  tokens[index].level === 0 ? '' : renderToken(tokens, index, options, env, self);
function codeBlock(fallback) {
  return (tokens, index, options, env, self) => {
    const token = tokens[index];
    if (token.level !== 0) return fallback(tokens, index, options, env, self);
    const language = token.info.trim().split(/\s+/)[0];
    const code = token.content.trimEnd();
    const highlighted = hljs.getLanguage(language)
      ? hljs.highlight(code, { language }).value
      : md.utils.escapeHtml(code);
    return env.mark({ kind: 'code', code, highlighted, language });
  };
}
md.renderer.rules.fence = codeBlock(md.renderer.rules.fence);
md.renderer.rules.code_block = codeBlock(md.renderer.rules.code_block);

const slugify = (value) =>
  value
    .toLowerCase()
    .replace(/[^a-z0-9 -]/g, '')
    .replace(/\s+/g, '-')
    .replace(/^-+|-+$/g, '') || 'section';

const guides = [];
const markdownBySource = new Map();
const errors = [];
for (const { category, slug, source, summary, order, keywords = [] } of manifest) {
  const markdown = (await readFile(path.join(repository, source), 'utf8')).replace(/\r\n/g, '\n');
  const privateMatch = (source + '\n' + markdown).match(internalContent);
  if (privateMatch) errors.push(source + ': ' + privateMatch[0]);
  const priorVersion = markdown.match(priorBlueTuskVersion);
  if (priorVersion)
    errors.push(
      source +
        ': package reference must target BlueTusk ' +
        packageCatalog.release +
        ' (' +
        priorVersion[0] +
        ')',
    );
  markdownBySource.set(source, markdown);
  const fragments = [];
  const headingMarkers = new Map();
  const env = {
    source,
    headingMarkers,
    mark(block) {
      return '<!--BT_BLOCK_' + (fragments.push(block) - 1) + '-->';
    },
  };
  const tokens = md.parse(markdown, env);
  const headings = [];
  const ids = new Set();
  let title;
  for (const [index, token] of tokens.entries()) {
    if (token.type !== 'heading_open' || token.level !== 0) continue;
    const inline = tokens[index + 1];
    const text = inline.children
      .map((child) => (child.type === 'softbreak' ? ' ' : child.content))
      .join('')
      .trim();
    inline.children = [];
    const level = Number(token.tag.slice(1));
    if (level === 1) {
      if (title) throw new Error(source + ': use exactly one top-level title.');
      title = text;
      headingMarkers.set(index, '');
      continue;
    }
    const base = slugify(text);
    let id = base;
    for (let suffix = 2; ids.has(id); suffix++) id = base + '-' + suffix;
    ids.add(id);
    const heading = { id, text, level };
    headings.push(heading);
    headingMarkers.set(index, env.mark({ kind: 'heading', heading }));
  }
  if (!title) throw new Error(source + ': missing title.');
  const body = md.renderer.render(tokens, md.options, env);
  const blocks = body
    .split(/<!--BT_BLOCK_(\d+)-->/)
    .flatMap((part, index) =>
      index % 2 ? [fragments[Number(part)]] : part.trim() ? [{ kind: 'html', html: part }] : [],
    );
  const searchText = tokens
    .filter((token) => token.type === 'inline')
    .map((token) => token.children.map((child) => child.content).join(''))
    .join(' ')
    .replace(/\s+/g, ' ')
    .slice(0, 1800);
  guides.push({
    metadata: {
      category,
      categoryLabel: labels[category],
      slug,
      title,
      summary,
      order,
      headings,
      sourcePath: source,
      sourceUrl: github + '/blob/main/' + source,
      keywords,
    },
    searchText,
    blocks,
  });
}
if (errors.length) throw new Error('Invalid public documentation:\n' + errors.join('\n'));

const prefix = '// Generated by scripts/generate-docs.mjs. Do not edit manually.\n';
const json = (value) => JSON.stringify(value, null, 2);
const files = new Map([
  [
    'src/generated/packages.generated.ts',
    prefix +
      "import type { PackageRecord } from '../app/content/packages';\n\n" +
      'export const RELEASE_VERSION = ' +
      json(packageCatalog.release) +
      ';\n' +
      'export const PACKAGES: readonly PackageRecord[] = ' +
      json(packageCatalog.packages) +
      ';\n',
  ],
  [
    'src/generated/guides.generated.ts',
    prefix +
      "import type { GuideIndexEntry } from '../app/content/models';\n\n" +
      'export const GUIDES: readonly GuideIndexEntry[] = ' +
      json(guides.map(({ metadata }) => metadata)) +
      ';\n',
  ],
  [
    'src/generated/guide-search.generated.ts',
    prefix +
      "import type { SearchRecord } from '../app/content/models';\n\n" +
      'export const GUIDE_SEARCH: readonly SearchRecord[] = ' +
      json(
        guides.map(({ metadata: g, searchText }) => ({
          title: g.title,
          description: g.summary,
          group: g.categoryLabel,
          route: sources.get(g.sourcePath),
          keywords: [...g.keywords, ...g.headings.map(({ text }) => text), searchText].join(' '),
        })),
      ) +
      ';\n',
  ],
]);

const loadCases = [];
for (const category of Object.keys(labels)) {
  const items = guides.filter(({ metadata }) => metadata.category === category);
  if (!items.length) throw new Error('No public guides in ' + category);
  files.set(
    contentDirectory + '/' + category + '.generated.ts',
    prefix +
      "import type { GuideManifestEntry } from '../../app/content/models';\n\n" +
      'export const GUIDES: readonly GuideManifestEntry[] = ' +
      json(items.map(({ metadata, blocks }) => ({ ...metadata, blocks }))) +
      ';\n',
  );
  loadCases.push(
    "    case '" +
      category +
      "': return (await import('./guide-content/" +
      category +
      ".generated')).GUIDES.find((guide) => guide.slug === slug);",
  );
}
files.set(
  'src/generated/guide-loader.generated.ts',
  prefix +
    "import type { GuideManifestEntry } from '../app/content/models';\n\n" +
    'const aliases = new Map<string, string>(' +
    json([...aliases]) +
    ');\n\n' +
    'export async function loadGuide(category: string, slug: string): Promise<GuideManifestEntry | undefined> {\n' +
    "  const canonical = aliases.get(category + '/' + slug);\n" +
    "  if (canonical) [category, slug] = canonical.split('/');\n" +
    '  switch (category) {\n' +
    loadCases.join('\n') +
    '\n    default: return undefined;\n  }\n}\n',
);
files.set('prerender-routes.txt', [...routes].join('\n') + '\n');
files.set(
  'public/sitemap.xml',
  '<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n' +
    [...routes].map((route) => '  <url><loc>' + origin + route + '</loc></url>').join('\n') +
    '\n</urlset>\n',
);
const intro =
  '# BlueTusk documentation\n\n> PostgreSQL for .NET, with ADO.NET, EF Core, real-time data, graphs, and data services.\n\n';
files.set(
  'public/llms.txt',
  intro +
    '[Full documentation text](' +
    origin +
    '/llms-full.txt)\n' +
    Object.entries(labels)
      .map(
        ([category, label]) =>
          '\n## ' +
          label +
          '\n\n' +
          guides
            .filter(({ metadata }) => metadata.category === category)
            .map(
              ({ metadata: g }) =>
                '- [' + g.title + '](' + origin + sources.get(g.sourcePath) + '): ' + g.summary,
            )
            .join('\n') +
          '\n',
      )
      .join(''),
);
files.set(
  'public/llms-full.txt',
  intro +
    guides
      .map(
        ({ metadata: g }) =>
          'Guide: ' +
          origin +
          sources.get(g.sourcePath) +
          '\n\n' +
          markdownBySource.get(g.sourcePath).trim(),
      )
      .join('\n\n---\n\n') +
    '\n',
);

const stale = [];
await mkdir(path.join(website, contentDirectory), { recursive: true });
for (const name of await readdir(path.join(website, contentDirectory))) {
  const relative = contentDirectory + '/' + name;
  if (name.endsWith('.generated.ts') && !files.has(relative)) {
    if (check) stale.push(relative);
    else await rm(path.join(website, relative));
  }
}
for (const [relative, content] of files) {
  const target = path.join(website, relative);
  if (check) {
    const existing = await readFile(target, 'utf8').catch(() => null);
    if (existing?.replace(/\r\n/g, '\n') !== content) stale.push(relative);
  } else {
    await writeFile(target, content, 'utf8');
  }
}
if (stale.length) throw new Error('Run npm run docs:generate. Stale files:\n' + stale.join('\n'));
console.log(
  (check ? 'Verified ' : 'Generated ') +
    guides.length +
    ' public guides and ' +
    routes.size +
    ' routes.',
);
