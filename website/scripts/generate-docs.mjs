import { readdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import MarkdownIt from 'markdown-it';
import hljs from 'highlight.js';

const websiteRoot = path.resolve(import.meta.dirname, '..');
const repositoryRoot = path.resolve(websiteRoot, '..');
const manifestPath = path.join(import.meta.dirname, 'docs-manifest.json');
const outputPath = path.join(websiteRoot, 'src', 'generated', 'guides.generated.ts');
const searchOutputPath = path.join(websiteRoot, 'src', 'generated', 'guide-search.generated.ts');
const repositoryUrl = 'https://github.com/jphgardner/BlueTusk';
const checkOnly = process.argv.includes('--check');

const slugify = (value) =>
  value
    .toLowerCase()
    .replace(/<[^>]+>/g, '')
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-|-$/g, '') || 'section';

async function listMarkdownFiles(directory, prefix) {
  const entries = await readdir(directory, { withFileTypes: true });
  const files = [];
  for (const entry of entries) {
    const absolute = path.join(directory, entry.name);
    const relative = `${prefix}/${entry.name}`.replaceAll('\\', '/');
    if (entry.isDirectory()) files.push(...(await listMarkdownFiles(absolute, relative)));
    else if (entry.isFile() && entry.name.toLowerCase().endsWith('.md')) files.push(relative);
  }
  return files;
}

const categoryLabels = {
  'getting-started': 'Getting started',
  provider: 'Provider',
  'ef-core': 'EF Core',
  'real-time': 'Real time',
  extensions: 'Extensions',
  graph: 'Graph',
  architecture: 'Architecture',
  operations: 'Operations',
};

