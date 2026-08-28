# V1 release readiness

BlueTusk `1.0.0` was published on 2026-08-23 under the explicit repository-owner
decision recorded in the
[V1 publication record](releases/1.0.0-publication-record.md). Publication did
not complete the external exact-candidate evidence below: independent approval,
fuzz, reference performance, endurance, and PostgreSQL 19 GA remain open. The
standard release process remains fail closed for later versions.

## Implemented V1 hardening

- Nine bounded parser fuzzing targets, replayable corpus tests, CI/scheduled
  coverage-guided runs, finding archive/minimisation tools, and a
  machine-checked target/corpus/limit/workflow synchronization contract.
- An explicit ADO.NET compatibility contract covering PostgreSQL routines,
  parameter directions, transactions, reader behaviours, schema discovery,
  Dapper, dependency injection, health checks and documented exclusions.
- Exact public-API budgets for all six product families.
- Commit-pinned workflow actions, CodeQL, dependency review, NuGet advisory
  auditing, CycloneDX 1.6 and SPDX 2.3 SBOMs, artifact hashes and build
  provenance.
- A machine-checked intentional test-credential inventory covering 24
  workflow/Compose occurrences by fingerprint, exact path, count and
  local-only context; unknown values, external-host use and release-workflow
  literals fail closed. External scanner disposition remains independently
  required.
- A canonical evidence-only package artifact for all six product families,
  with exact per-family archive reconstruction, package-content verification,
  SHA-256 inventory, SBOM/provenance binding and no registry credentials.
- A digest-pinned PostgreSQL 19 programme with upstream milestone-drift
  detection and a GA-only stable-publication gate.
- Streams, Sync, and ContinuousGraph endurance workflows that bind reports to
  the candidate commit, NuGet/npm hashes, runtime, operating system and
  service-image digests.
- A fail-closed cross-run disturbance contract requiring process death,
  network interruption, controlled storage exhaustion, credential rotation,
  primary failover, backward/forward clock movement and PostgreSQL minor
  upgrade during both exact endurance windows: 14 recoveries and 28 hashed
  injection/recovery summaries.
- A six-meter, 62-instrument telemetry contract; 14 reference production SLOs;
  a deployable OpenTelemetry Collector, Prometheus rules and Grafana dashboard;
  and metric lifecycle tests for every product family.
- Complete checked-in BenchmarkDotNet coverage for 120 measured workloads across
  22 fixtures, 46 allocation budgets, 19 reference-machine latency budgets and
  a manual exact-candidate performance workflow.
- A single fail-closed V1 verifier that distinguishes deterministic engineering
  readiness from PostgreSQL 19 GA, endurance, performance, pilot, recovery,
  game-day and accountable approval evidence for one immutable commit.
- A schema-4 operational approval contract with ten gate-specific evidence
  shapes, measured pilot and field-performance minimums, restore/RPO/RTO
  comparisons, rollback and game-day outcomes, cross-pilot independence checks
  and fail-closed mutation self-tests. Approval schema 4 distinguishes public
  prereleases from unpublished stable-candidate artifacts. Candidate evidence
  schema 3 binds all
  seven workflow run attempts and completion times, rejects stale approvals, orders
  independent review after operational acceptance and makes maintainer
  sign-off the final decision.
- A source-controlled GitHub governance contract that requires 35 named status
  checks, protected `main`, fresh independent review, self-review-protected
  candidate/publication environments, the dependency graph, vulnerability
  alerts, automated security fixes and private vulnerability reporting; both
  release paths verify the live settings through the GitHub API.

The detailed evidence and commands are in the
[hardening programme](hardening-programme.md),
[ADO.NET compatibility matrix](ado-net/compatibility.md),
[fuzzing guide](fuzzing.md),
[PostgreSQL 19 programme](postgresql19-programme.md), and
[release process](release-process.md). The operational definition and evidence
layout are in [V1 production readiness](operations/production-readiness.md).

## Verification snapshot

The combined local verification record through 2026-08-17 produced:

| Gate | Result |
| --- | --- |
| Release build | 121 projects; zero warnings and zero errors |
| Formatting | No changes required |
| PostgreSQL 19 full solution matrix | 3,289 passed, 158 environment-specific skips, zero failures across 45 test assemblies |
| ADO.NET live compatibility suite | 11 passed, including function `IN`/`OUT`/`INOUT`, procedure `CALL`, Dapper, schema and DI health checks |
| Public API budget | 12,991 signatures across six product families |
| Dependency vulnerability audit | No advisory matched in any solution project |
| Provider candidate packaging | 31 NuGet packages and 29 symbol packages verified |
| Candidate SBOM/provenance smoke | 60 artifact hashes and 317 components/packages verified in both SBOM formats |
| Angular website delivery | Initial raw/Brotli, largest lazy chunk and complete distribution are budgeted; hashed assets, metadata and no-source-map policy are verified after every production build |
| Repository gates | Solution layout, documentation links, workflow YAML, PowerShell syntax, Action pins, supply chain and PostgreSQL 19 programme passed |
| Live repository governance | Active `main` ruleset with 35 required checks; three non-bypassable, self-review-protected environments; twelve RC/stable tag policies; dependency graph, alerts, automated fixes and private reporting enabled. The candidate gate remains fail closed on eight missing secret bindings and the absence of an independent eligible reviewer. |

