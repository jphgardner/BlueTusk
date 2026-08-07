# ADR 0007: Persist checkpoints before replication feedback

- Status: Accepted
- Date: 2026-08-03

## Context

PostgreSQL may recycle WAL after receiving replication feedback. Reporting progress before the application checkpoint is durable can create an unrecoverable loss window. Concurrent owners can also corrupt progress without an ownership fence.

## Decision

An acknowledgement completes in this order: durable downstream handling, monotonic compare-and-swap checkpoint persistence, then PostgreSQL replication feedback. A failure before checkpoint persistence causes safe redelivery. A failure after persistence but before feedback may also redeliver, but cannot lose acknowledged work.

Checkpoint stores reject backward movement and compare the expected store generation. Lease stores grant exclusive ownership with monotonically increasing fencing tokens. Every checkpoint mutation and relay group acknowledgement carries the active token.

## Consequences

Delivery is at least once. Store implementations must pass a shared crash-injection and fencing conformance kit. Operational diagnostics distinguish destination, checkpoint, feedback, lease-expiry, and source-identity failures.
