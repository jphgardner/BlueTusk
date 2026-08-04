# Real-time operations

Operating Streams, Sync, Live and Continuous Graph requires preserving the
relationship between PostgreSQL source identity, durable progress and
downstream effects.

## Operational state

Inventory and back up:

- PostgreSQL publication and replication slot definitions;
- source identity;
- snapshot and stream checkpoints;
- schema and mapping fingerprints;
- durable relay segments, indexes and consumer-group positions;
- Sync destination version/idempotency metadata;
- quarantine/dead-letter records; and
- Control Plane deployment/audit state.

A database backup without the corresponding consumer state may not be enough to
resume safely.

## Normal startup

1. Validate configuration and source identity.
2. Open the replication-capable source.
3. Load the durable checkpoint.
4. Validate schema/mapping fingerprints.
5. Recover incomplete relay or destination work.
6. Start reading from the recorded WAL position.
7. Expose readiness only after the component can make safe progress.

If no checkpoint exists, choose explicitly between “start now” and the fenced
snapshot bootstrap.

## Delivery and acknowledgement

The safe ordering is:

```text
read committed source transaction
  → validate mapping/version
  → apply durable side effect
  → persist destination/consumer progress
  → acknowledge source delivery
```

The exact atomic boundary differs by destination. When progress and side effect
cannot be committed together, use idempotency/version checks and
reconciliation.

## Process failure

After an unclean stop:

- reconnect using the same source identity;
- resume from the durable checkpoint;
- re-deliver any transaction not safely acknowledged;
- let destination idempotency/version checks absorb duplicates; and
- reconcile partial external work before advancing.

Do not skip a transaction merely because part of its effect is visible.

## Network interruption

Distinguish:

- source connection loss;
- checkpoint/state-store loss;
- relay storage loss; and
- destination connection loss.

Back off with bounded jitter, retain cancellation responsiveness and avoid
opening unbounded replacement connections. Readiness should reflect whether the
component can safely progress, while liveness should reflect process health.

## Storage pressure

Alert on:

- PostgreSQL WAL retained by the slot;
- transaction spool disk;
- relay bytes and oldest retained position;
- checkpoint-store failures;
- quarantine growth; and
- destination rebuild sidecar storage.

Fail closed before the host filesystem is exhausted. Retention must never delete
data still required by the slowest protected consumer group.

## Credential rotation

Use a credential provider or host configuration that can obtain a new
credential without rebuilding application state. Rotation may require
reconnecting physical sessions. Drain or invalidate old pooled sessions
according to the provider’s credential lifecycle.

Test source, state store and every destination credential independently.

## Primary failover

After failover:

1. route to a role-compatible server;
2. validate PostgreSQL system/source identity;
3. verify publication and slot continuity;
4. confirm the checkpoint WAL position is available;
5. reconcile any transaction whose acknowledgement outcome was uncertain; and
6. resume while monitoring lag and duplicates.

A recreated cluster with the same DNS name is not automatically the same
source.

## Clock movement

Ordering uses PostgreSQL/WAL and explicit sequence/version identities, not wall
clock alone. Wall-clock movement still affects:

- token expiry;
- retention age;
- alert windows;
- time-based destination fields; and
- operational reports.

Use UTC for persisted operational timestamps and test forward/backward clock
movement as required by the endurance contract.

## PostgreSQL minor upgrade

Rehearse:

- clean stop or failover;
- slot/publication persistence;
- reconnect and capability discovery;
- checkpoint continuity;
- extension compatibility; and
- lag recovery.

Bind the test to the exact images and candidate artifacts used in the endurance
report.

## Reconciliation and rebuild

Reconciliation compares authoritative source/version state with destination
state without advancing the normal checkpoint merely because a repair was
attempted.

Use a rebuild when:

- mapping compatibility cannot be preserved;
- destination state is untrustworthy;
- source/schema identity changed intentionally; or
- retention no longer contains the required replay range.

Build new destination state alongside the old version where possible, validate
it, then atomically switch an alias/pointer.

## Observability

Minimum signals:

| Signal | Why |
| --- | --- |
| Source WAL position and lag | Detect retention/storage risk |
| Last acknowledged checkpoint and age | Detect stalled progress |
| Transactions/rows/bytes processed | Establish throughput and workload shape |
| Relay oldest/newest positions and bytes | Protect replay and disk capacity |
| Destination apply latency/failures | Separate source health from sink health |
| Quarantine count and oldest age | Prevent silent permanent failure |
| Reconciliation differences/repairs | Detect destination drift |
| Restart/reconnect/failover counts | Expose instability |

Trace identifiers should connect source transaction identity to relay and
destination operations without exposing row contents.

## Release evidence

The exact V1 gates require:

- 72 continuous hours for Streams;
- 24 continuous hours for Sync;
- process death;
- network interruption;
- storage exhaustion;
- credential rotation;
- primary failover;
- clock movement; and
- PostgreSQL minor upgrade.

Every report is bound to source commit, package hashes, runtime, operating
system and image digests. A candidate code change invalidates and restarts the
applicable run.

See [Streams endurance](../streams/release-endurance.md) and
[Sync endurance](../sync/release-endurance.md).
