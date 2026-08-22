# V1 production readiness

BlueTusk has two deliberately different readiness states:

- **engineering ready** means the repository, packages, tests, public API,
  security controls, telemetry contract, benchmark corpus and operational
  assets pass deterministic checks; and
- **candidate ready** means one immutable commit has also completed the real
  release workflows, endurance runs, reference-machine performance run,
  PostgreSQL 19 GA verification and accountable human/operational acceptance.

Engineering readiness is necessary but does not authorise stable publication.
The machine-readable contract is
[`eng/v1-production-readiness.json`](../../eng/v1-production-readiness.json),
and the verifier defaults to the safe engineering mode:

```powershell
./eng/verify-v1-production-readiness.ps1
```

The command must finish with every publication switch disabled. Candidate mode
instead requires all six stable policies armed in the immutable reviewed
`origin/main` commit, with no release tags or candidate packages published.
It is intentionally impossible to pass with the checked-in example evidence.

## What V1 measures

The V1 evidence set covers five different questions. They must not be collapsed
into one number.

| Evidence | Question answered | Authority |
| --- | --- | --- |
| Correctness and compatibility | Does the implementation satisfy its declared contract? | Unit, integration, specification, fuzz, stress and package tests |
| Reference-machine benchmarks | Did a known code path regress on the controlled machine? | 98 BenchmarkDotNet results, 46 allocation budgets, 19 latency budgets and locked multiplexing comparisons |
| Website delivery | Is the documentation and evidence surface bounded and deployable? | Hashed production output, raw/Brotli bundle budgets, static metadata and the archived build report |
| Production SLOs | Is one deployed application meeting its reliability objectives? | 60 runtime instruments, 14 SLOs, Prometheus rules and deployment telemetry |
| Release acceptance | Is this exact immutable candidate safe to publish? | Manual workflows, endurance, GA evidence, rehearsals, pilots and named approvals |

Benchmark latency is not a production SLO. A 53 ns in-memory codec result says
nothing about a database across a network, and a five-second delivery SLO is
not permission for a microbenchmark to regress. The two systems have different
owners, environments and failure actions.

## Deterministic engineering gates

`verify-v1-production-readiness.ps1 -Mode Engineering` executes these gates:

1. solution ownership and dependency layout;
2. the package-only Clean Architecture application suite, backend/UI container
   runtime closure and fail-closed live deployment health contract;
3. documentation links and generated-source contract;
4. exact public-API budgets for all six product families;
5. the synchronized nine-target fuzzing contract, encoded corpus coverage and
   bounded execution policy;
6. the Angular website production contract, delivery budgets, metadata and
   automatic emitted-build verification;
7. pinned workflow actions and supply-chain source controls;
8. the declared protected-branch, required-check and deployment-environment
   governance contract;
9. the current digest-pinned PostgreSQL 19 programme record;
10. complete, non-empty benchmark coverage for every `[Benchmark]` method;
11. allocation, reference latency and multiplexing performance budgets;
12. the six-meter, 60-instrument telemetry contract;
13. all 14 reference SLOs, alerts, dashboard panels and Collector safety
   controls; and
14. fail-closed publication policy.

The normal build still compiles, tests and packages the full solution. This
gate validates the production contracts around those outputs.

The website delivery budgets and their exact emitted-build measurements are
documented in [website production](website-production.md). They are synthetic
release regression gates, not a substitute for pilot traffic and field Core
Web Vitals.

## GitHub governance gate

[`eng/v1-github-governance.json`](../../eng/v1-github-governance.json)
defines the repository settings that cannot live in a workflow file. Source
mode verifies that every required workflow is bound to the expected
environment and that the contract remains fail closed:

```powershell
./eng/verify-github-governance.ps1
```

Remote mode queries GitHub and is mandatory inside both the exact-candidate
and tag-release paths:

```powershell
./eng/verify-github-governance.ps1 -Mode Remote -Repository owner/repository
```

