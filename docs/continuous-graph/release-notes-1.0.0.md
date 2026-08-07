# BlueTusk ContinuousGraph 1.0.0 release record

Status: release-prepared, not published.

ContinuousGraph 1.0.0 stabilises capability-guarded registered SQL/PGQ plans,
dependency-aware invalidation, bounded affected-key incremental evaluation,
authoritative repair, replay/checkpoint restart, and the optional Control Plane
adapter. Its public API is frozen by
[`eng/continuous-graph-api-freeze.json`](../../eng/continuous-graph-api-freeze.json).

Stable publication requires PostgreSQL 19 GA, Provider, Streams, Live, and
Control Plane 1.0.0, a successful exact-candidate 24-hour endurance run with at
least 100,000 evaluations, 99.9% committed outcomes, lifecycle P95 at or below
one second, and no ordering or reconciliation errors. Cancellation,
authoritative repair, replay restart, and PostgreSQL disconnect/recovery must
all be evidenced. At least one independent application pilot must exercise
ContinuousGraph.

Support starts only after `continuous-graph-v1.0.0` is tagged from the
immutable reviewed `main` candidate and registry availability, hashes,
provenance, smoke tests, and dependency resolution pass. Published 1.0.0
artifacts are immutable; defects use rollback or pinning and a new fixed
version.
