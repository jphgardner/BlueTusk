# Ecosystem expansion evidence ledger

Working branch: `codex/ecosystem-products`. Events, Jobs, Documents, Projections,
Search, Schema, Sql, Studio, Edge, and Workflows have independent
`0.1.0-preview.1` packages. Publication is disabled for all ten families.
Implemented paths and passing local gates are substantial, but no family is
production qualified.

| Family | Implemented and directly exercised | Material work still open |
| --- | --- | --- |
| Events | Transactional outbox/inbox, typed routing, replay and Streams adapter; 44 live tests per PostgreSQL version, native execution, 100,000-event Projections/Events campaign | Longer retention, large producer/consumer fleets, upgrades and disaster recovery |
| Jobs | Durable admissions, leases, fences, typed workers, scheduling and effects; 36 live tests per version, 600-second storage/fault campaign and physical promotion | Representative multi-day retention/capacity and operations under repeated failure |
| Documents | Typed JSONB, atomic CAS sessions, patches, indexes, Streams/Live adapters and host health; 30 live tests per version, 16-cell load and killed-writer recovery | Physical TOAST/WAL growth, tail latency, sustained hot keys and operational limits |
| Projections | Durable joins/aggregates, snapshot/WAL checkpoint and cutover, fenced Live updates; 34 live tests per version, 100,000-effect exact-state campaign, 600-second overload and physical promotion | Longer independent-host load, retention/format upgrades and wider failure distributions |
| Search | Full-text/vector/hybrid, ACLs, pgvector, OpenSearch ANN and embedding jobs; 53 live tests per version and native execution | Scale limits, rolling backend upgrades, longer queue/reindex operation and latency budgets |
| Schema | Catalogue contracts, bounded add-only plans, durable DDL journal/reconciliation and CLI; 63 core plus 6 CLI tests per version and native execution | Destructive/partition-parent migrations, invalid-index repair automation and broad upgrade operations |
| Sql | Runtime contracts, incremental generator and installed catalogue-validation CLI; 33 runtime, 31 generator and 8 CLI tests per version, native and fresh package-only consumer | Broader PostgreSQL type/shape coverage and multi-version upgrade receipts |
| Studio | Authenticated SQL/explain/schema workspace, durable audit, Events/ControlPlane adapters and CLI; 14 core plus 2 CLI tests per version | Large multi-tenant deployment, operational hardening and long-lived audit retention |
| Edge | SQLite/IndexedDB durable cache/queue, authenticated HTTP server, atomic application callback; 33 live tests per version, 14 browser contracts, real browser restart/HTTP recovery and native execution | Wider browser/platform matrix, long offline retention and repeated server failover |
| Workflows | Durable DAG, activities, signals/timers/joins, compensation and replay; 26 core plus 6 DI tests per version, 600-second Jobs/Workflows campaign and physical promotion | Multi-day workload, large definitions/history and repeated failover/upgrade operation |

## Current local gates

- The full 195-project Release solution builds with zero warnings and errors.
  Layout verification finds 73 ordered solution folders; two embedded template
  projects are intentionally excluded. The source supply-chain gate finds pinned
  actions and CI images across 14 workflows. All 16 API family budgets pass,
  covering 16,962 public signatures.
- The combined database gate passes **419 tests in 15 projects** with zero
  failures or skips on each PostgreSQL **15, 16, 17 and 18**. All four runs used
  the same unchanged candidate source. PostgreSQL 18 additionally passed five
  real Windows x64 NativeAOT executables. An actual Linux x64 container ran
  the same 419 tests and five separate Linux NativeAOT executables against the
  exact source archive. These are local source-stable runs of a dirty working
  tree, not an immutable release or CI result.
- Edge's locked npm install, TypeScript build, 14 contract tests, real browser
  IndexedDB restart smoke and real browser/PostgreSQL HTTP recovery smoke pass.
  The fresh installed Schema/SQL tools and a separate package-only SQL consumer
  pass relation and catalogue receipt modes, including qualified enum/domain
  round trips.
- All ten candidate package sets verify locally: 32 NuGet packages, 31 symbol
  packages and one Edge npm package. This verifies archive metadata and content,
  not that the unpublished dependency graph can be restored from a public feed.
  The dependency audit reports zero vulnerable entries across 195 .NET projects;
  locked npm audit reports zero vulnerabilities in its 63-package tree. Audits
  are a point-in-time input, not a security certification.
- Events/Projections have source-bound exact-state 100,000-effect sweeps and a
  600-second bounded overload run with 866,618 rejected admissions rather than
  an unbounded queue. Three synchronous physical promotions recover with fenced
  ownership. Jobs/Workflows have storage/fault and physical-promotion campaigns.
  Their evidence lives under ignored `artifacts/` in this worktree; the
  corresponding product documents explain measured scope and limitations.
- The [Documents fixed-cardinality comparison](../documents/evidence/2026-09-28-maintenance-pair.md)
  completed two source-frozen 600-second profiles with exact logical checks,
  120-second idle drains and hard-killed writer recovery. The default profile
  ended at 16.20 GB of relation storage for roughly 16 MiB retained source
  content; experimental fast TOAST vacuum ended at 9.06 GB with lower
  throughput, higher tail latency and more WAL per transition. Neither profile
  established a physical bound.

## Production qualification still required

The ten products need explicit application-level throughput, p99 and capacity
budgets; longer representative runs on independent load hosts; bounded storage
and recovery under sustained hot updates; version/format and rolling upgrades;
credential rotation, network partition, storage pressure and repeated failover
across more families; security review and operational telemetry/runbooks; and
reproducible, clean-commit packages with SBOM/provenance and passing CI on the
committed candidate. Windows browser evidence does not establish a Linux/browser
matrix. PostgreSQL 15–17 combined managed gates do not execute every native
smoke on those versions. The package publication policy and unreleased
dependency gates remain disabled. Passing tests and short campaigns should not
be read as proof of massive-production performance or efficiency.