function inferCategory(source) {
  if (
    source === 'README.md' ||
    source === 'docs/README.md' ||
    source.startsWith('docs/getting-started/')
  )
    return 'getting-started';
  if (
    /^docs\/(?:ado-net|protocol|replication|types)\//.test(source) ||
    source === 'docs/pipeline-mode.md'
  )
    return 'provider';
  if (source.startsWith('docs/ef-core/')) return 'ef-core';
  if (/^docs\/(?:streams|sync|live|realtime-platform|control-plane)\//.test(source))
    return 'real-time';
  if (source.startsWith('docs/extensions/')) return 'extensions';
  if (/^docs\/(?:graph|continuous-graph)\//.test(source)) return 'graph';
  if (source.startsWith('docs/architecture/')) return 'architecture';
  return 'operations';
}

function inferSlug(source) {
  if (source === 'README.md') return 'project-overview';
  if (source === 'docs/README.md') return 'handbook';
  const withoutExtension = source.replace(/\.md$/i, '').replace(/^docs\//, '');
  const segments = withoutExtension.split('/');
  if (segments.at(-1)?.toLowerCase() === 'readme') segments.pop();
  return slugify(segments.join('-') || 'handbook');
}

function inferSummary(markdown, fallback) {
  const lines = markdown.split(/\r?\n/);
  let paragraph = [];
  let inFence = false;
  for (const rawLine of lines) {
    const line = rawLine.trim();
    if (line.startsWith('```')) {
      inFence = !inFence;
      continue;
    }
    if (
      inFence ||
      !line ||
      line.startsWith('#') ||
      line.startsWith('|') ||
      line.startsWith('> [!') ||
      line.startsWith('![')
    ) {
      if (paragraph.length > 0) break;
      continue;
    }
    paragraph.push(line);
    if (paragraph.join(' ').length >= 180) break;
  }
  const text = paragraph
    .join(' ')
    .replace(/\[([^\]]+)\]\([^)]+\)/g, '$1')
    .replace(/[`*_>#]/g, '')
    .replace(/\s+/g, ' ')
    .trim();
  if (!text) return fallback;
  return text.length > 220 ? `${text.slice(0, 217).trimEnd()}…` : text;
}

const manifestOverrides = JSON.parse(await readFile(manifestPath, 'utf8'));
const discoveredDocs = await listMarkdownFiles(path.join(repositoryRoot, 'docs'), 'docs');
const rootDocs = ['README.md', 'CONTRIBUTING.md', 'SECURITY.md', 'VERSIONING.md'];
const allSources = [...new Set([...discoveredDocs, ...rootDocs])].sort();
const overrideBySource = new Map(
  manifestOverrides.map((entry) => [entry.source.replaceAll('\\', '/'), entry]),
);
const manifest = [];
for (const [index, source] of allSources.entries()) {
  const existing = overrideBySource.get(source);
  if (existing) {
    manifest.push(existing);
    continue;
  }
  const markdown = await readFile(path.join(repositoryRoot, ...source.split('/')), 'utf8');
  const category = inferCategory(source);
  const name = path.basename(source, '.md').replaceAll('-', ' ');
  manifest.push({
    category,
    categoryLabel: categoryLabels[category],
    slug: inferSlug(source),
    source,
    summary: inferSummary(markdown, `Reference guide for ${name}.`),
    keywords: source
      .replace(/\.md$/i, '')
      .split(/[\/_.-]+/)
      .filter(Boolean),
    order: 1000 + index,
  });
}
const routeBySource = new Map(
  manifest.map((entry) => [
    entry.source.replaceAll('\\', '/'),
    `/documentation/${entry.category}/${entry.slug}`,
  ]),
);

const escapeHtml = (value) =>
  value.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');

function resolveLink(href, sourcePath) {
  if (!href || href.startsWith('#') || /^[a-z][a-z\d+.-]*:/i.test(href)) return href;
  const [linkPath, anchor = ''] = href.split('#');
  const sourceDirectory = path.posix.dirname(sourcePath);
  const normalized = (
    linkPath.startsWith('/')
      ? linkPath.slice(1)
      : path.posix.normalize(path.posix.join(sourceDirectory, linkPath))
  ).replace(/^\.\//, '');
  const internal = routeBySource.get(normalized);
  if (internal) return `${internal}${anchor ? `#${anchor}` : ''}`;
  if (normalized.endsWith('/')) {
    return `${repositoryUrl}/tree/main/${normalized}${anchor ? `#${anchor}` : ''}`;
  }
  return `${repositoryUrl}/blob/main/${normalized}${anchor ? `#${anchor}` : ''}`;
}

function createMarkdown(codeBlocks) {
  const md = new MarkdownIt({
    html: false,
    linkify: false,
    typographer: true,
  });
  const renderCode = (code, language = '') => {
    const safeLanguage = language && hljs.getLanguage(language) ? language : '';
    const highlighted = safeLanguage
      ? hljs.highlight(code, { language: safeLanguage }).value
      : escapeHtml(code);
    const index = codeBlocks.push({ kind: 'code', code, highlighted, language: safeLanguage }) - 1;
    return `<!--BT_CODE_${index}-->`;
  };
  md.renderer.rules.fence = (tokens, index) =>
    renderCode(tokens[index].content, tokens[index].info.trim().split(/\s+/)[0]);
  md.renderer.rules.code_block = (tokens, index) => renderCode(tokens[index].content);
  const defaultLinkOpen =
    md.renderer.rules.link_open ??
    ((tokens, index, options, _env, self) => self.renderToken(tokens, index, options));
  md.renderer.rules.link_open = (tokens, index, options, env, self) => {
    const hrefIndex = tokens[index].attrIndex('href');
    if (hrefIndex >= 0) {
      tokens[index].attrs[hrefIndex][1] = resolveLink(
        tokens[index].attrs[hrefIndex][1],
        env.sourcePath,
      );
      const href = tokens[index].attrs[hrefIndex][1];
      if (/^https?:/.test(href)) {
        tokens[index].attrSet('target', '_blank');
        tokens[index].attrSet('rel', 'noreferrer');
      }
    }
    return defaultLinkOpen(tokens, index, options, env, self);
  };
  return md;
}

const guides = [];
for (const item of manifest) {
  const absoluteSource = path.join(repositoryRoot, ...item.source.split('/'));
  const markdown = await readFile(absoluteSource, 'utf8');
  const codeBlocks = [];
  const md = createMarkdown(codeBlocks);
  const tokens = md.parse(markdown, { sourcePath: item.source });
  const headings = [];
  const usedIds = new Map();
  for (let index = 0; index < tokens.length; index++) {
    if (tokens[index].type !== 'heading_open') continue;
    const text = tokens[index + 1]?.content ?? 'Section';
    const base = slugify(text);
    const count = usedIds.get(base) ?? 0;
    usedIds.set(base, count + 1);
    const id = count === 0 ? base : `${base}-${count + 1}`;
    headings.push({ id, text, level: Number(tokens[index].tag.slice(1)) });
  }
  const title = headings[0]?.text ?? item.slug;
  const searchableText = tokens
    .filter((token) => token.type === 'inline' || token.type === 'code_inline')
    .map((token) => token.content)
    .join(' ')
    .replace(/\s+/g, ' ')
    .trim();
  const wordCount = markdown.split(/\s+/).filter(Boolean).length;
  const { source: _source, ...metadata } = item;
  const rendered = md.renderer.render(tokens, md.options, { sourcePath: item.source });
  const parts = rendered.split(/<!--BT_CODE_(\d+)-->/);
  const blocks = parts.flatMap((part, index) => {
    if (index % 2 === 1) return [codeBlocks[Number(part)]];
    return part ? [{ kind: 'html', html: part }] : [];
  });
  guides.push({
    ...metadata,
    title,
    sourcePath: item.source,
    sourceUrl: `${repositoryUrl}/blob/main/${item.source}`,
    headings,
    wordCount,
    readMinutes: Math.max(1, Math.ceil(wordCount / 220)),
    searchText: searchableText.slice(0, 6000),
    blocks,
  });
}

const categoryOrder = [
  'getting-started',
  'provider',
  'ef-core',
  'real-time',
  'extensions',
  'graph',
  'architecture',
  'operations',
];
guides.sort(
  (left, right) =>
    categoryOrder.indexOf(left.category) - categoryOrder.indexOf(right.category) ||
    left.order - right.order,
);
const generated =
  `// Generated by scripts/generate-docs.mjs. Do not edit manually.\n` +
  `import type { GuideManifestEntry } from '../app/content/models';\n\n` +
  `export const GUIDES: readonly GuideManifestEntry[] = ${JSON.stringify(guides, null, 2)};\n`;
const searchRecords = guides.map((guide) => ({
  title: guide.title,
  description: guide.summary,
  route: `/documentation/${guide.category}/${guide.slug}`,
  group: guide.categoryLabel,
  keywords: `${guide.keywords.join(' ')} ${guide.headings.map((heading) => heading.text).join(' ')} ${guide.searchText.slice(0, 2000)}`,
}));
const generatedSearch =
  `// Generated by scripts/generate-docs.mjs. Do not edit manually.\n` +
  `import type { SearchRecord } from '../app/content/models';\n\n` +
  `export const GUIDE_SEARCH: readonly SearchRecord[] = ${JSON.stringify(searchRecords, null, 2)};\n`;

if (checkOnly) {
  let current = '';
  let currentSearch = '';
  try {
    current = await readFile(outputPath, 'utf8');
  } catch {}
  try {
    currentSearch = await readFile(searchOutputPath, 'utf8');
  } catch {}
  if (current !== generated || currentSearch !== generatedSearch) {
    console.error('Generated documentation is stale. Run npm run docs:generate.');
    process.exitCode = 1;
  }
} else {
  await writeFile(outputPath, generated, 'utf8');
  await writeFile(searchOutputPath, generatedSearch, 'utf8');
  console.log(`Generated ${guides.length} documentation guides.`);
}