The active `main` ruleset must prevent deletion and force pushes, require a
pull request, require a fresh independent approval after the last push,
require resolved review threads, and require every V1 build, integration,
security, fuzzing, website and observability check against the latest `main`.
The same remote gate requires an SPDX 2.3 dependency graph with package
evidence, vulnerability alerts, active automated security fixes, and private
vulnerability reporting.

Configure `v1-candidate-readiness` with at least one eligible reviewer,
prevent self-review, disable administrator bypass, and allow only protected
branches. Configure `package-production` and `package-prerelease` with the same
non-bypassable reviewer protection and custom deployment patterns for the six
release-tag prefixes. Repository administrators must create these settings
before dispatching a candidate. All three environments must
hold a `V1_GOVERNANCE_TOKEN` secret whose fine-grained repository permissions
include Administration read, Actions read, Contents read and Environments
read; the workflows use it only inside their protected environment to inspect
settings and secret names, never secret values, and never write settings.
Merely naming an environment in YAML is not protection: GitHub can create a
referenced environment without reviewer rules, so both release workflows
verify the live API state before accepting evidence or publishing.

As of 2026-08-17 the active `main` ruleset, all three environments, all twelve
RC/stable tag policies and the repository security features satisfy their
structural settings checks. The full remote contract remains fail closed until
its eight declared environment-secret bindings are provisioned. The repository
also needs another eligible human reviewer: its only current collaborator is
the owner, and non-bypassable prevent-self-review intentionally leaves
owner-initiated deployments waiting for an independent approval.

## Reference performance gate

The V1 performance workflow is manual because public hosted runners are not a
stable performance environment. It targets the labelled
`self-hosted, windows, x64, bluetusk-benchmark` runner and verifies that the CPU
is an AMD Ryzen 7 5800X before measuring.

Dispatch `.github/workflows/performance.yml` with:

- the full candidate SHA; and
- confirmation `RUN-V1-PERFORMANCE-GATE`.

The workflow checks out that SHA directly, starts the digest-pinned current
PostgreSQL 19 image, and runs every fixture with BenchmarkDotNet MediumRun and
the in-process toolchain. `--inProcess` is required for this repository because
BenchmarkDotNet's generated-project discovery can otherwise find retained
worktree artifacts. A zero exit code alone is not accepted: the verifier rejects
empty reports, missing statistics, stale methods, missing fixtures and known
failure markers in the log.

Provider-relative multiplexing latency is measured a second way to remove
sequential-provider order drift from the release decision. After 64 warm-up
bursts per provider, the runner records five trials of 501 alternating paired
blocks; every block executes 32 real 64-command bursts for each provider and
reverses order from the preceding block. The verifier recomputes mean, P95 and
P99 ratios for each trial from raw per-operation block timings and applies the
unchanged provider budgets to the median of the five trial ratios. This paired
phase runs before the long BenchmarkDotNet suite. With 501 observations,
each trial's P99 is the sixth-slowest block rather than a statistic decided by
one or two scheduler spikes. BenchmarkDotNet
remains authoritative for BlueTusk's absolute P95 limits and all allocation
limits. The fail-closed self-test rejects truncated samples, invalid values,
wrong order, duplicate workloads and a synthetic 6% regression.

The artifact contains:

- brief JSON and Markdown reports for the full fixture inventory;
- the full multiplexing report with raw result measurements;
- the raw alternating-provider multiplexing report;
- the BenchmarkDotNet log;
- a SHA-256-bound environment/evidence manifest; and
- the exact source commit, SDK/runtime, OS, processor and PostgreSQL image.

The manifest hashes every report and the complete log with its byte count; the
candidate verifier rejects an unregistered or altered result.

Reference budgets have at most 25% headroom over the named checked-in
environment. The original `WriteSimpleQuery` ceiling was derived from a
three-sample ShortRun and did not represent the two internally stable launch
bands later observed on the same Ryzen 7 5800X. Its release ceiling is therefore
calibrated from two independent MediumRun workflow artifacts: the larger
26.578 ns mean and 31.624 ns P95 receive the same 25% headroom and are rounded
up to 35 ns and 40 ns. The budget file records both exact commits and workflow
run IDs. Its self-test proves that duplicate runs, invalid commits, false
maxima, and any ceiling above the evidence-derived limit fail closed. No other
absolute latency ceiling changed.

