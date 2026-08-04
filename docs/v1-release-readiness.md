# V1 release readiness

BlueTusk is code-ready for an immutable V1 candidate. Stable publication is
not yet authorised. The release process remains fail closed until the external,
exact-candidate evidence below is complete.

## Implemented V1 hardening

- Nine bounded parser fuzzing targets, replayable corpus tests, CI/scheduled
  coverage-guided runs, and finding archive/minimisation tools.
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

The detailed evidence and commands are in the
[hardening programme](hardening-programme.md),
[ADO.NET compatibility matrix](ado-net/compatibility.md),
[fuzzing guide](fuzzing.md),
[PostgreSQL 19 programme](postgresql19-programme.md), and
[release process](release-process.md).

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

This snapshot validates the tooling and current working tree. It is not
immutable release evidence: the final candidate must be committed, clean, and
rerun by the required workflows at that exact commit.

## Gates that must remain open

1. Freeze the final commit, versions and package hashes.
2. Run the complete manual `build.yml` evidence workflow at that commit,
   including PostgreSQL 15–19, PgBouncer, NativeAOT/trimming, connector,
   authentication, stress and packaging jobs.
3. Complete and archive the exact 72-hour Streams and 24-hour Sync endurance
   workflows at the same candidate commit. Any candidate code change restarts
   the applicable run.
4. Repeat PostgreSQL 19 testing for each later beta/RC and GA. Do not describe
   PostgreSQL 19 support as stable before the GA record passes.
5. Complete the
   [independent release review](release-review-handoff.md), application pilots,
   backup/restore and rollback rehearsal, and maintainer sign-off.
6. Enable stable publication one dependency-ordered product family at a time:
   Provider, Streams, Sync/Live, Control Plane, then Continuous Graph preview.

Until every applicable item is complete for one immutable commit, all
publication switches must remain disabled.
