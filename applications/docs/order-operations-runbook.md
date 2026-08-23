# Order Fulfilment Operations RC runbook

Owner: fulfilment operations. Data: `bluetusk-orders-rc/application`. Entry point:
`https://orders.192.168.1.230.nip.io`.

For an incident, freeze operator mutations at the Gateway or role boundary, preserve the
API/worker logs and current Streams checkpoint, and record the latest immutable audit ID.
Restore service in this order: PostgreSQL primary readiness, migration Job completion,
worker relay, API readiness, UI, then Live resume. Re-run the same idempotency key for an
uncertain command; never edit aggregate state to repair it.

For relay recovery, stop the worker, compare unrelayed `orders.operational_audit` rows with
the read model/checkpoint, restart from the last committed checkpoint, and reconcile before
reopening mutations. Exercise one corrupted replay payload and one cancelled delivery in RC;
both must quarantine or clean up without advancing the checkpoint.

Database connection and update failures do not terminate the relay host. The
worker records `StoreUnavailable`, backs off for five seconds, and retries from
the durable unrelayed set. Alert on sustained failures and relay lag; do not
restart repeatedly or mark rows relayed by hand.

Restore into a new CloudNativePG cluster, run the migration Job, verify audit/order counts and
the latest IDs, then switch the Gateway only after replay lag reaches zero. Roll back application
images by digest; do not reverse a database migration unless its checked-in `Down` operation
has been rehearsed against a restored copy.

Before accepting a rollout, run
`./eng/verify-application-platform-health.ps1 -RequireApplications`. Treat any
API/kubelet mismatch, node pressure, degraded volume, unhealthy database,
unready worker, or failed migration as a deployment failure even when cached
Kubernetes status still says `Running`. Retain the command output with the image
digest manifest and rollout record.
