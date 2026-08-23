# PostgreSQL 19 compatibility programme

PostgreSQL 19 is currently at Beta 3. BlueTusk treats it as pre-GA evidence,
not a production dependency. The official project warns that beta features and
behaviour may still change and does not recommend beta releases for production.
General availability is currently planned for September 2026.

`eng/postgresql19-programme.json` is the machine-readable cadence. The checked
Beta 3 container is pinned by OCI digest, the scheduled official-branch
snapshot detects catalogue and grammar drift, and
`verify-postgresql19-programme.ps1 -VerifyOfficialCurrent` fails when the
official documentation advances beyond the recorded milestone.

BlueTusk advanced from Beta 2 to Beta 3 on 2026-08-17 after the official
documentation moved on 2026-08-13. The full serial solution suite and the
application migration/integration suite passed against
`postgres:19beta3-alpine@sha256:b1692e50613a21e61c424859f943b9e193ae73e5a8c68abd5382dfb235bf15fc`
with zero failures. This is milestone-drift evidence only; it is neither the
immutable GA matrix nor production approval.

For every later beta and every release candidate:

1. Pin the official image by digest and record its release date.
2. Run the full PostgreSQL 15–19 solution matrix at the exact BlueTusk commit.
3. Run the SQL/PGQ migration, discovery, typed-query, raw-SQL, reverse
   engineering, performance, replication and stress subsets.
4. Review the PostgreSQL release notes for protocol, catalogue, type, grammar
   and migration changes.
5. Archive test results, server version, image digest, source commit and
   package hashes; then update the programme record.

The [typed SQL/PGQ boundary](graph/README.md#exact-v1-typed-subset-boundary) remains
fixed: linear typed paths and direct scalar predicates are supported; the rest
stays available through parameterised raw SQL. Unsupported typed forms fail
without a string-concatenation fallback.

Stable publication invokes
`verify-postgresql19-programme.ps1 -RequireGeneralAvailability`. That gate
cannot pass until PostgreSQL 19 GA has an official digest-pinned image and
exact-commit evidence. BlueTusk `1.0.0` was published under the explicit owner
exception recorded in the
[V1 publication record](releases/1.0.0-publication-record.md); the PostgreSQL
19 GA evidence itself remains deferred, and the standard gate remains in force
for later releases.
