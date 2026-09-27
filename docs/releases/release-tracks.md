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

The historical `v1-candidate-readiness.yml` aggregator and its V1 evidence schema
still describe a six-family, PostgreSQL-19-GA-qualified 1.0 candidate. They are
not a valid 1.2 core release gate. The 1.2 candidate aggregator must be migrated
to this track policy, exact 1.2 versions, the scoped performance manifest,
Live/Control Plane endurance and current connector evidence before publication
can be enabled. Keep the existing protections and independent approvals;
do not bypass the old aggregator or reinterpret its historical artifacts.

See the [audit action record](../improvement-audit.md) for remaining work and
acceptance criteria. This policy change is not a release approval.
