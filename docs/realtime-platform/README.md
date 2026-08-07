# BlueTusk real-time platform

BlueTusk extends the native provider with transaction-preserving change
streams, durable fan-out, destination synchronisation, authorised live queries,
and ContinuousGraph queries. Every component is MIT licensed and remains in
this monorepo, but each product family has an independent semantic version and
release train.

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
- Direct groups own independent slots. Streams also includes PostgreSQL relay
  fan-out from one slot.
- All memory, transaction, spool, acknowledgement-age, and WAL-lag queues are bounded.
- Exported snapshots restart with a new epoch after exporter/session loss; an expired snapshot is not resumable.
- Live uses CDC as invalidation and reruns an authorised bounded EF query before emitting client-visible data.
- Sync advances only after a destination confirms durable handling of the complete source transaction.

See the [public contracts](contracts.md), [delivery phases](delivery-plan.md), and accepted [architecture decisions](../architecture/decisions).

## Release trains

The release manifest is `eng/product-families.json`; version properties live under `eng/versions`. A product project declares its train with `BlueTuskProductFamily`. Release tags are independently named `provider-v*`, `streams-v*`, `sync-v*`, `live-v*`, `control-plane-v*`, and `continuous-graph-v*`.

An empty family is valid during architecture work but cannot be packaged. This prevents placeholder NuGet packages from implying implemented behavior.

Each family declares its cross-family release dependencies and an explicit
schema-2 publication policy. During preparation all policies are disabled; in
the immutable candidate all six are armed. Exact stable channels, tag prefixes,
dependency order, and required exact-commit workflow evidence are
machine-enforced. Every package
project is listed explicitly, so a new project cannot silently enter a release
train. `-Candidate` can build a gated verification
artifact without opening its publication gate. Families with npm artifacts
always run a clean locked install, vulnerability audit, client build, and
client tests before any tarball is created. See the
[release process](../release-process.md).

Implementation status: all six families are release-prepared at stable
`1.0.0`. [Streams](../streams/release-notes-1.0.0.md) has its complete CDC and
relay contracts; [Sync](../sync/release-notes-1.0.0.md) has all four
destinations on one conformance contract; [Live](../live/release-notes-1.0.0.md)
has its PostgreSQL stores, transports, and NuGet/npm clients; the
[Control Plane and Dashboard](../control-plane/release-notes-1.0.0.md) provide
authorised inventory, operations, audit, and versioned v1 APIs; and
[ContinuousGraph](../continuous-graph/release-notes-1.0.0.md) has bounded
incremental maintenance plus authoritative repair. Publication remains
disabled during preparation. After PostgreSQL 19 GA, a reviewed arming PR to
`main` creates the immutable candidate; tags and protected production approval
remain the publication boundary.
