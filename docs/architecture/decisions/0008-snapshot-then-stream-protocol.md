# ADR 0008: Bootstrap with an exported consistent snapshot

- Status: Accepted
- Date: 2026-08-03

## Context

A source snapshot and subsequent CDC must cover concurrent writes without a gap. PostgreSQL exported snapshots exist only while their exporting transaction remains alive, so an interrupted exporter cannot be resumed honestly.

## Decision

Streams creates a replication-consistent point and exported snapshot, keeps the exporter transaction alive, and lets bounded parallel readers import that snapshot. Readers use keyset-paged binary COPY. CDC starts at the matching consistent position and is held until snapshot completion.

Snapshot delivery has explicit reset, start, batch, and complete lifecycle calls. Rows use a snapshot epoch plus table/key identity rather than pretending to have WAL change identities.

If the exporter or any required session is lost, the epoch is abandoned. A new exported snapshot and epoch restart the idempotent snapshot from the beginning; an expired snapshot is never presented as resumable.

## Consequences

Consumers must support idempotent snapshot replay and an explicit reset. The protocol prefers correctness over partial snapshot continuation and permits parallelism only while the exporter lifetime is guaranteed.
