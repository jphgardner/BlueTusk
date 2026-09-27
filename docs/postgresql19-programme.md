# PostgreSQL 19 compatibility programme

PostgreSQL 19 Beta 4 was released on 24 September 2026. BlueTusk's last verified
milestone is Beta 3; Beta 4 is recorded as **not yet tested**, not silently
promoted to supported status. The official roadmap now targets October 2026.
Beta images are for compatibility development, not production dependencies.

The [Beta 4 announcement](https://www.postgresql.org/about/news/postgresql-19-beta-4-released-3386/)
removed SQL/PGQ, including `GRAPH_TABLE`, from PostgreSQL 19. A later PostgreSQL
major may restore it; no version is promised here. Graph stays available as
preview development on the historical Beta 3 fixture. The other five BlueTusk
families have a separate [release track](releases/release-tracks.md).

`eng/postgresql19-programme.json` is the machine-readable cadence. The checked
Beta 3 container is pinned by OCI digest, the scheduled official-branch
snapshot detects catalogue and grammar drift, and
`verify-postgresql19-programme.ps1 -VerifyOfficialCurrent` checks the observed
official milestone. The verifier reports the last tested milestone separately.
The fixture remains Beta 3 until new image and test evidence have been recorded.

BlueTusk advanced from Beta 2 to Beta 3 on 2026-08-17 after the official
documentation moved on 2026-08-13. The full serial solution suite and the
application migration/integration suite passed against
`postgres:19beta3-alpine@sha256:b1692e50613a21e61c424859f943b9e193ae73e5a8c68abd5382dfb235bf15fc`
with zero failures. This is milestone-drift evidence only; it is neither the
immutable GA matrix nor production approval.

For every later beta and every release candidate:

1. Pin the official image by digest and record its release date.
2. Run the full PostgreSQL 15–19 solution matrix at the exact BlueTusk commit.
3. Run protocol, replication, type, migration, reverse-engineering, native
   `REPACK` execution/progress, performance and stress subsets. Test capability
   absence explicitly. Run positive SQL/PGQ tests only on a server that actually
   provides it; do not count skipped Graph tests as proof of Graph compatibility.
4. Review the PostgreSQL release notes for protocol, catalogue, type, grammar
   and migration changes.
5. Archive test results, server version, image digest, source commit and
   package hashes; then update the programme record.

The [typed SQL/PGQ boundary](graph/README.md#exact-v1-typed-subset-boundary) remains
fixed: linear typed paths and direct scalar predicates are supported; the rest
stays available through parameterised raw SQL. Unsupported typed forms fail
without a string-concatenation fallback.

BlueTusk 1.2 also has a first-class API for PostgreSQL 19's native `REPACK`
statement, including synchronous and asynchronous execution, every documented
table/database, `USING INDEX`, `ANALYZE`, `VERBOSE`, and `CONCURRENTLY` shape,
and `pg_stat_progress_repack` monitoring. The Beta 3 integration test reclaims
a table, refreshes selected statistics, executes the concurrent path, and
verifies data preservation. This is pre-GA compatibility evidence and must be
repeated against every later milestone and the final digest-pinned GA image.

Promoting PostgreSQL 19 from preview to stable compatibility still requires
`verify-postgresql19-programme.ps1 -RequireGeneralAvailability`, a digest-pinned
GA image, and exact-candidate compatibility evidence. Core 1.2 publication does
not require that optional compatibility promotion. Its supported stable matrix
is PostgreSQL 15–18; its own performance, durability and release gates remain.
Graph needs separate supported-server SQL/PGQ qualification, not a PostgreSQL 19
GA version check. The [V1 publication record](releases/1.0.0-publication-record.md)
is historical and does not qualify later releases.
