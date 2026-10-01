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
GitHub identity and release approval unverified.

`eng/verify-core-candidate-payloads.ps1` adds the next offline aggregation stage.
It first verifies the same bindings and approvals, then runs the existing
website, complete Core package/SBOM, full performance-leadership matrix, raw
regression/compatibility/Sync connector, full-duration endurance and operational
disturbance readers. All fourteen roles must pass. Each endurance provenance
file must be byte-for-byte identical to the packaged candidate provenance;
matching a source SHA alone does not prove the same packages were exercised.
The complete assembled bundle, including raw descendants, is fingerprinted
before and after verification and must remain unchanged. Symbolic links,
junctions and case-ambiguous file paths are rejected.

Use a completed, immutable evidence bundle. An active capture directory is not
an aggregation input. The report may set `AllPayloadsValidated=true` only after
every payload reader passes. Fixture identity, execution authenticity, live
GitHub identity and `ReleaseApproved` remain false. No successful Core payload
aggregate has been captured yet. The final authenticity gate and remaining
Core evidence producers still need implementation. The binding self-test also
checks that correctly hashed synthetic placeholders and a same-source package
provenance substitution cannot pass this payload stage; these are reader tests,
not release qualification.

```powershell
./eng/verify-core-candidate-payloads.ps1 `
    -EvidencePath artifacts/my-core-evidence/candidate.json `
    -ExpectedCommit <full-40-character-sha> -CandidateCommitUtc <commit-time-UTC>
```

`build-core-candidate-envelope.ps1 -VerifyPayloads` runs both offline stages
before retaining the envelope. Without that switch it retains binding-only
evidence with the original false payload and release flags.

For authorized local Docker campaigns, `-UseLocalExecution` builds a separate
schema 5 envelope from `producer-runs.json`. Build, security and fuzzing still
require actual GitHub Actions records. Performance and the three endurance
producers may instead use `LocalDocker` capture records. Local records use UUIDs
and `local:<uuid>` artifact identities; GitHub records retain their real run IDs
and attempts. Local captures cannot supply GitHub URLs or numeric workflow IDs.
Each capture binds an immutable manifest, its source and verifier-tool commits,
UTC start/completion times, exit status, host/Docker platform, pinned images,
retained logs and exact producer artifact hashes. The binding reader counts
local captures separately and does not certify execution authenticity or
publication. It accepts no replacement for missing payload qualification.

`eng/build-core-local-endurance-record.ps1` emits an endurance capture record
only after the existing full-duration payload reader passes: 72 hours and
100,000 Streams transactions; 24 hours and 100 Sync cycles; or 24 hours and
100,000 Live/Control Plane cycles. It inspects only named Docker resources
labelled `bluetusk.owner=v1-local-qualification` and bound to the same candidate
commit. Preserve the full report/provenance trees and capture log under the
canonical artifact paths before emitting a record. Short preflight diagnostics
cannot produce a release-duration record. Performance leadership still requires
its full Windows/Linux comparison matrix; a reference-budget report is not a
performance-leadership manifest.

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

`eng/collect-core-github-run.ps1` collects a canonical eight-field workflow
record from the live GitHub API, rather than accepting a claimed run identity.
It requires a clean committed collector checkout and an exact candidate commit
available locally. The requested attempt must be a completed successful manual
run in the expected repository, with the matching head, workflow ID and path.
All pages of that attempt's jobs must agree with its source and execution IDs,
be complete, and contain only successful or conditional skipped jobs. At least
one job must have succeeded. This does not permit skipped applicable test cases;
the raw compatibility and regression readers remain unchanged.

```powershell
./eng/collect-core-github-run.ps1 -WorkflowFile security.yml `
    -RunId <actual-run-id> -RunAttempt <actual-attempt> `
    -ExpectedCommit <full-40-character-sha> `
    -OutputDirectory artifacts/my-core-security-metadata
```

The collector retains two matching API snapshots, raw stderr, file hashes and
its separate tool commit. `record.json` is created only after both snapshots
pass; rejected captures retain their failure and cannot supply that canonical
record. Copy the record into the appropriate schema 4 workflow or schema 5
GitHub producer entry, preserving the complete metadata capture separately.
`completedUtc` is the latest actual job completion, not a supplied timestamp.

`LiveRunMetadataValidated=true` proves this metadata collection stage only.
The workflow head is not proof of an actual checkout when a workflow overrides
its source. Checkout identity, artifact identity, fixture identity, execution
authenticity, full payload validation and release approval remain false. The
candidate's final authenticity gate still requires implementation. The mapper's
synthetic substitution tests do not certify any live GitHub run.

For an individual GitHub artifact, supply both `-ArtifactId <actual-artifact-id>`
and `-ArtifactName <exact-artifact-name>` to the same collector. It downloads the
binary archive directly from the verified artifact API route, retains matching
before/after artifact metadata, and checks the actual ZIP byte count and SHA-256
against GitHub's digest. The artifact must belong to the selected repository,
workflow run and source; its creation/update must fall within that attempt's
lifecycle, and it must remain unexpired. A previous attempt's archive is rejected.

The retained `capture.json` includes an individual `artifactReceipt`: archive
identity, the exact decompressed size and SHA-256 of every file, and the separate
collector commit. Unsafe paths, duplicate or case ambiguous paths, links, special
files and file/directory collisions are rejected before any payload assembly.
The collector does not extract the archive, follow members or unpack nested
archives. Both download and decompressed inventory have bounded byte limits.
Failed captures retain partial archives and raw API responses without producing
`record.json`. Use a fresh capture directory for each individual artifact.

`ArtifactDeliveryIdentityValidated=true` proves that individual delivery only.
GitHub's artifact API does not provide a job/attempt identity: lifecycle agreement
is retained explicitly and does not prove the producer's actual checkout.
The complete artifact set, nested payloads, fixtures, execution authenticity and
release eligibility still require independent verification. Their flags remain
false. The artifact guard tests use synthetic inputs and cannot qualify a release.

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
