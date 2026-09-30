# Durable storage maintenance contract

Jobs and Workflows bound admitted payloads, in-memory claims, attempt/history
entries and individual pruning batches. Those bounds do not impose a physical
database or WAL disk limit. The host owns admission/backlog quotas and a regular
retention loop for every tenant/queue; PostgreSQL operators own page reuse,
autovacuum, transaction-age and WAL/disk budgets. Deleting rows is only the first
stage of storage maintenance.

Choose terminal retention longer than upstream retry/redelivery and external
idempotency windows. Pruning releases deduplication identities, so a later
redelivery can create new work. Workflows prune their completed instances and
cascaded nodes/history/signals; their dispatch Jobs require their own pruning.
Definitions and recurring schedules are durable configuration and have separate
operator-managed lifetimes. Waiting signals/timers and permanently running
instances need an explicit application lifetime/cancellation policy; terminal
retention cannot delete them.

Run bounded `PruneAsync` batches with a finite per-scope/time budget and reschedule
scopes fairly. Observe returned counts, capped active counts and ready age,
terminal age, durable throughput and rejected offers. Never increase admission
indefinitely to conceal overload. A deleted row can remain physically needed by
an older database snapshot. Check long transactions, replication slots and other
vacuum blockers when live counts fall but dead rows or relation files grow.

Standard vacuum makes dead table/index space reusable. It usually keeps files
allocated; this is useful when subsequent work reuses those pages. A physical
plateau therefore means a stable relation-byte range across repeated cleanup
cycles under sustained arrivals, rather than zero retained rows or file shrinkage.
Keep autovacuum enabled. Supplement or tune it based on measured churn and dead
rows; the tested cadence below is a reference workload policy, not a universal
setting. PostgreSQL documents the distinction between standard vacuum and table
rewrites in its [routine vacuuming guidance](https://www.postgresql.org/docs/15/routine-vacuuming.html).

The explicit operator action is ordinary
`VACUUM (ANALYZE, TRUNCATE FALSE, INDEX_CLEANUP ON)` for each known runtime table,
outside a transaction. `TRUNCATE FALSE` avoids requesting the exclusive lock for
tail truncation. The [scoped psql script](../../eng/jobs-workflows-vacuum.sql)
quotes schema/table identifiers, selects only known Jobs/Workflows tables, stops
on failure and uses a two-second lock deadline and a 30-second statement deadline.
It rejects missing requested runtime tables before maintenance. For a Jobs-only
deployment, supply an empty `workflows_schema` variable; at least one configured
runtime target is required.
Use an operator identity with the maintenance privileges appropriate to the
PostgreSQL version; runtime CRUD grants are insufficient. The exact options and
transaction restriction are in [PostgreSQL VACUUM documentation](https://www.postgresql.org/docs/15/sql-vacuum.html).
Run one maintenance process per schema, observe durations/errors, and retry a
timed-out cycle on the next scheduled pass rather than spin indefinitely.

This operation neither prunes live state nor shrinks files by rewrite. It does
not disable autovacuum, force a global checkpoint, restart PostgreSQL or issue
`VACUUM FULL`. A rewrite/reindex/partition strategy requires its own measured
maintenance window, disk headroom and lock/recovery qualification. Set database
and filesystem budgets independently; checkpoint/WAL size settings are not a
complete hard cap when replication/archive retention or stalled slots retain WAL.

Record physical table/index/TOAST bytes, estimated live/dead rows, vacuum and
autovacuum timestamps/counts, oldest transaction age, checkpoint write/sync time,
WAL rate, available filesystem space, replication-slot retained WAL and durable
latency. PostgreSQL statistics may lag and dead/live row statistics are estimates;
use direct bounded application counts for lifecycle assertions. Trigger/audit or
business-effect tables have their own retention budget and are not runtime-state
storage. A stable runtime schema says nothing about an unbounded business ledger.

The [storage campaign](performance.md) tests two seconds of terminal retention,
four tenant scopes with 32 reserved slots each, four workers per scope, a
32-connection pool and 1 KiB compressible padding. After 120 seconds of default
autovacuum observation it supplements maintenance every 30 seconds without tail
truncation. This measures reuse under that specific workload. Production
operators must establish their own cadence, disk envelope and sustained-load
thresholds with real payloads and retention before relying on a storage budget.
