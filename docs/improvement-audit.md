# BlueTusk improvement audit and action record

Audited on 27 September 2026. This record covers the whole product and developer
experience. A completed implementation needs its stated validation; older
benchmarks and narrow test runs do not certify a later release candidate.

## Current decisions

Continuous Graph remains in the product. The owner has directed that its later
availability must not block the other product lines. PostgreSQL 19 Beta 4
removed SQL/PGQ, including the engine used by BlueTusk's `GRAPH_TABLE` queries.
Graph's Beta 3 fixtures remain preview evidence; a PostgreSQL 19 GA milestone
alone will not establish Graph compatibility. Its release policy must depend on
a supported server providing the required capability and its own evidence.

The [official Beta 4 announcement](https://www.postgresql.org/about/news/postgresql-19-beta-4-released-3386/)
also records fixes for native `REPACK`. The
[roadmap](https://www.postgresql.org/developer/roadmap/) now targets October 2026.
Update current support claims without rewriting historical measurements.

## Work and acceptance evidence

| Work | State | Evidence required to close it |
| --- | --- | --- |
| Separate Graph release readiness from the other families | Track policy, scoped performance, five-family packaging/approvals, stable core CI/endurance and isolated Graph database/config implemented; final candidate aggregation pending | Core helper self-tests and actual package/SBOM checks; exact 1.2 remote candidate aggregation still required |
| Live refresh/replay recovery | Implemented; Windows PostgreSQL validation passed | 78 tests pass with zero skips, including real PostgreSQL stores and SSE/SignalR/gRPC transports; final-candidate platform and endurance gates remain |
| Contributor setup and focused validation | Implemented; local command validation passed | Doctor/project registration, missing-database refusal, hashed TRX summary, focused Check, five client builds/8 client tests, generated guides and production website build |
| Pool candidate performance acceptance | Pending | Matched before/after captures for `dd1da1a`, then the complete reference comparisons with latency, allocation, CPU and RSS |
| Server incremental result costs | Pending | Small-change work scales with affected rows; immutable historical snapshots; ordered top-N and repair correctness; unchanged Graph tier cost targets |
| Browser reducer costs | Pending | Bounded event batching before materialization; sequence/resume correctness; large-result/churn allocation and latency measurements |
| Spool completion stalls | Pending | Unchanged end-to-end 4 MiB P95 budget passes on isolated storage; crash recovery retains flush, checksum and acknowledgement guarantees |
| Multiplexing and EF performance | Pending | Existing absolute limits and confidence-qualified paired workload targets pass; no hidden allocation or memory regressions |
| Connector coverage and overhead | Seven destinations now required by the performance contract; measurements and optimisation pending | Profile and measure Kafka, S3 and Webhooks alongside the four earlier destinations; retain transaction ordering, durability, retry and quarantine semantics |
| Control Plane degraded fleet operation | Pending | Bounded per-instance deadlines, partial/stale status, 1/100/1,000-source scaling and failed-instance tests |
| Dashboard scale and maintainability | Pending | Direct detail access, bounded server-side inventory queries, mobile/keyboard/large-inventory browser evidence, separated presentation assets |
| Website availability | External blocker identified | DigitalOcean account unlocked; existing workers Ready; Traefik/site healthy; external HTTPS, CSS and documentation routes verified |
| One current documentation truth | Pending | Stable/preview/candidate distinctions, generated support/version information, runnable package examples and guide journeys |
| Final release evidence | Pending | One immutable candidate, required CI, consumers, supply chain, endurance, rehearsals, pilots and independent approval |

## First implementation evidence

Fourteen new Live failure cases failed against the preceding implementation.
After the recovery change, all 24 original query-session/shared-subscription
cases pass. Four additional tests cover serialization, post-append cancellation,
divergent replay and failing metrics observers. The complete Windows Live run
passes 78 cases with zero skips against a disposable PostgreSQL 18 fixture
(`postgres:18-alpine@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873`).
Earlier offline runs passed 67 and skipped seven database cases; they are kept
separate from the later database run. The API budget remains 13,571 signatures
across six families. These counts are development validation, not a release
verdict. Raw results are retained under `artifacts/audit-live-*-20260927`,
`artifacts/audit-live-*-results-20260927` and `artifacts/dev`.

Release-track self-tests accept the five core families and Graph preview,
reject Graph stable, and reject eight invalid policy mutations. Performance
evidence schema 3 separately validates the complete Core and Graph preview
matrices, including 25 rejection fixtures. These are verifier self-tests with
synthetic data, not new measured wins. The historical V1 candidate aggregator
has not yet been migrated; [release tracks](releases/release-tracks.md) explains
that remaining publication gate.

The next slice separates actual evidence producers and readers. Core packaging
and approval verification no longer require Graph packages or Graph pilot
coverage. Core endurance uses digest-pinned PostgreSQL 18, retaining the exact
72/24/24-hour sequence. The core Kubernetes database has separate storage and
Graph preview has a separate candidate ConfigMap: no historical database volume
is downgraded. Sync validation rejects duplicated or substituted projects even
when the list still has nine entries. Core build compatibility covers stable
15–18; the historical Beta 3 matrix is a separate manual preview workflow.

Synthetic core self-tests accept ten correctly scoped approvals and reject six
bad sets; accept a seven-run workflow set and reject nine bad sets; accept two
endurance reports and reject 26 altered reports. They do not represent operator
approval, successful CI or elapsed endurance. The draft aggregation contract
preserves 100,000 Live/Control Plane cycles, not a reduced minimum. The final
remote aggregation, complete current performance evidence and actual endurance
runs remain open, and all publication flags remain disabled.

The contributor client command builds all five clients and runs eight available
tests across the core, Svelte and Vue packages. Angular/React-specific tests are
still part of the browser work to expand; they are not counted as executed here.
The website command generates 140 guides and 150 crawlable/prerendered routes,
then passes production checks for hashed assets, metadata, source-map exclusion
and size limits. It does not deploy the website or establish field mobile/CWV
acceptance.

A clean SDK artifacts build exposed a protobuf analyzer configuration tied to
the default `obj` location. The generated-file exception now lives in the root
editor configuration and covers the exact generated protobuf filenames in both
default and repository artifact directories. Handwritten APIs remain checked.

The current hosting check found all three existing DigitalOcean workers
powered off and all nodes `NotReady` with `NodeStatusUnknown`. The cluster API
reports `error`. Power-on requests for the existing workers were rejected with
HTTP 403 and an account-lock message. The account owner must resolve that lock
through the DigitalOcean control panel/support before infrastructure recovery
can proceed. No replacement infrastructure was provisioned.

## Execution order

Migrate the 1.2 exact-candidate aggregator to the independent Graph release
policy; accept or reject the pool candidate; remove server/browser
full-result work; close the spool, multiplexing, EF and connector gaps; improve
fleet/dashboard operations and current documentation; assemble final release
evidence. Infrastructure recovery can proceed as soon as the account is unlocked.
