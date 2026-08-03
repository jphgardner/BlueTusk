# BlueTusk Streams 0.1.0-preview.1

This is the first packaging-ready Streams preview. It establishes the public
transaction, checkpoint, lease, snapshot, typed-mapping, hosting, and relay
contracts needed by the later Sync and Live product families.

## Packages

- `BlueTusk.Streams`
- `BlueTusk.Streams.Aspire`
- `BlueTusk.Streams.CloudEvents`
- `BlueTusk.Streams.DependencyInjection`
- `BlueTusk.Streams.EntityFrameworkCore`
- `BlueTusk.Streams.Storage.File`
- `BlueTusk.Streams.Storage.PostgreSql`
- `BlueTusk.Streams.Storage.Redis`
- `BlueTusk.Streams.Testing`
- `BlueTusk.Streams.Tool`

All packages use the repository's MIT licence and the independent Streams
`0.1.0-preview.1` version property.

## Guarantees in this preview

- transaction-preserving, ordered, at-least-once delivery;
- explicit partial-column states and conservative changed-column accuracy;
- stable source, transaction, change, snapshot-row, schema, and mapping IDs;
- bounded changes, transaction bytes, spool storage, relay storage,
  acknowledgement age, and WAL lag;
- versioned, integrity-checked disk spool, relay, checkpoint, and CloudEvent
  envelopes;
- monotonic compare-and-swap checkpoints, exclusive leases, fencing, and
  checkpoint-before-feedback acknowledgement;
- direct slot-per-group delivery and one-slot PostgreSQL relay fan-out with
  independently checkpointed groups;
- exported-snapshot keyset binary COPY and matching-position CDC, including
  explicit cross-process new-epoch restart; and
- schema/decoding pause defaults, typed conventions/overrides, EF-derived
  mappings, DI hosting, health checks, OpenTelemetry, Aspire, CLI, and testing
  integrations.

The test gate covers fake pgoutput sequences, crash boundaries, spool limits,
state-store conformance, relay replay/retention, and a live PostgreSQL 15-19
matrix. Every supported server passed no-gap concurrent snapshot writes,
cross-process snapshot restart, PostgreSQL state storage, and relay fan-out.
The hosted sample additionally completed a real snapshot and consumed a later
CDC insert as one acknowledged transaction.

## Preview boundaries

- Delivery is at least once. Stable IDs assist idempotency; exactly once is not
  claimed.
- Prepared/two-phase transaction delivery is fail-fast by default. The Phase 4
  opt-in staging mode is available for consumers that durably stage `Prepared`
  deliveries and atomically handle the later committed or rolled-back lifecycle
  delivery; see [prepared transactions](prepared-transactions.md).
- File storage is single-node. PostgreSQL is the production relay default;
  Redis is an alternative checkpoint/lease store.
- `RestartSnapshot` is explicit because replacing an inactive slot discards its
  retained WAL. The destination must durably reset or supersede the abandoned
  snapshot epoch.
- The preview has not completed the Phase 4 API freeze, complete format-upgrade
  matrix, or 72-hour release endurance gate required for Streams 1.0. Relay
  schema migration, bounded compaction, confirmed group removal,
  payload-protection hooks, and atomic backup/restore are implemented as Phase
  4 hardening slices.

The release workflow packages this family independently. It publishes to NuGet
only after an explicit publish-enabled workflow dispatch or a matching Streams
version tag; completing this code gate does not silently publish packages.
