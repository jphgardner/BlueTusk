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
| Separate Graph release readiness from the other families | Policy, scoped evidence, stable core CI/endurance and isolated Graph application/database/config implemented; final candidate aggregation pending | Core helper self-tests, actual package/SBOM checks and separate local application captures; exact 1.2 remote candidate aggregation still required |
| Live refresh/replay recovery | Implemented; Windows PostgreSQL validation passed | 78 tests pass with zero skips, including real PostgreSQL stores and SSE/SignalR/gRPC transports; final-candidate platform and endurance gates remain |
| Contributor setup and focused validation | Implemented; local command validation passed | Doctor/project registration, missing-database refusal, hashed TRX summary, focused Check, five client builds/53 client tests, diagnostic fixture self-tests, generated guides and production website build; remote Windows/Linux client jobs still require execution |
| Pool candidate performance acceptance | Pending | Matched before/after captures for `dd1da1a`, then the complete reference comparisons with latency, allocation, CPU and RSS |
| Server incremental result costs | Pending | Small-change work scales with affected rows; immutable historical snapshots; ordered top-N and repair correctness; unchanged Graph tier cost targets |
| Browser reducer costs | Bounded batching and local correctness implemented; paired diagnostic retained; full performance acceptance pending | Every event remains validated; valid-prefix tokens and historical snapshots tested; large-result update/churn/rerank captures retained; tiny-reset costs, real browsers, Linux, slow clients, fan-out and confidence-qualified latency/allocation still open |
| Spool completion stalls | Pending | Unchanged end-to-end 4 MiB P95 budget passes on isolated storage; crash recovery retains flush, checksum and acknowledgement guarantees |
| Multiplexing and EF performance | Pending | Existing absolute limits and confidence-qualified paired workload targets pass; no hidden allocation or memory regressions |
| Connector coverage and overhead | Seven destinations now required by the performance contract; measurements and optimisation pending | Profile and measure Kafka, S3 and Webhooks alongside the four earlier destinations; retain transaction ordering, durability, retry and quarantine semantics |
| Control Plane degraded fleet operation | Pending | Bounded per-instance deadlines, partial/stale status, 1/100/1,000-source scaling and failed-instance tests |
| Dashboard scale and maintainability | Pending | Direct detail access, bounded server-side inventory queries, mobile/keyboard/large-inventory browser evidence, separated presentation assets |
| Website availability | External blocker identified | DigitalOcean account unlocked; existing workers Ready; Traefik/site healthy; external HTTPS, CSS and documentation routes verified |
| One current documentation truth | Pending | Stable/preview/candidate distinctions, generated support/version information, runnable package examples and guide journeys |
| Final release evidence | Pending | One immutable candidate, required CI, consumers, supply chain, endurance, backup/restore and rollback rehearsals and independent approval; independent pilots are not a 1.1.0 gate (owner delegation, 2026-10-04) |

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

The evidence-producer slice separates actual producers and readers. Core packaging
and approval verification no longer require Graph packages or Graph pilot
coverage. Since the 2026-10-04 owner delegation, 1.1.0 Core approval
verification requires no pilot records at all. Core endurance uses digest-pinned PostgreSQL 18, retaining the exact
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

Real core package capture at `e19b58919b187fe5298fddf00fbf8eeaadd51b84`
produced 65 NuGet packages, 62 symbol packages and five npm tarballs: 132
artifacts, 20,123,936 bytes. Both SBOMs contain 371 components/packages. An
end-to-end reader check exposed misuse of a prerelease-only version override;
the reader now validates exact stable source versions before using the normal
family package verifier. The real-package regression test accepts the stable
set without arming a prerelease train and rejects eight altered identity,
scope or integrity records. The failed initial reader log and the corrected
verification are retained separately. This is package validation, not registry
publication or final-candidate certification.

An additional application-CI coupling has been removed. The old runner read the
current PostgreSQL 19 milestone and required a Beta 3 image, so recording the
untested Beta 4 milestone caused it to fail before running any test. Core plans
now use the stable core fixture without reading that programme. Graph plans use
the explicitly verified historical milestone and stay separate. Orders runs in
core CI; Topology and Fraud run in manual historical preview CI. All three
applications remain compiled and architecture-checked. The existing protected
`PostgreSQL 19 live matrix` context is restored as provider-only preview
compatibility; branch protections were not changed. The SQL/PGQ capability tests
now check the actual server catalogue rather than assuming support by major.

Application reader self-tests accept both track-specific synthetic TRX sets and
reject 12 invalid plans and 26 invalid result sets. A malformed Graph programme
does not change the core plan. Real local development captures pass the Orders
journey on PostgreSQL 18 and both Graph journeys on historical Beta 3, with zero
skips. They retain hashed TRX and source-cleanliness reports under
`artifacts/audit-application-*-b126da6-20260927-first`. These runs use the retained
application RC dependencies and a dirty development checkout, not newly built
exact-candidate packages or remote release evidence. The final core aggregator,
full current performance evidence, endurance and independent approval remain
open.

Five focused provider integration cases also pass with zero skips on both
PostgreSQL 18 and historical Beta 3. They cover synchronous provider operations
and capability-dependent property-graph behaviour: absence on the stable server
and actual DDL/query/inspection on the historical preview server. Raw TRX are
retained in `artifacts/audit-provider-capabilities-b126da6-20260927` and
`artifacts/audit-provider-capabilities-beta3-b126da6-20260927`. These are focused
development checks, not the full compatibility matrix. Existing-evidence and
workspace-root output targets are refused before any container starts.

