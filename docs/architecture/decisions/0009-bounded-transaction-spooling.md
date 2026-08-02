# ADR 0009: Bound transaction memory and spill to a versioned spool

- Status: Accepted
- Date: 2026-08-03

## Context

Logical replication can stream transactions larger than process memory. Bounds on channel capacity alone do not constrain a single transaction, and partial delivery would break the transaction-preserving contract.

## Decision

Streams accounts for queued transactions, changes, bytes, individual transaction size, spool storage, acknowledgement age, and WAL lag. Transaction assembly remains in memory up to configured limits and then spills to an internal disk spool.

Spool records use a versioned binary envelope, integrity checks, atomic completion markers, bounded storage, and encryption-at-rest hooks. Aborted transactions remove their staged records. Startup recovery removes incomplete records and retains complete, unacknowledged records when their source identity matches.

## Consequences

Large transactions remain one delivery unit without unbounded managed memory. Spool exhaustion pauses reading and surfaces health diagnostics rather than silently dropping data. The spool format receives upgrade tests before releases.
