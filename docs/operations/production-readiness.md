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
is intentionally impossible to pass with the checked-in example evidence.

## What V1 measures

The V1 evidence set covers four different questions. They must not be collapsed
into one number.

| Evidence | Question answered | Authority |
| --- | --- | --- |
| Correctness and compatibility | Does the implementation satisfy its declared contract? | Unit, integration, specification, fuzz, stress and package tests |
| Reference-machine benchmarks | Did a known code path regress on the controlled machine? | 89 BenchmarkDotNet results, 37 allocation budgets, 18 latency budgets and locked multiplexing comparisons |
| Production SLOs | Is one deployed application meeting its reliability objectives? | 60 runtime instruments, 14 SLOs, Prometheus rules and deployment telemetry |
| Release acceptance | Is this exact immutable candidate safe to publish? | Manual workflows, endurance, GA evidence, rehearsals, pilots and named approvals |

Benchmark latency is not a production SLO. A 53 ns in-memory codec result says
nothing about a database across a network, and a five-second delivery SLO is
not permission for a microbenchmark to regress. The two systems have different
owners, environments and failure actions.

## Deterministic engineering gates

`verify-v1-production-readiness.ps1 -Mode Engineering` executes these gates:

1. solution ownership and dependency layout;
2. documentation links and generated-source contract;
3. exact public-API budgets for all six product families;
4. pinned workflow actions and supply-chain source controls;
5. the current digest-pinned PostgreSQL 19 programme record;
6. complete, non-empty benchmark coverage for every `[Benchmark]` method;
7. allocation, reference latency and multiplexing performance budgets;
8. the six-meter, 60-instrument telemetry contract;
9. all 14 reference SLOs, alerts, dashboard panels and Collector safety
   controls; and
10. fail-closed publication policy.

The normal build still compiles, tests and packages the full solution. This
gate validates the production contracts around those outputs.

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

The artifact contains:

- brief JSON and Markdown reports for the full fixture inventory;
- the full multiplexing report with raw result measurements;
- the BenchmarkDotNet log;
- a SHA-256-bound environment/evidence manifest; and
- the exact source commit, SDK/runtime, OS, processor and PostgreSQL image.

The manifest hashes every report and the complete log with its byte count; the
candidate verifier rejects an unregistered or altered result.

Reference budgets have 25% headroom over the named checked-in environment.
Change a budget only with a fresh report, an explanation of the trade-off and
review by the performance owner. Never loosen a budget merely to make CI green.

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
├── streams/
│   ├── report.json
│   └── candidate-sbom/build-provenance.json
├── sync/
│   ├── report.json
│   └── candidate-sbom/build-provenance.json
├── performance/
│   ├── multiplexing-evidence.json
│   └── results/*.json
└── approvals/
    ├── independent-release-review.json
    ├── security-review.json
    ├── application-pilot-a.json
    ├── application-pilot-b.json
    ├── backup-restore-rehearsal.json
    ├── rollback-rehearsal.json
    ├── incident-response-game-day.json
    ├── slo-owner-approval.json
    └── maintainer-signoff.json
```

Start from
[`eng/v1-candidate-evidence.example.json`](../../eng/v1-candidate-evidence.example.json)
and
[`eng/v1-approval-evidence.example.json`](../../eng/v1-approval-evidence.example.json).
Replace every placeholder. Approval files are content-addressed from the
candidate manifest; each one must identify the required gate, exact candidate
commit, accountable person, UTC approval time, acceptance summary and zero
blocking findings.

Run the final gate from a clean checkout at that exact commit:

```powershell
./eng/verify-v1-production-readiness.ps1 `
  -Mode Candidate `
  -Commit '<40-character-candidate-sha>' `
  -EvidencePath 'D:/release-evidence/v1-evidence/candidate.json'
```

For the publication-grade run, configure a protected
`v1-candidate-readiness` GitHub environment with required reviewers and the
`V1_APPROVAL_EVIDENCE_BASE64` secret. The secret is a base64-encoded ZIP whose
`approvals` directory contains the nine exact approval JSON files. Dispatch
`.github/workflows/v1-candidate-readiness.yml` with the candidate SHA and the
four successful workflow run IDs. It queries GitHub for every run, downloads
the non-expired performance/endurance artifacts, constructs the
content-addressed manifest, executes candidate mode, and archives the verified
bundle.

Candidate mode proves all of the following:

- exactly one successful manual `build.yml`, performance, Streams 72-hour and
  Sync 24-hour run is recorded for the candidate SHA;
- the Streams report contains at least 100,000 transactions over at least 72
  hours, fault-injection counters, bounded/empty final storage and package
  provenance;
- the Sync report contains at least 100 cycles over at least 24 hours across
  all six projects and three digest-pinned destination services;
- both endurance reports, package provenance and service images match the
  candidate;
- fresh performance results pass coverage, latency, allocation and
  multiplexing budgets on the reference machine;
- PostgreSQL 19 is a verified GA milestone rather than a beta or RC; and
- every required approval file has a matching SHA-256 and candidate commit.

Moving a tag, editing a workflow, changing a dependency, altering a package
version, changing a publication policy or fixing any candidate code creates a
new commit and invalidates the evidence.

## Operational acceptance

### Application pilots

Run at least two independently operated, representative applications. Each
pilot record must define traffic and data shape, PostgreSQL topology, enabled
families, duration, expected SLOs, upgrade/rollback path, observed resource
limits, defects and acceptance owner. A demo or maintainer-only sample is not
an independent pilot.

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
protected V1 candidate-readiness workflows at its release commit; Streams and
Sync additionally require their own endurance workflow. The release verifier
queries GitHub by exact `head_sha`, so a local or fabricated candidate manifest
cannot substitute for the protected readiness workflow.

Release order remains Provider, Streams, Sync/Live, Control Plane, then
Continuous Graph preview. Stop if a dependency package is unavailable, an SLO
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

As of 2026-08-04, deterministic engineering work can be verified locally, but
stable V1 remains blocked by external facts: PostgreSQL 19 GA is not yet the
recorded milestone, the exact final candidate workflows have not run, the
72-hour/24-hour endurance artifacts do not yet exist for that SHA, and the
independent pilots/rehearsals/approvals are not complete. Publication switches
must remain disabled until candidate mode passes.
