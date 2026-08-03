# Real-time platform contracts

## Streams

`IChangeStream` exposes ordered `ChangeTransactionDelivery` values. `ChangeTransaction` and its changes are immutable; only the delivery object can acknowledge progress. Insert, update, delete, and truncate changes carry `ChangeRow<T>` values whose columns distinguish value, database null, not published, unavailable old value, unchanged TOAST, and decoding failure. `ChangedColumnSet` explicitly reports exact or unknown knowledge.

`ChangeId` combines source identity, commit-end LSN, transaction ID, and ordinal. Snapshot rows instead combine snapshot epoch, table, and key. A checkpoint records its format version, PostgreSQL system and database identity, slot, output plug-in, canonical publication fingerprint, mapping fingerprint, acknowledged commit position, and store generation.

Checkpoint stores provide monotonic compare-and-swap persistence. Lease stores provide exclusive ownership and fencing tokens. Transaction spools provide bounded memory followed by integrity-checked, encryption-compatible disk spill. Custom stores and spools must pass public conformance kits.

The default schema response is `PauseAndReload`; typed decoding failures also pause by default. Alternative fail, dynamic, and callback policies are explicit configuration.

## Durable relay

The PostgreSQL relay owns a configurable control schema, `bluetusk_streams` by default, in a separate control data source. It stores source epochs, versioned transaction envelopes, group checkpoints and leases, snapshot progress, quarantine records, and retention watermarks. Publications containing the control schema are rejected.

Retention requires every applicable group to pass a transaction and the resume window to expire. Envelope versions and checksums are verified before delivery.

## Sync

`ISyncDestination` advertises transactional batches, idempotent upserts, deletes, checkpoint co-location, reconciliation, and alias-swap support. Source transactions stay intact. Transform fingerprints are stable and changes require an explicit migration or rebuild. Poison records pause unless an operator explicitly chooses quarantine-and-advance.

PostgreSQL atomically applies writes and its destination checkpoint. NATS uses stable change IDs for JetStream deduplication. Redis uses idempotent materialisation with atomic scripts or batches. OpenSearch uses stable document IDs, bulk operations, versioned rebuild indexes, and alias swaps. All four connectors must pass the same conformance suite before the first Sync preview.

## Live

Trusted server code registers query plans; clients can only supply typed parameter values. Initial support is one keyed table, simple predicates, tenant filters, deterministic ordering, and bounded `Take`. CDC invalidation causes an authorised EF requery and keyed diff producing initial, add, update, remove, reorder, or reset events.

Subscription identity includes database, query fingerprint, parameters, security scope, policy version, and result limit. Signed, expiring resume tokens bind that identity to a delivered sequence. Relay retention provides bounded replay. Cross-scope subscription sharing is forbidden.

## Control Plane and Continuous Graph

Control Plane and Dashboard expose source, slot, WAL, relay, group, checkpoint, snapshot, pipeline, retry, quarantine, reconciliation, subscription, quota, and replay health. Mutations require role-based authorisation, confirmation, and immutable audit records; slot deletion and checkpoint rewind are never one-click defaults.

Continuous Graph initially registers capability-guarded SQL/PGQ plans, extracts table dependencies, and uses transaction invalidation followed by authoritative `GRAPH_TABLE` requery and diff. Incremental graph maintenance is deferred beyond the preview.

The preview compiler accepts trusted typed EF graph factories only. A
registration names the configured graph element-table aliases it uses; those
aliases are validated against EF property-graph metadata and become the exact
Live table dependency set. Plans require PostgreSQL 19 SQL/PGQ capability,
deterministic ordering including a direct result key, and one bounded `Take`.
The compiled plan delegates initial cursor reservation, security-scoped
identity, invalidation coalescing, authoritative requery, and keyed diff/reset
to Live.