Change a budget only with fresh immutable reports, an explanation of the
trade-off and review by the performance owner. Never loosen a budget merely to
make CI green.

## Production observability acceptance

The reference deployment must register all six meters, export through an
internal OpenTelemetry Collector, load the Prometheus rules and import the
versioned Grafana dashboard. The complete setup and alert runbooks are in
[production observability](observability.md).

Before sign-off, the SLO owner must demonstrate:

1. actual OTLP-to-Prometheus name translation matches every rule;
2. all 60 expected instruments appear under controlled exercise;
3. metric dimensions remain within the reviewed cardinality classes;
4. every alert routes to an owned destination and links to a usable runbook;
5. Collector loss, queue saturation and dropped telemetry are themselves
   visible;
6. application traffic continues safely if telemetry export is unavailable;
7. no SQL, parameter, token, tenant or payload data is exported; and
8. the deployment accepts the reference SLOs or records an explicit reviewed
   override.

The reference error-budget policy pages on fast 14.4-times burn, creates a
ticket on sustained 6-times burn, freezes non-remediation rollout when the
30-day budget is exhausted, and resumes only after measured recovery.

## Immutable candidate evidence

Create a directory outside the tracked source tree from the downloaded workflow
artifacts and approval records:

```text
v1-evidence/
├── candidate.json
├── packages/
│   ├── package-manifest.json
│   ├── packages/*.{nupkg,snupkg,tgz}
│   └── sbom/
│       ├── bluetusk.cdx.json
│       ├── bluetusk.spdx.json
│       └── build-provenance.json
├── website/
│   ├── production-metrics.json
│   └── hashed production assets
├── streams/
│   ├── report.json
│   └── candidate-sbom/build-provenance.json
├── sync/
│   ├── report.json
│   └── candidate-sbom/build-provenance.json
├── continuous-graph/
│   ├── report.json
│   └── candidate-sbom/build-provenance.json
├── disturbances/
│   ├── operational-disturbance-evidence.json
│   ├── streams/*-injection.json and *-recovery.json
│   └── sync/*-injection.json and *-recovery.json
├── performance/
│   ├── multiplexing-evidence.json
│   ├── multiplexing-paired-evidence.json
│   └── results/*.json
└── approvals/
    ├── independent-release-review.json
    ├── security-review.json
    ├── application-pilot-a.json
    ├── application-pilot-b.json
    ├── website-deployment-acceptance.json
    ├── backup-restore-rehearsal.json
    ├── rollback-rehearsal.json
    ├── incident-response-game-day.json
    ├── slo-owner-approval.json
    └── maintainer-signoff.json
```

Start from
[`eng/v1-candidate-evidence.example.json`](../../eng/v1-candidate-evidence.example.json)
and
[`eng/v1-approval-evidence.examples.json`](../../eng/v1-approval-evidence.examples.json).
Replace every placeholder. Approval files are content-addressed from the
candidate manifest and validated against the exact gate-specific contract in
[`eng/v1-approval-evidence-contract.json`](../../eng/v1-approval-evidence-contract.json).
Each one must identify the required gate, exact candidate commit, accountable
person, UTC approval time, acceptance summary, zero blocking findings and the
required measured details. Each approval must cite at least one retained HTTPS
evidence record; a blank, local-path-only, generic narrative or untraceable
approval fails candidate mode. See
[V1 operational approval evidence](approval-evidence.md) for every required
field, threshold and retained record.

Run the final gate from a clean checkout at that exact commit:

```powershell
./eng/verify-v1-production-readiness.ps1 `
  -Mode Candidate `
  -Commit '<40-character-candidate-sha>' `
  -EvidencePath 'D:/release-evidence/v1-evidence/candidate.json'
