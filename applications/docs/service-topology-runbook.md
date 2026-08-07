# Service Topology Centre RC runbook

Owner: platform operations. Data: `bluetusk-topology-rc/application`. Entry point:
`https://topology.192.168.1.230.nip.io`.

On graph divergence, suspend health ingestion, retain the graph checkpoint and Streams replay
position, force an authoritative evaluation from PostgreSQL, and compare node, edge, incident,
and ordering counts before resuming. A repair is complete only when ContinuousGraph reports a
committed result and no unreconciled element remains.

For restart evidence, terminate the worker during evaluation, restart from checkpoint/replay,
and verify the result sequence is monotonic. For PostgreSQL recovery, confirm the CloudNativePG
primary change, connection recovery, replay continuation, and Live resume without duplicate
topology changes. Cancellation must leave neither a committed graph result nor an advanced
checkpoint.

Restore to a new cluster, apply migrations, validate `service_topology_graph`, run authoritative
repair, and expose the Gateway only after lifecycle P95 and reconciliation pass. Roll back by
digest while retaining the database and checkpoint; never drop the property graph during an
application rollback.