The earlier contributor capture built all five clients and ran eight tests
across core, Svelte and Vue. The later browser slice adds actual Angular signals
and dependency-injection tests plus React DOM lifecycle, request replacement,
StrictMode, server-rendering and SSE tests. The strict command now requires all
five packages' build/test scripts rather than silently skipping missing suites.
Local validation passes 53 tests with zero skips: 42 core, four Angular, five
React, one Vue and one Svelte. The four diagnostic scenario self-tests are
separate from that count. Locked dependencies audit with zero vulnerabilities;
CI now defines the same checks on Windows/Linux, but those jobs have not run.
The website command generates 140 guides and 150 crawlable/prerendered routes,
then passes production checks for hashed assets, metadata, source-map exclusion
and size limits. It does not deploy the website or establish field mobile/CWV
acceptance.

## Browser reduction evidence

The core client now reduces bounded batches before constructing a result array,
instead of reconstructing the full array on every event and batching only the
framework notification afterward. Its default is 64 already available frames,
with immediate partial-batch publication at the end of each read and no added
timer. An unchanged-index update avoids key search and array shifts; reset swaps
the validated replacement map instead of duplicating every entry. Published
arrays remain unchanged by later events. Rank changes still shift array entries
and each published snapshot still materializes the result; server incremental
costs and all browser full-result work are not claimed closed.

The tests cover 2,000 seeded changes against an independent ordered-array model,
301-event bounded publication, per-event compatibility mode, in-batch duplicates,
sequence gaps, valid-prefix persistence, reset/restart with lower sequences,
stale fetch completion, pending-read cancellation, every UTF-8/CRLF byte split,
token-callback failure, retry-listener cleanup and adapter teardown. The initial
24-case regression run against the Git-exported `3e2b1aa` client passed five and
failed 19 with no skips. The first attempt was stopped after exposing a callback
error/reconnect loop; the bounded completed regression log is separate. Twelve
later tests were added afterward and are not counted as 19 baseline failures.

The first Windows x64/Node 24.15.0 diagnostic captures 12 cases with seven
fresh-process pairs each, two warm-ups, alternating order, identical encoded
bursts and matching independently computed final-state hashes. At 100,000 rows,
128 indexed updates measured 490.029 ms before and 8.418 ms after; removals/adds
434.782/11.720 ms; reranking 399.271/14.948 ms. Each delta burst publishes two
snapshots rather than 128, with sampled allocation ratios around 0.018. A full
100,000-row reset measured 29.336/23.590 ms and a 0.663 sampled allocation ratio.
Ten-row single-reset timing regressed in this capture, while tiny allocation
estimates are poorly resolved. These weaknesses remain open; this is not an
across-the-board performance pass.

Raw measurements, V8 sampling profiles, GC/CPU/whole-process RSS, source/module
hashes, environment and a 171-entry hashed inventory are retained under
`artifacts/audit-browser-performance-3e2b1aa-20260927`. The capture records a
dirty development source hash, not final-candidate qualification. Statistical
heap sampling is not exact allocated bytes, and seven empirical duration
samples cannot certify tail confidence intervals. No SignalR, Linux, browser
rendering, fan-out or endurance win is implied. The diagnostic deliberately uses
a non-release evidence kind and leaves the full release-performance contract
unchanged.

The completed 48-test/four-fixture contributor log is retained at
`artifacts/audit-browser-clients-3e2b1aa-20260927-first.log`. Five subsequent cases
also verify that invalid reset/reorder arrays and keys fail with a protocol
error without replacing or losing the valid prefix. The updated diagnostic
reports allocation ratios as unresolved if either side has fewer than 32 V8
allocation samples across the capture; that is a resolution safeguard, not a
95% confidence test. Short-window CPU counters can report zero on this host,
which is not zero CPU work. Earlier raw evidence is never overwritten.

A second capture measures clean commit
`308e05d08da19189b771a693043bf166980878bf` against the same exported reference.
All 12 final-state comparisons pass and all 171 artifact sizes/hashes verify.
For 100,000-row delta bursts, update/churn/rerank means are respectively
397.014/8.115 ms, 406.790/11.610 ms and 421.242/16.464 ms before/after;
sampled allocation ratios are 0.018. The full reset is 30.151/23.674 ms
(0.661 sampled allocation ratio). Ten-row and 1,000-row single-reset timing
ratios remain 1.265 and 1.025, not accepted wins. The ten-row reset allocation
ratio is unresolved by the sampling safeguard. This confirms the large-burst
improvement without closing the smaller-case or full qualification gaps.

The clean report, raw samples, profiles and environment are retained in
`artifacts/audit-browser-performance-308e05d-20260927-clean`; its manifest SHA-256
is `8a77107c6d4a491e70103b426a96e749b5b9958bb2fb0d26371e22c0f5f53db3`.
The clean 53-test/four-fixture log and production-build log are retained at
`artifacts/audit-browser-clients-308e05d-20260927-clean.log` and
`artifacts/audit-browser-website-308e05d-20260927-clean.log`. Both clean runs pass;
the website prerenders all 150 routes and verifies assets, metadata and size
budgets. No remote client CI, registry publication or site deployment occurred.

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