This snapshot validates the tooling and current working tree. It is not
immutable release evidence: the final candidate must be committed, clean, and
rerun by the required workflows at that exact commit.

The separately verified [V1 application suite](v1-applications.md) adds three
package-only applications, PostgreSQL 19 Beta 3 migration/integration coverage,
three browser journeys, exact RC packaging contracts, image evidence, Helm
deployments, and platform preflight tooling. It is RC staging evidence only:
publication/deployment still require protected credentials and approvals, and
the applications do not yet count as formal V1 pilots.

The 2026-08-18 live RC audit rejected that deployment as evidence. Kubernetes
API objects still described workloads as `Running`, but every Ready kubelet
reported zero runtime pods and MicroK8s logged repeated node lookup plus
Kine/Dqlite socket failures. All three worker images also exited immediately
because their package-only hosts reference `Microsoft.AspNetCore.App` while the
published worker base contained only `Microsoft.NETCore.App`. The source fix
uses the digest-pinned ASP.NET Core chiseled runtime, the protected image
workflow now executes every backend image and verifies both exact shared
frameworks, and the RC deployment now cross-checks API state against kubelet,
Longhorn, CloudNativePG, deployments, containers and migration jobs. Candidate
`96b33c3` is retained for audit but superseded; none of its workflow results may
be used as final V1 evidence. A replacement immutable candidate must be frozen
after the cluster is repaired and the corrected images are deployed.

## Gates that must remain open

1. Add at least one additional eligible human reviewer. The repository
   currently has one collaborator, and prevent-self-review correctly blocks
   owner-initiated candidate and publication deployments until another reviewer
   is available.
2. Add a read-only `V1_GOVERNANCE_TOKEN` to all three protected environments with
   Administration read, Actions read, Contents read and Environments read so
   exact-candidate and pre-publication jobs can verify the live settings and
   required secret names.
3. Repair the `proxmox-homelab` MicroK8s datastore/reconciliation failure,
   build and attest all nine corrected application images from a reviewed
   immutable commit, deploy them by digest, and retain a passing
   `verify-application-platform-health.ps1 -RequireApplications` record before
   starting either pilot.
4. After PostgreSQL 19 GA, merge the reviewed final arming PR to `main`. All
   six policies must be stable `1.0.0` and armed in that immutable commit; no
   release tag or candidate package publication may exist yet.
5. Run the complete manual `build.yml` evidence workflow at that commit,
   including PostgreSQL 15–19, PgBouncer, NativeAOT/trimming, connector,
   authentication, stress, website and canonical all-family packaging jobs.
6. Run the manual `security.yml` CodeQL workflow and the manual `fuzzing.yml`
   workflow for at least one hour per parser target at that exact commit. Every
   fuzz target must complete without a crash or hang finding.
7. Run the complete manual `performance.yml` reference-machine workflow at that
   commit and archive its integrity-bound result set.
8. Complete and archive the exact 72-hour Streams, 24-hour Sync, and 24-hour
   ContinuousGraph endurance workflows at the same candidate commit.
   ContinuousGraph must complete at least 100,000 evaluations, commit at least
   99.9%, keep lifecycle P95 at or below one second, record repair, replay
   restart, cancellation cleanup and PostgreSQL disconnect recovery, and
   report zero ordering or reconciliation errors. Perform and content-address
   all required operational disturbances. Any candidate change restarts the
   affected workflows.
9. Repeat PostgreSQL 19 testing for each later beta/RC and GA. Do not describe
   PostgreSQL 19 support as stable before the GA record passes.
10. Complete the
   [independent release review](release-review-handoff.md), two independent
   24-hour application pilots that collectively cover all six families and
   include ContinuousGraph in at least one pilot,
   website deployment acceptance, backup/restore and rollback rehearsal,
   incident game day, security/SLO owner approval and maintainer sign-off.
11. Run `verify-v1-production-readiness.ps1 -Mode Candidate` against the complete
   SHA-256-bound evidence directory.
12. Approve protected production publication and create tags one at a time:
   Provider, Streams, Sync, Live, Control Plane, then ContinuousGraph. Verify
   registry availability, hashes, provenance, installation, and dependency
   resolution after each tag before creating the next.

During preparation all publication switches remain disabled. In the immutable
candidate they are armed; tags and protected `package-production` approval are
the actual publication boundary.
