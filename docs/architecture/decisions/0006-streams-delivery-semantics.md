# ADR 0006: Make Streams the application CDC boundary

- Status: Accepted
- Date: 2026-08-03

## Context

The replication packages expose PostgreSQL wire messages. Applications need a durable contract that preserves transactions and remains stable as pgoutput evolves. Allowing each product to interpret wire messages would duplicate transaction assembly, schema handling, restart logic, and failure semantics.

## Decision

`BlueTusk.Streams` is the only application-level CDC abstraction. Sync, Live, Control Plane, and Continuous Graph must not reference `BlueTusk.Replication` or `BlueTusk.Replication.PgOutput` directly; an architecture test enforces the boundary.

Streams delivers immutable, ordered transactions at least once. It does not claim exactly-once delivery. A delivery can be acknowledged, negatively acknowledged, or disposed without acknowledgement. Only the delivery object can advance durable progress.

Direct consumer groups initially own independent PostgreSQL slots. The first preview also includes a durable PostgreSQL relay so multiple independently checkpointed groups can share one slot.

## Consequences

All application products inherit one ordering, identity, checkpoint, schema, and retry model. Duplicate delivery remains possible and consumers must be idempotent. Lower-level replication packages remain public for specialist wire-level users, but are not an application-product dependency.
