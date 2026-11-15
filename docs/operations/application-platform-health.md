# Diagnose deployment health

Use separate startup, liveness, and readiness checks. Liveness answers whether
the process can continue running. Readiness answers whether that instance can
serve its workload with the expected database and downstream dependencies.

## Check the database path

Use the application’s BlueTusk data source and a restricted role to check
connectivity. Bound the check with a short timeout and cancellation. For a
write workload, confirm that the database is the intended writable primary.
Verify the schema or storage contract separately from network reachability.
Use fresh, meaningful status for the affected instance.

## Check real-time workers

Look at the last committed checkpoint, WAL lag, retained slot bytes, and
successful delivery time. Check spooling, retry pressure, and durable state
before restarting a worker. A healthy HTTP endpoint does not establish that
a Streams or Sync pipeline is keeping up.

Use [real-time operations](../realtime-platform/operations.md) for recovery
sequences and [observability](observability.md) for metrics and alerts.

## Check the hosting platform

On Kubernetes, verify the assigned node is healthy, the intended database
and storage are available, and the workload is ready on the expected
revision. Check readiness and endpoint freshness after a rollout or failover.
Inspect failed migration jobs and their database state before retrying.
Run migrations as a controlled action outside the application startup loop.

## Recover a degraded deployment

Fix the underlying connectivity, permission, or resource problem before
restarting consumers. Confirm that the retained checkpoint resumes and that a
fresh query matches the served result. Resume traffic gradually and monitor
errors, connection checkout time, and data freshness.
