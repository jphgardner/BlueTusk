# Real-time platform delivery plan

The implementation uses two-week iterations and lands only passing vertical slices. Each slice updates documentation and is committed and pushed on an AI-neutral `feature/...` branch.

| Phase | Scope | Release gate |
| --- | --- | --- |
| 0 | ADRs, dependency rules, product versions, release trains, contract documentation | contracts reviewed; dependency direction and release-family declarations machine-enforced |
| 1 | relation/type cache, dynamic rows, transaction assembly, identities, bounded spool, deliveries | PostgreSQL 15–19 DML, tuple-state, abort, reconnect, and streamed-transaction coverage |
| 2 | stores, CAS checkpoints, leases, direct groups, PostgreSQL relay, replay and retention | crash injection proves redelivery or clean progress without loss |
| 3 | typed/EF mappings, exported-snapshot bootstrap, DI, telemetry, Aspire, CloudEvents, CLI, testing | concurrent snapshot writes have no gap; restart creates a new epoch; direct and relay modes present |
| 4 | prepared transactions, relay operations and migrations, Control Plane foundation, Streams freeze | format upgrades pass and 72-hour fault-injected relay endurance completes |
| 5 | Sync state machine, transformations, four connectors, reconciliation, rebuilds and dashboard | all connectors pass snapshot-plus-stream, recovery, upgrade, repair, and endurance suites |
| 6 | registered Live compiler, gap-free initial results, diffs, replay, transports and clients | adversarial isolation and reconnect testing plus checked-in load budgets pass |
| 7 | bounded SQL/PGQ registration, dependency invalidation, authoritative graph diff and samples | PostgreSQL 19 guards, graph correctness, cancellation, and workload benchmarks pass |
| 8 | V1 expansion: multiplexing, managed hosting, capability-secured client SQL/LINQ, isolated transformations, and incremental graph evaluation | each capability has threat modelling, bounded resource contracts, fault recovery, operations, conformance, benchmarks, and an independently reviewable release gate |

Every phase additionally requires formatting, a zero-warning build, current provider regressions, documentation-link validation, vulnerability auditing, packaging checks, and applicable public API and serialization baselines.

## Locked defaults

- .NET 10 and PostgreSQL 15–19, with explicit PostgreSQL 19 capability guards.
- PostgreSQL is the first relay/control store; file is single-node and Redis is an alternative checkpoint/lease store.
- Sync workers are in-process for 1.0.
- Live ships SignalR and SSE, then TypeScript and Angular, with gRPC and React before 1.0.
- Schema drift and poison records pause with diagnostics by default.
- Bounded statement multiplexing is implemented as the first Phase 8 vertical
  slice. Managed hosting, capability-secured client SQL/LINQ, isolated
  transformation processes, and incremental graph evaluation are V1 scope and
  remain gated until their production contracts and evidence land. “Arbitrary”
  client queries never means bypassing registration, RLS, tenant scope,
  authorisation, cost limits, or cancellation.
