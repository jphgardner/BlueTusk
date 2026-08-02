# ADR 0010: Use PostgreSQL for the first durable relay

- Status: Accepted
- Date: 2026-08-03

## Context

Slot-per-group consumption is simple but multiplies source slots and retained WAL. A durable fan-out layer lets one replication slot serve independent groups while preserving replay and acknowledgement state.

## Decision

The first Streams preview includes a PostgreSQL-backed relay. It uses a separate control data source by default and owns a configurable schema named `bluetusk_streams`. Configuration rejects publications that contain the relay control schema.

The relay stores source registrations and epochs, versioned transaction envelopes, groups, checkpoints, fencing leases, snapshot progress, quarantine records, and retention watermarks. An append becomes visible atomically. A transaction is retained until every applicable group has acknowledged beyond it and the configured resume-retention window has elapsed.

## Consequences

PostgreSQL is the production-default checkpoint and relay backend. Operators must capacity-plan relay storage independently from source WAL. File storage remains single-node only; Redis is an alternative checkpoint and lease store, not the initial relay store.
