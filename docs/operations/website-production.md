# Website production contract

The Angular website is part of the V1 release evidence surface. It explains
product maturity, publishes the source-synchronized handbook, and exposes the
current benchmark, compatibility and operational records. A successful
application build alone is not enough: the shipped static output must also meet
the checked-in delivery contract.

## Deterministic build budgets

[`website/production-contract.json`](../../website/production-contract.json)
defines the maximum production output:

| Measurement | V1 ceiling | Purpose |
| --- | ---: | --- |
| Initial JavaScript and CSS, raw | 950 kB | Prevent unbounded startup growth |
| Initial JavaScript and CSS, Brotli | 220 KiB | Bound the expected compressed transfer |
| Largest lazy JavaScript or CSS asset, Brotli | 275 KiB | Bound documentation-route payload growth |
| Complete static distribution | 20 MiB | Bound 135 prerendered routes and the machine-readable handbook |

Angular independently enforces its configured initial bundle ceiling. The
post-build verifier measures the emitted files, compresses JavaScript and CSS
with Brotli, records the byte length and SHA-256 of every deployed file,
confirms content-hashed initial assets, rejects source maps and writes
`production-metrics.json` into the archived static distribution:

```powershell
cd website
npm ci
npm test
npm run build
```

`npm run build` invokes `verify-production-build.mjs` automatically. Run
`npm run verify:production` to recheck an existing build. Independently verify
the emitted report and every recorded asset with:

```powershell
../eng/verify-website-evidence.ps1 `
  -DistributionPath dist/website/browser `
  -MetricsPath dist/website/browser/production-metrics.json `
  -ExpectedCommit (git rev-parse HEAD)
```

## Delivery and discoverability

The production contract also requires:

- a language, viewport, description, theme colour, Open Graph and Twitter
  metadata record;
- no unresolved deployment-origin placeholder;
- 135 prerendered routes so crawlers receive complete page text without running JavaScript;
- explicit `OAI-SearchBot`, `ChatGPT-User`, and `GPTBot` access in `robots.txt`;
- a sitemap, `llms.txt` documentation index, complete `llms-full.txt` handbook, and standard
  `.well-known/security.txt` contact;
- guide-specific descriptions, canonical URLs, Open Graph URLs, and crawler directives;
- hashed JavaScript and CSS with no production source maps;
- explicit image dimensions to prevent layout shift;
- a bounded persistent-navigation logo; and
- eager loading for the above-fold architecture image with fixed dimensions.

The host remains responsible for TLS, SPA fallback to `index.html`, Brotli or
gzip content encoding, immutable caching for hashed assets, short-lived caching
for `index.html`, and security headers appropriate to the selected deployment
origin. Those host-specific facts belong in deployment evidence rather than in
an Angular source claim.

## Release evidence

The manual `build.yml` exact-candidate run builds and tests the website, runs
the production verifier, audits npm dependencies and archives the browser
distribution together with `production-metrics.json`. The same run archives
the canonical six-family package evidence, so the protected gate cannot combine
a website from one build with packages from another. The run is one of the 7
exact-SHA workflow records required by the protected V1 candidate gate.
The aggregator downloads both artifacts, binds the metrics report by SHA-256,
verifies its source commit and budgets, then re-hashes every emitted file
before adding the complete distribution to the 90-day readiness bundle.

These metrics are delivery regression budgets, not field Core Web Vitals.
Production operators must still collect real-user LCP, INP and CLS for the
chosen host, geography and traffic profile and attach the results to the
`website-deployment-acceptance` record.
