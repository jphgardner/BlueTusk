# BlueTusk ecosystem expansion

The requested outcome is ten complete ecosystem products, built for large
production workloads and qualified with measured efficiency and recovery
evidence. Code, a successful build, and a production qualification are separate
milestones. None of the new products is currently production qualified.

## Product requirements

| Product | Required behavior |
| --- | --- |
| Events | Typed and versioned domain events; atomic business-write/outbox transactions; tenant and stream ordering; stable event identity; inbox deduplication atomic with consumer effects; bounded delivery and replay; consumer checkpoints; EF integration; event contract evolution. |
| Jobs | Durable transactional enqueue; delayed and recurring scheduling; bounded fair workers; database-clock leases and fencing; heartbeat, cancellation, retries and terminal failure; idempotency; bounded history and retention; typed handlers; operational controls. |
| Documents | Typed JSONB sessions and source-generated serialization; tenant isolation; revision-based concurrency; atomic multi-document saves; patching; bounded keyset queries; declarative indexes; document schema evolution; Streams and Live integration. |
| Projections | Durable multi-source read models; joined and aggregate projections; ordered transactional application with colocated checkpoints; versioned definitions; no-gap rebuilds and cutover; replay, reconciliation and repair; bounded dependencies; Live integration. |
| Search | PostgreSQL full-text, vector and hybrid retrieval; versioned ingestion and chunking; bounded embedding workers; tenant and permission filtering; stable rank/pagination; deletion propagation; ranking extensions; pgvector and OpenSearch adapters. |
| Schema | Consistent catalogue snapshots; canonical fingerprints; structural compatibility and consumer-impact checks; migration plans and safe deployment sequencing; drift checks; bounded discovery; declarative indexes and schema contract evolution. |
| Sql | Typed query methods generated from SQL files; schema-validated parameters/results; PostgreSQL type support; cancellation and ownership; deterministic generation and diagnostics; NativeAOT/trimming; build-time schema snapshots and explicit validation command. |
| Studio | Authenticated developer workspace; SQL editor; schema browser; bounded explain/query execution; subscription inspection; event tracing and replay; redaction, authorization and audit; integration with existing Control Plane and CLI. |
| Edge | Durable SQLite and browser storage; selective authorized sync; queued idempotent writes; explicit conflict and deletion semantics; offline queries; crash recovery, schema upgrades and bounded retained state; Live-compatible reconnect. |
| Workflows | Versioned persisted workflow execution; durable activities, timers, signals and joins; compensation; history and replay; fenced scheduling and cancellation; retries and external-effect idempotency; observability; migration of in-flight definitions. |

## Shared architecture

- Depend on Data and higher public abstractions, never PostgreSQL wire internals.
- Streams remains the only application-level CDC boundary.
- New core libraries do not depend on EF. EF and host integrations are separate
  packages. New libraries do not introduce an Npgsql runtime dependency.
- Store business effects and their deduplication/checkpoint state in one
  transaction where PostgreSQL can provide that atomic boundary. External
  side effects are at least once and require an explicit idempotency contract.
- Scope all identities and persisted keys by their tenant and product context.
- Use database time for distributed deadlines and monotonic fences for ownership.
- Bound payload bytes, result counts, batches, concurrency, retention, retry
  windows, caches and buffers. Reject incompatible durable formats explicitly.
- Do not log SQL parameters, connection strings, payloads, or credentials.

## Verification and production qualification

Every product needs direct evidence for its own requirements above. Required
shared gates are:

1. Release builds, formatting, dependency architecture, package-consumer tests,
   public API compatibility and repository registration.
2. Unit and real PostgreSQL tests covering the relevant transaction, race,
   cancellation, restart, duplicate, tenant and version-boundary behavior.
3. Windows and Linux compatibility with supported PostgreSQL releases and each
   required extension/destination; browser and SQLite coverage for Edge.
4. NativeAOT/trimming evidence for packages which advertise those capabilities.
5. Repeatable BenchmarkDotNet and database workload reports for throughput,
   P50/P95/P99, allocation, GC, memory, connection count, WAL and database load.
   Include payload-size, tenant-count, concurrency and backlog sweeps, a named
   reference environment and practical latency/throughput budgets.
6. Sustained overload/backpressure, fairness, retention and storage-growth
   evidence, not only short steady-state benchmarks.
7. Recovery under worker/process death, network partition, database failover,
   storage pressure, credential rotation, clock movement and server upgrades.
8. Durable-format upgrade and rollback rehearsals; no acknowledged effects lost,
   no cross-tenant delivery and no stale-owner mutation.
9. Security review, dependency scan, SBOM/provenance, deployable examples,
   operator runbooks, health/telemetry and supported configuration contracts.
10. All evidence bound to the same immutable candidate and independent release
    approval before publication or any production support claim.

“Most performant” requires workload-specific comparative evidence. No universal
performance leadership claim will be inferred from architecture or unit tests.

## Work sequence

Events, Jobs and Documents are implemented in parallel in the first batch.
Schema and the shared verification infrastructure are developed alongside them.
The remaining products stay in scope: Projections, Search, Sql, Studio, Edge,
and Workflows. They are implemented against the durable contracts established
by the first batch, then qualified individually and together. This sequence
does not reduce the completion requirements.

Progress and measured evidence are recorded in `progress.md`. Missing tests,
integrations, performance results or release evidence mean incomplete work.
