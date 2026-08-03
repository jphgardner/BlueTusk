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

Implementation status: [Streams 0.1.0-preview.1](../streams/release-notes-0.1.0-preview.1.md) has passed the Phase 3 implementation and packaging gates. Its release manifest is publishable through the independently versioned Streams release workflow. The [Control Plane and Dashboard](../control-plane/README.md) provide source/relay, Sync, and Live inventory plus authorised pages, confirmed/audited operation controls, and immutable PostgreSQL audit, but their release train remains non-publishable until the later Graph dashboard and upgrade gates complete. [Sync](../sync/README.md) has its pipeline kernel, all four required destinations on one live conformance contract, bounded reconciliation/repair, cutover-safe rebuild, in-process hosting, restart-aware relay bootstrap, retry/rate-limit policy, dashboard integration, and an executable 24-hour endurance workflow. Sync remains non-publishable until successful long-run evidence is archived. [Live](../live/README.md) has the registered-query engine, transports and clients, operational projection, expanded vetted query shapes, and adversarial/load gates; its final real-PostgreSQL release audit remains open. [Continuous Graph](../continuous-graph/README.md) has begun Phase 7 with capability-guarded typed SQL/PGQ registration, exact graph-element dependency extraction, and authoritative Live requery/diff. It remains non-publishable until the remaining PostgreSQL 19, dashboard, sample, and benchmark gates pass.
