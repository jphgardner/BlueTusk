# Core products and Graph preview

BlueTusk 1.2 has two readiness tracks. Provider, Streams, Sync, Live and Control
Plane can qualify for release without waiting for Graph. Continuous Graph is
retained as preview work, including its compiler, incremental engine, dashboard
and examples. Sharing a source version does not make every family production
qualified.

| Track | Server support | Required evidence |
| --- | --- | --- |
| Five core families | PostgreSQL 15–18 stable; PostgreSQL 19 preview | Exact-candidate build, security, performance, compatibility, package consumers, durability/endurance, operational rehearsals and independent approval |
| Continuous Graph preview | Historical, digest-pinned PostgreSQL 19 Beta 3 fixture with SQL/PGQ | Separate preview tests and performance results; not production evidence |
| Future Graph stable | A supported server release that actually provides SQL/PGQ | Capability probe, differential/security/recovery tests, unchanged Graph cost limits, 24-hour endurance and independent release approval |

PostgreSQL 19 Beta 4 [removed SQL/PGQ](https://www.postgresql.org/about/news/postgresql-19-beta-4-released-3386/).
PostgreSQL 19 GA alone will therefore not qualify Graph. A future server version
has not been assigned here. Native PostgreSQL 19 `REPACK` remains preview
compatibility work until the GA matrix passes.

## Verify the track and its measurements

`eng/release-tracks.json` defines track membership. `verify-release-track.ps1`
rejects stable Graph publication even if someone enables its package flag.
Core families retain their other gates and dependency order; none depends on
Continuous Graph. All stable package-publication flags remain disabled.

```powershell
./eng/verify-release-track.ps1
./eng/test-release-track-verifier.ps1
./eng/verify-performance-leadership-contract.ps1
./eng/test-performance-leadership-evidence-verifier.ps1

# An evidence manifest must identify its exact scope and commit.
./eng/verify-performance-leadership-evidence.ps1 `
    -EvidencePath artifacts/my-core-evidence/evidence.json `
    -ExpectedCommit <full-40-character-sha> -Scope Core
```

Performance evidence schema 3 names `Core` or `ContinuousGraphPreview`.
`Core` requires every declared core workload, including all seven Sync
destinations, on both Windows and Linux. `ContinuousGraphPreview` requires its
own complete matrix and cannot certify a core release. Preview results cannot
substitute for missing core workloads. Confidence intervals, allocation,
latency, CPU and memory limits have not been reduced. Verifier self-tests use
synthetic fixtures only; their success is not a performance result.

## Remaining release wiring

The build workflow now measures stable PostgreSQL 15–18 compatibility and
produces `v1.2-core-packages-<sha>` independently of optional historical
six-family packaging. The separate `postgresql-preview.yml` workflow retains
the historical PostgreSQL 19 Beta 3 matrix; it is not current Beta 4 support or
stable qualification. Compile-time and shared API checks still cover Graph.
The package-only application job runs Orders on the pinned stable database.
Topology and Fraud integration tests are retained in that manual preview
workflow. Their absence from core runtime validation is explicit, not a set of
skipped Graph tests reported as successful Graph qualification.

The existing protected `PostgreSQL 19 live matrix` status is preserved as a
provider-only check on the last verified historical fixture; it does not run
Continuous Graph or native SQL/PGQ scenarios. The separate preview matrix still
tests those scenarios. No protected status or approval rule has been removed.
This historical compatibility check is not a claim that Beta 4 or GA passed.

```powershell
./eng/test-applications-postgresql.ps1 -ReleaseTrack Core
./eng/test-applications-postgresql.ps1 -ReleaseTrack ContinuousGraphPreview
./eng/test-application-postgresql-tracks.ps1
```

Application plans deliberately use `lastVerifiedMilestone`, not the latest
announced milestone. Core plans never read the PostgreSQL 19 programme. The
reader requires exactly the named test set for each track and rejects skips,
failures, substitutions, mismatched counters and cross-track results. A new
capture directory preserves the TRX and hash; local results do not replace the
candidate's package-consumer or remote evidence gates.

Core packaging and approval readers accept an explicit `-ReleaseTrack Core`.
The core package manifest contains exactly five families, all five npm clients,
both SBOM formats and exact-commit provenance. Pilot approvals must collectively
cover those five families, with exactly five 1.2 versions in maintainer signoff.
All ten approval records, distinct pilot operators and independent review stay
required. The owner cannot substitute self-approval for independent review.

Streams and Sync release endurance now use the same digest-pinned PostgreSQL
18 image. Kubernetes uses a new `postgresql-core` StatefulSet and PVC; it does
not downgrade or replace the historical PostgreSQL 19 Graph volume. Preview
has its own candidate ConfigMap. The durations remain 72 hours for Streams,
24 for Sync and 24 for Live/Control Plane; the last requires at least 100,000
cycles. Sync requires all nine named test projects covering seven destinations,
not merely nine arbitrary project entries.

These contributor commands test the readers with synthetic evidence only:

```powershell
./eng/test-core-approval-evidence.ps1
./eng/test-core-workflow-evidence.ps1
./eng/test-core-endurance-evidence.ps1

# Build immutable local evidence from a clean, committed checkout. No publish.
./eng/build-v1-candidate-packages.ps1 -ReleaseTrack Core `
    -OutputRoot artifacts/my-core-packages -Commit (git rev-parse HEAD)
./eng/verify-v1-package-evidence.ps1 -ReleaseTrack Core `
    -EvidenceRoot artifacts/my-core-packages -ExpectedCommit (git rev-parse HEAD)
./eng/test-core-package-evidence.ps1 `
    -EvidenceRoot artifacts/my-core-packages -ExpectedCommit (git rev-parse HEAD)
```

Use a new output directory for each capture; existing evidence is not overwritten.
`eng/v1.2-candidate-readiness.json` is a draft aggregation contract, explicitly
marked as migration in progress. `eng/build-core-candidate-envelope.ps1` now
builds its schema 4 envelope from seven run records, fourteen canonical artifact
roles and ten approval records. `eng/verify-core-candidate-bindings.ps1` verifies
exact commit and producer bindings, file digests and sizes, root containment,
approval schemas and approval ordering. Its self-test exercises a synthetic
binding set and rejects malformed or stale substitutions; it is part of the
build gate. The binding report explicitly leaves payload qualification, live
GitHub identity and release approval unverified. The final remote aggregator and
the remaining Core evidence producers still need implementation.

Manual `build.yml` runs now capture the Core unit regression matrix on Windows
and Linux, and stable database acceptance on PostgreSQL 15–18. Each capture
retains the discovery log, raw TRX, test log and exact-commit test assembly.
All four database fixtures are digest-pinned. The compatibility runner verifies
that its test connection targets the inspected local database and port. Unit
regression excludes the named database-only Live and Sync classes; compatibility
executes those classes against the live fixtures. The existing complete solution
build/test jobs remain required, as do separate destination and topology gates.

`eng/build-core-test-manifest.ps1` joins the two OS captures or four database
captures without overwriting evidence. `eng/verify-core-test-evidence.ps1`
requires complete coverage, the canonical project filters, exact discovery/TRX
agreement, zero skips and failures, matching counters, source-bound assemblies
and the recorded stable database identities. The aggregated
`v1.2-core-test-evidence-<sha>-<run-id>` artifact contains `regression/` and
`compatibility/` payload trees at the schema 4 binding paths. Copy both complete
trees when assembling a candidate. These readers qualify test payloads only;
they do not certify live GitHub identity, performance, endurance or publication.

```powershell
./eng/test-core-test-evidence.ps1
./eng/verify-core-test-evidence.ps1 -Kind Regression `
    -EvidencePath artifacts/my-core-evidence/regression/core-regression-manifest.json `
    -ExpectedCommit <full-40-character-sha>
./eng/verify-core-test-evidence.ps1 -Kind Compatibility `
    -EvidencePath artifacts/my-core-evidence/compatibility/core-compatibility-manifest.json `
    -ExpectedCommit <full-40-character-sha>
```

The proposed seven-run scope excludes Graph and adds combined Live/Control Plane
endurance. Preserve the full downloaded payload trees beside the envelope:
package, benchmark, website and endurance readers need those raw files before
any candidate can be approved. Hashing report JSON alone cannot qualify them.

The historical `v1-candidate-readiness.yml` aggregator and its V1 evidence schema
still describe a six-family, PostgreSQL-19-GA-qualified 1.0 candidate. They are
not a valid 1.2 core release gate. The 1.2 candidate aggregator must be migrated
to this track policy, exact 1.2 versions, the scoped performance manifest,
Live/Control Plane endurance and current connector evidence before publication
can be enabled. Keep the existing protections and independent approvals;
do not bypass the old aggregator or reinterpret its historical artifacts.

See the [audit action record](../improvement-audit.md) for remaining work and
acceptance criteria. This policy change is not a release approval.
