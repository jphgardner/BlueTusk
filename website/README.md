# BlueTusk website

The BlueTusk site uses Angular, Angular Material, and Tailwind CSS.
Angular prerenders the public pages to static HTML. The site serves its
guides without a database or application server.

## Develop locally

Use Node.js 24 and the npm version declared in `package.json`.

```sh
npm ci
npm start
```

The Angular development server prints its local URL. Markdown sources are in
the repository’s `docs` directory. After editing a guide or the public
manifest, run `npm run docs:generate` to refresh the site, search index,
sitemap, and plain-text documentation index.

## Add a guide

1. Write a task-based guide with prerequisites, steps, expected output, and
   failure handling.
2. Add its path, category, slug, and summary to
   `scripts/docs-manifest.json`. Keep existing routes stable.
3. Run `npm run docs:generate` and check the rendered guide and its links.

The manifest lists public guides. Guide text comes from Markdown, and Angular renders searchable headings and copyable examples. The package catalog is generated from the package projects and browser manifests in the release inventory.

## Build and serve

```sh
npm run docs:check
npm test -- --watch=false
npm run build
```

Serve `dist/website/browser`. Use prerendered `index.html` files for known
routes and `index.csr.html` as the application fallback. The repository’s
Dockerfile and Nginx configuration serve this layout. See
[Angular deployment](https://angular.dev/tools/cli/deployment) for other
static hosts.
