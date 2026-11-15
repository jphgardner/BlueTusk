# Upgrade Jobs storage and workers

Job data and workflow state can survive a process restart. Before a package
upgrade, read the [durable format](durable-format.md) documentation and
confirm that the target application can read the entries and payload versions
the existing deployment writes.

## Rehearse

1. Restore a backup to an isolated PostgreSQL database.
2. Record counts by job state and sample durable job IDs.
3. Pause old workers and run the new version’s controlled initialization step.
4. Start a new worker and verify it can process a queued job, retry a failed
   job, and observe a terminal job without rerunning it.

Confirm that payloads and their type or version identifiers still resolve to
the intended registered handlers. Keep any old handler or deserializer needed
by pending and retryable jobs.

## Deploy and verify

Drain or pause workers before an incompatible schema or handler change.
Apply database changes with the deployment identity and start an updated
worker using its normal restricted role. Verify readiness, queue age, lease
renewals, and terminal outcomes before restoring full concurrency.

If the new version changed a durable format the old version cannot read,
restore a compatible backup or use the documented migration procedure before
rolling back application binaries. See [failover](failover.md) for the
database promotion sequence.
