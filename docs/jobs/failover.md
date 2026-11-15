# Recover jobs after PostgreSQL failover

Keep the old primary fenced and direct job workers to a single writable
primary. The job lease protects updates in that database. It cannot reconcile
two independently writable PostgreSQL histories.

## Prepare the worker

1. Connect using a PostgreSQL endpoint that tracks the writable primary.
2. Use compatible worker and database schemas on both sides of promotion.
3. Size the lease duration for normal handler runtime and heartbeat jitter.
4. Give each external effect a durable idempotency identity derived from
   the job and operation. A job may run again if it lost its lease before it
   recorded completion.

Keep recovery bounded with connection, command, handler, and heartbeat
timeouts. Observe lease-renewal failures and oldest-due-job age so a stopped
worker or an unavailable primary is visible before the queue backs up.

## Promote and reconnect

Pause or drain consumers if the failover is planned. Fence writes to the old
primary, promote the replica, and reconnect workers through the intended
database endpoint. Verify the target is writable using the same scoped
identity as the worker. Confirm the schema is compatible before reopening
admission.

An expired lease can be acquired by a new owner. Updates from an old owner
cannot complete or renew a job under its stale fence. External systems still
need their own idempotency protection because a process can fail after
performing work and before confirming it to PostgreSQL.

## Verify recovery

Check the durable queue for overdue work, terminal failures, and completion
records. Compare committed application effects with the corresponding job
identities. If promotion lost committed state, follow the database recovery
policy before letting workers resume. See [host the worker](hosting.md) for
worker timing and [upgrade Jobs](upgrade.md) for schema changes.
