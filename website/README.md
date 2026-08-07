# BlueTusk website

The developer-facing BlueTusk product and documentation site. It uses Angular
22, Angular Material, and Tailwind CSS. Every repository Markdown guide is
discovered, transformed into sanitized searchable content, cross-linked to
other guides, and checked for drift during a production build.

## Local development

```powershell
npm install
npm start
```

`npm start` refreshes the complete documentation index before starting the
Angular development server at `http://localhost:4200`.

## Validation and production build

```powershell
npm run docs:check
npm test -- --watch=false
npm run build
```

The standard Angular browser output is written to `dist/website/browser`.
The project contains no framework-specific server runtime. Its production
output is static and can be deployed by the existing site delivery process.

## Self-hosting

Serve the contents of `dist/website/browser` from any static web server:

- send unknown application routes to `index.html` so Angular deep links work;
- serve files under their original paths;
- enable normal compression and immutable caching for hashed assets;
- avoid long-lived caching for `index.html`.

The site has no backend, authentication, external persistence, or
platform-specific runtime requirement.
