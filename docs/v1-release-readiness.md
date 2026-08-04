# V1 release readiness

BlueTusk is code-ready for an immutable V1 candidate. Stable publication is
not yet authorised. The release process remains fail closed until the external,
exact-candidate evidence below is complete.

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
- A digest-pinned PostgreSQL 19 programme with upstream milestone-drift
  detection and a GA-only stable-publication gate.
- Streams and Sync endurance workflows that bind reports to the candidate
  commit, NuGet/npm hashes, runtime, operating system and service-image digests.
- A six-meter, 60-instrument telemetry contract; 14 reference production SLOs;
  a deployable OpenTelemetry Collector, Prometheus rules and Grafana dashboard;
  and metric lifecycle tests for every product family.
- Complete checked-in BenchmarkDotNet coverage for 89 measured workloads across
  21 fixtures, 37 allocation budgets, 18 reference-machine latency budgets and
  a manual exact-candidate performance workflow.
- A single fail-closed V1 verifier that distinguishes deterministic engineering
  readiness from PostgreSQL 19 GA, endurance, performance, pilot, recovery,
  game-day and accountable approval evidence for one immutable commit.
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

The final local verification on 2026-08-04 produced:

| Gate | Result |
| --- | --- |
| Release build | 121 projects; zero warnings and zero errors |
| Formatting | No changes required |
| PostgreSQL 19 full solution matrix | 3,289 passed, 158 environment-specific skips, zero failures across 45 test assemblies |
| ADO.NET live compatibility suite | 11 passed, including function `IN`/`OUT`/`INOUT`, procedure `CALL`, Dapper, schema and DI health checks |
| Public API budget | 12,975 signatures across six product families |
| Dependency vulnerability audit | No advisory matched in any solution project |
| Provider candidate packaging | 31 NuGet packages and 29 symbol packages verified |
| Candidate SBOM/provenance smoke | 60 artifact hashes and 317 components/packages verified in both SBOM formats |
| Repository gates | Solution layout, documentation links, workflow YAML, PowerShell syntax, Action pins, supply chain and PostgreSQL 19 programme passed |
| Live repository governance | Settings verified: 35 required checks, strict `main`, two protected environments, SPDX 2.3 dependency graph with 633 packages, alerts, automated fixes and private reporting; declared environment secrets remain to be provisioned |

This snapshot validates the tooling and current working tree. It is not
immutable release evidence: the final candidate must be committed, clean, and
rerun by the required workflows at that exact commit.

## Gates that must remain open

1. Add at least one additional eligible human reviewer. The repository
   currently has one collaborator, and prevent-self-review correctly blocks
   owner-initiated candidate and publication deployments until another reviewer
   is available.
2. Add a read-only `V1_GOVERNANCE_TOKEN` to both protected environments with
   Administration read, Actions read, Contents read and Environments read so
   exact-candidate and pre-publication jobs can verify the live settings and
   required secret names.
3. Freeze the final commit, versions and package hashes.
4. Run the complete manual `build.yml` evidence workflow at that commit,
   including PostgreSQL 15–19, PgBouncer, NativeAOT/trimming, connector,
   authentication, stress and packaging jobs.
5. Run the manual `security.yml` CodeQL workflow and the manual `fuzzing.yml`
   workflow for at least one hour per parser target at that exact commit. Every
   fuzz target must complete without a crash or hang finding.
6. Run the complete manual `performance.yml` reference-machine workflow at that
   commit and archive its integrity-bound result set.
7. Complete and archive the exact 72-hour Streams and 24-hour Sync endurance
   workflows at the same candidate commit. Any candidate code change restarts
   the applicable run.
8. Repeat PostgreSQL 19 testing for each later beta/RC and GA. Do not describe
   PostgreSQL 19 support as stable before the GA record passes.
9. Complete the
   [independent release review](release-review-handoff.md), application pilots,
   backup/restore and rollback rehearsal, incident game day, security/SLO owner
   approval and maintainer sign-off.
10. Run `verify-v1-production-readiness.ps1 -Mode Candidate` against the complete
   SHA-256-bound evidence directory.
11. Enable stable publication one dependency-ordered product family at a time:
   Provider, Streams, Sync/Live, Control Plane, then Continuous Graph preview.

Until every applicable item is complete for one immutable commit, all
publication switches must remain disabled.
