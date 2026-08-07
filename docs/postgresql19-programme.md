# PostgreSQL 19 compatibility programme

PostgreSQL 19 is currently at Beta 2. BlueTusk treats it as pre-GA evidence,
not a production dependency. The official project warns that beta features and
behaviour may still change and does not recommend beta releases for production.
General availability is currently planned for September 2026.

`eng/postgresql19-programme.json` is the machine-readable cadence. The checked
Beta 2 container is pinned by OCI digest, the scheduled official-branch
snapshot detects catalogue and grammar drift, and
`verify-postgresql19-programme.ps1 -VerifyOfficialCurrent` fails when the
official documentation advances beyond the recorded milestone.

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
exact-commit evidence. Publication remains disabled independently in the
product-family manifest.
