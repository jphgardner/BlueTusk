# BlueTusk real-time platform

BlueTusk extends the native provider with transaction-preserving change streams, durable fan-out, destination synchronisation, authorised live queries, and eventually continuous graph queries. Every component is MIT licensed and remains in this monorepo, but each product family has an independent semantic version and release train.

```text
PostgreSQL 15–19
       ↓
Data / COPY / Replication / pgoutput
       ↓
BlueTusk Streams ─→ PostgreSQL durable relay
       ├──────────→ BlueTusk Sync
       └──────────→ BlueTusk Live ─→ Continuous Graph
                              ↑
                    Control Plane / Dashboard
```

Streams is the only application-level CDC boundary. Sync, Live, and Continuous Graph consume Streams deliveries or relay cursors and are forbidden by architecture tests from referencing replication internals.

## Correctness contract

- Delivery is ordered, transaction-preserving, and at least once. Exactly once is not claimed.
- Durable downstream handling precedes checkpoint persistence; checkpoint persistence precedes PostgreSQL feedback.
- Checkpoints are monotonic compare-and-swap records bound to a source identity and lease fencing token.
- Direct groups own independent slots. The first Streams preview also includes PostgreSQL relay fan-out from one slot.
- All memory, transaction, spool, acknowledgement-age, and WAL-lag queues are bounded.
- Exported snapshots restart with a new epoch after exporter/session loss; an expired snapshot is not resumable.
- Live uses CDC as invalidation and reruns an authorised bounded EF query before emitting client-visible data.
- Sync advances only after a destination confirms durable handling of the complete source transaction.

See the [public contracts](contracts.md), [delivery phases](delivery-plan.md), and accepted [architecture decisions](../architecture/decisions).

## Release trains

The release manifest is `eng/product-families.json`; version properties live under `eng/versions`. A product project declares its train with `BlueTuskProductFamily`. Release tags are independently named `provider-v*`, `streams-v*`, `sync-v*`, `live-v*`, `control-plane-v*`, and `continuous-graph-v*`.

An empty family is valid during architecture work but cannot be packaged. This prevents placeholder NuGet packages from implying implemented behavior.

Each family declares its cross-family release dependencies and an explicit
schema-2 publication policy. A family cannot enable publication while any
dependency is still gated. Stable and preview channels, exact tag prefixes, and
required exact-commit workflow evidence are machine-enforced. Every package
project is listed explicitly, so a new project cannot silently enter a release
train. `-Candidate` can build a gated verification
artifact without opening its publication gate. Families with npm artifacts
always run a clean locked install, vulnerability audit, client build, and
client tests before any tarball is created. See the
[release process](../release-process.md).

Implementation status: [Streams 0.1.0-preview.1](../streams/release-notes-0.1.0-preview.1.md) has passed the Phase 3 implementation and packaging gates. The [Control Plane and Dashboard](../control-plane/README.md) provide source/relay, Sync, Live, and Continuous Graph inventory, versioned v1 agent APIs, authorised pages, confirmed/audited operation controls, and transactionally migrated immutable PostgreSQL audit. [Sync](../sync/README.md) has its pipeline kernel, all four required destinations on one live conformance contract, bounded reconciliation/repair, cutover-safe rebuild, in-process hosting, restart-aware relay bootstrap, retry/rate-limit policy, dashboard integration, and an executable 24-hour endurance workflow. [Live 0.1.0-preview.1](../live/release-notes-0.1.0-preview.1.md) has passed its implementation, PostgreSQL 15–19 store/transport, client, and package gates. [Continuous Graph 0.1.0-preview.1](../continuous-graph/release-notes-0.1.0-preview.1.md) has passed its Phase 7 implementation and packaging gates. All six publication policies are currently disabled while the exact V1 release evidence remains open; candidate artifacts do not grant publication permission.
