# ADR 0012: Keep source transactions as the Sync delivery unit

- Status: Accepted
- Date: 2026-08-03

## Context

Destinations offer different transactional and idempotency guarantees. Flattening source transactions into unrelated records would obscure partial failures and permit checkpoints to pass work that was not durably applied.

## Decision

`ISyncDestination` declares transactional batches, idempotent upserts, deletes, checkpoint co-location, reconciliation, and alias-swap capabilities. A pipeline checkpoint advances only after the destination confirms durable handling of the whole source transaction.

The official connector contract is deliberately specific: atomic state and
checkpoint for PostgreSQL and Redis, replay-safe materialisation for OpenSearch,
and durable publication with a stable identity for NATS JetStream. The public
capability flags expose the mechanics available to each connector without
changing the existing destination interface.

Transform definitions have stable version fingerprints. A changed fingerprint requires an explicit rebuild or migration. Poison records pause by default; quarantine-and-advance is an explicit operator policy. PostgreSQL, NATS JetStream, Redis, and OpenSearch ship together after passing one destination conformance suite.

## Consequences

Connectors may implement durability differently but expose one state machine and recovery contract. The shared conformance suite verifies same-process and restart redelivery, and each official connector's live tests cover its own atomicity, partial-write, versioning, or broker-deduplication boundary. Operators can therefore see the durable outcome each connector proves instead of inferring it from a broad slogan.
