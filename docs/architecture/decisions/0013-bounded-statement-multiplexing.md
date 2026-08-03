# ADR 0013: Use bounded, session-neutral statement multiplexing

- Status: Accepted
- Date: 2026-08-04

## Context

High-concurrency applications should not need one physical PostgreSQL session for every logical command. Multiplexing arbitrary commands is unsafe, however, because transactions, temporary objects, settings, listeners, cursors, advisory locks, COPY, and prepared-statement state belong to a physical session.

## Decision

Multiplexing is opt-in on `BlueTuskDataSource`. A bounded channel feeds a fixed number of persistent physical worker lanes. The automatic worker count uses at most half of the configured pool, capped at four, so ordinary session-affine checkouts retain capacity. Queue size, commands per lease, commands per pipeline flush, and graceful shutdown time all have explicit bounds.

Only commands created directly from the data source are eligible. Explicit connections and transactions remain session-affine. A conservative SQL classifier routes known stateful statements and routines away from multiplexing. `Auto`, `Require`, and `Disable` let a caller accept fallback, fail closed, or pin a command deliberately.

Each eligible command is its own PostgreSQL pipeline synchronization group. Errors, cancellation, and timeouts therefore cannot advance or poison adjacent commands. Scalar responses retain only their first value; repeated row descriptions and statement shapes are reused; transient batch storage is pooled. Text result format remains the safe default because PostgreSQL user-defined types are not required to expose binary output.

Shutdown first drains accepted work. After the configured deadline it closes the active physical transport, completes queued commands with disposal errors, and waits for every worker to stop. Scheduler statistics expose queue depth, active work, completions, failures, cancellations, pipeline flushes, and pipelined command counts.

## Consequences

Multiplexed commands gain bounded fan-in and fewer pool checkouts while retaining explicit per-command error boundaries. Worker lanes consume physical pool capacity while active, so worker count must be sized with session-affine work. SQL classification cannot prove that an arbitrary user-defined function is session-neutral; trusted stateful functions must use `Disable`, and untrusted client SQL requires the separate capability-policy layer.
