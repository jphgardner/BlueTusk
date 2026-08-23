# BlueTusk ContinuousGraph 1.0.0 release record

Status: published on 2026-08-23 from `continuous-graph-v1.0.0` at release commit
`7380d7b028c72b2aae348b778711d104d022a3f8`.

The complete release evidence, registry inventory, and explicit owner-accepted
exceptions are recorded in the
[V1 publication record](../releases/1.0.0-publication-record.md).

ContinuousGraph 1.0.0 stabilises capability-guarded registered SQL/PGQ plans,
dependency-aware invalidation, bounded affected-key incremental evaluation,
authoritative repair, replay/checkpoint restart, and the optional Control Plane
adapter. Its public API is frozen by
[`eng/continuous-graph-api-freeze.json`](../../eng/continuous-graph-api-freeze.json).

The planned standard publication gate required PostgreSQL 19 GA, Provider,
Streams, Live, and Control Plane 1.0.0 plus a successful exact-candidate 24-hour
endurance run and an independent application pilot. The package dependencies
were published first; the owner exception records the deferred GA, endurance,
and pilot evidence.

Support starts with the immutable `continuous-graph-v1.0.0` packages. Registry
availability, contents, hashes, SBOMs, provenance, tests, and dependency
resolution passed in the recorded release workflow. Defects use rollback or
pinning and a new fixed version.

Service Topology Centre and Fraud Graph Investigator exercise repair,
checkpoint/restart, disconnect recovery, cancellation, path analysis, and
ordered result handling from exact RC packages. They also use readable fluent
property-graph migrations. This is PostgreSQL 19 Beta 3 staging coverage;
neither application is yet an independent stable pilot.
