# ADR 0009: Bound transaction memory and spill to a versioned spool

- Status: Accepted
- Date: 2026-08-03

## Context

Logical replication can stream transactions larger than process memory. Bounds on channel capacity alone do not constrain a single transaction, and partial delivery would break the transaction-preserving contract.

## Decision

Streams accounts for queued transactions, changes, bytes, individual transaction size, spool storage, acknowledgement age, and WAL lag. Transaction assembly remains in memory up to configured limits and then spills to an internal disk spool.

Spool records use a versioned binary envelope, integrity checks, atomic completion markers, bounded storage, and encryption-at-rest hooks. Aborted transactions remove their staged records. The spool is not a durable restart log: safe redelivery comes from PostgreSQL or the durable relay. Existing spool artifacts count against the storage limit after restart; an operator removes confirmed orphans only while the worker is stopped.

Version 1 keeps its CRC-32/IEEE checksum, little-endian framing, completion footer, and flush-to-disk boundary. The implementation uses the pinned `System.IO.Hashing` library's optimized checksum paths, with a compatible fallback when hardware acceleration is unavailable. One incremental hasher is reused for a writer's segmented records and reset between records; payload ownership and custom protector behavior are unchanged. CRC detects corruption, not malicious modification or disclosure; it does not replace an authenticated protector.

## Consequences

Large transactions remain one delivery unit without unbounded managed memory. Spool exhaustion pauses reading and surfaces health diagnostics rather than silently dropping data. The spool format receives upgrade tests before releases.