```

For the publication-grade run, configure a protected
`v1-candidate-readiness` GitHub environment with required reviewers, the
read-only `V1_GOVERNANCE_TOKEN` secret described above, and the
`V1_APPROVAL_EVIDENCE_BASE64` secret. The approval secret is a base64-encoded
ZIP whose `approvals` directory contains the ten exact approval JSON files and
whose `disturbances` directory contains the reviewed operational-disturbance
report plus its 28 content-addressed injection/recovery summaries. Dispatch
`.github/workflows/v1-candidate-readiness.yml` with the candidate SHA and the
seven successful workflow run IDs: build, security, one-hour-per-target fuzzing,
performance, Streams endurance, Sync endurance and ContinuousGraph endurance.
It queries GitHub for every
run; downloads the non-expired package, website, performance and endurance
artifacts; constructs the content-addressed manifest; executes candidate mode;
and archives the verified bundle.

Candidate mode proves all of the following:

- exactly one successful manual build, security, one-hour-per-target fuzzing,
  performance, Streams 72-hour, Sync 24-hour, and ContinuousGraph 24-hour run
  is recorded for the
  candidate SHA, with a unique positive run ID/attempt, HTTPS URL and
  non-future completion timestamp after the candidate commit; no extra or
  duplicate workflow records are accepted;
- the intentional test-credential inventory passes inside the exact security
  run, and every external secret-scanner finding is independently resolved or
  accepted with a retained review reference;
- the archived Angular production metrics match the candidate, every emitted
  website file matches its recorded length and SHA-256, and all delivery
  budgets pass;
- all six product-family package sets have exact archive inventories and
  metadata, every NuGet/npm and symbol archive matches its byte length and
  SHA-256, and the CycloneDX 1.6, SPDX 2.3 and provenance records are bound to
  the same candidate commit;
- the Streams report contains at least 100,000 transactions over at least 72
  hours, fault-injection counters, bounded/empty final storage and package
  provenance;
- the Sync report contains at least 100 cycles over at least 24 hours across
  all six projects and three digest-pinned destination services;
- the ContinuousGraph report contains at least 100,000 evaluations over at
  least 24 hours, at least 99.9% committed outcomes, lifecycle P95 at or below
  one second, positive authoritative-repair, replay-restart,
  cancellation-cleanup, and PostgreSQL disconnect-recovery evidence, and zero
  ordering or reconciliation errors;
- all three endurance reports, package provenance and service images match the
  candidate;
- all seven production disturbances passed inside each exact endurance report
  window: 14 recoveries with zero blockers or observed data loss, backed by 28
  unique SHA-256-bound injection/recovery records;
- fresh performance results pass coverage, latency, allocation and
  multiplexing budgets on the reference machine;
- PostgreSQL 19 is a verified GA milestone rather than a beta or RC; and
- every required approval file has a matching SHA-256 and candidate commit,
  passes its gate-specific measurement contract and was approved after the
  latest exact workflow completed; the two pilots have distinct applications,
  operators and approvers, collectively cover all six families, include
  ContinuousGraph in at least one pilot, and website acceptance names the exact
  production-metrics hash, independent review follows all operational
  approvals, and maintainer sign-off is the final decision.

Moving a tag, editing a workflow, changing a dependency, altering a package
version, changing a publication policy or fixing any candidate code creates a
new commit and invalidates the evidence.

The canonical package artifact, reconstruction verifier and publication
separation are documented in
[canonical V1 package evidence](package-evidence.md).
The cross-run operational matrix and protected evidence layout are documented
in [endurance disturbance evidence](endurance-disturbance-evidence.md).

## Operational acceptance

### Pre-pilot platform acceptance

No application observation counts toward a pilot while the deployment control
plane is only reporting cached desired state. Run
`./eng/verify-application-platform-health.ps1 -RequireApplications` against the
selected cluster immediately after the digest-pinned rollout and retain its
output. The verifier independently compares every non-terminal API pod with the
Ready kubelet that must be running it, rejects node pressure, rejects any
non-healthy Longhorn volume or CloudNativePG cluster, and requires the API,
worker and UI deployments for all three reference applications to be fully
observed, ready and available with no unready container or failed migration job.
The protected image workflow must also have executed each backend image and
proved the exact .NET and ASP.NET Core shared-framework closure. A green rollout
status without both checks is not production evidence.

The complete check matrix, safe failure interpretation and recovery sequence are
in [application platform health and rollout acceptance](application-platform-health.md).

### Application pilots

Run at least two independently operated, representative applications. Each
pilot record must define traffic and data shape, PostgreSQL topology, enabled
families, duration, expected SLOs, upgrade/rollback path, observed resource
limits, defects and acceptance owner. A demo or maintainer-only sample is not
an independent pilot. Each pilot must run for at least 24 hours and record at
least 1,000 operations and 100 transactions; the two records must have distinct
applications, operator organisations and accountable approvers. Collectively
they must cover all six product families, and at least one must exercise
ContinuousGraph.

### Website deployment acceptance

Record the selected public origin, certificate and TLS result, SPA fallback,
hashed-asset and `index.html` cache policy, Brotli/gzip behavior, security
headers, broken-link crawl, supported desktop/mobile browsers and representative
field LCP, INP and CLS. The approval must reference the exact archived website
artifact and candidate commit; localhost laboratory measurements alone do not
pass this gate. V1 requires at least 100 field samples over 28 days with p75
LCP at most 2,500 ms, INP at most 200 ms and CLS at most 0.1.

### Backup and restore

Restore into an empty isolated environment, not over the source. Record backup
identifier and encryption, object/row counts, checkpoint positions, start/end
times, RPO/RTO, reconciliation result, integrity hashes and operator. A backup
that has not been restored is not evidence.

### Rollback

Exercise application/package rollback without mutating durable protocol
formats. Confirm version compatibility, connection drain, relay/checkpoint
ownership, Live client reset behavior, Control Plane fencing and post-rollback
reconciliation. Record the trigger and decision authority.

### Incident game day

Inject a representative dependency failure or saturation event. Operators must
detect it from shipped telemetry, identify the affected SLO, follow the
runbook, preserve durable state, mitigate or roll back, and complete a
time-stamped incident record with follow-up owners.

## Publication and rollback authority

The protected `package-production` environment remains the credential and
final human-approval boundary. Only exact version tags can publish. Every
product family requires successful manual build, reference-performance and
protected V1 candidate-readiness workflows at its release commit; Streams,
Sync, and ContinuousGraph additionally require their own endurance workflows.
The release verifier
queries GitHub by exact `head_sha`, so a local or fabricated candidate manifest
cannot substitute for the protected readiness workflow.

Release order is Provider, Streams, Sync, Live, Control Plane, then
ContinuousGraph. Stop if a dependency package is unavailable, an SLO
is burning, evidence points to another SHA, or a protected reviewer declines.

For a package defect:

1. stop rollout and preserve logs, traces, metrics and package hashes;
2. roll the application back to the last compatible package set;
3. do not delete replication slots, advance checkpoints or bypass fencing;
4. reconcile destinations and Live replay state;
5. invalidate the candidate evidence; and
6. produce a new immutable candidate after the fix.

NuGet versions are immutable. Recovery is a new fixed version plus application
rollback/pinning, not replacing an already published archive.

## Current V1 boundary

As of 2026-08-17, deterministic engineering work can be verified locally, but
stable V1 remains blocked by external facts: PostgreSQL 19 GA is not yet the
recorded milestone, the exact final candidate workflows have not run, the
72-hour Streams, 24-hour Sync, and 24-hour ContinuousGraph endurance artifacts
and the required in-window disturbance
recoveries do not yet exist for that SHA, and the
independent pilots/rehearsals/approvals are not complete. The live repository
settings satisfy the declared protection policy, but the required environment
secrets and a second eligible reviewer are still needed before either
protected environment can complete an owner-initiated deployment. The six
current GitGuardian findings refer to inventoried disposable test credentials,
but still require explicit external false-positive disposition before the
security approval can pass. Publication switches remain disabled during
preparation. The final post-GA arming PR creates the candidate; tags and
protected production approval remain the actual publication boundary.
