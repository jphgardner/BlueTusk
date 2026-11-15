# BlueTusk real-time platform

BlueTusk can react after PostgreSQL commits a change. Start with the outcome you
need; most applications do not need every product.

## Choose one starting point

| You need to…                                                              | Start with                                        | What it gives you                                                                    |
| ------------------------------------------------------------------------- | ------------------------------------------------- | ------------------------------------------------------------------------------------ |
| Process committed changes in .NET                                         | [Streams](../streams/README.md)                   | Complete transactions, checkpointing, leases, spooling, and snapshots.               |
| Feed several independent consumers from one slot                          | [Durable relay](../streams/durable-relay.md)      | Retained transactions and independently acknowledged groups.                         |
| Keep Redis, OpenSearch, NATS, Kafka, PostgreSQL, S3, or a webhook current | [Sync](../sync/README.md)                         | Transforms, destination guarantees, retries, reconciliation, and rebuilds.           |
| Push a bounded query result to connected users                            | [Live](../live/README.md)                         | Authorized queries, keyed diffs, replay, resume tokens, and browser clients.         |
| Maintain a changing graph result                                          | [Continuous Graph](../continuous-graph/README.md) | Incremental and authoritative SQL/PGQ maintenance under the original security scope. |
| Inspect and operate the deployment                                        | [Control Plane](../control-plane/README.md)       | Redacted inventory, drill-down dashboard, authorization, and audit.                  |

## How the pieces connect

```text
PostgreSQL 15–19
       ↓
Data / COPY / Replication / pgoutput
       ↓
BlueTusk Streams ─→ PostgreSQL durable relay
       ├──────────→ BlueTusk Sync
       └──────────→ BlueTusk Live ─→ Continuous Graph
                              ↑
                    Control Plane / Dashboard
```

Build and prove Streams first. Add a relay when more than one independently
recoverable consumer needs the feed. Add Sync, Live, or Continuous Graph only
for the corresponding outcome.

Streams is the only application-level CDC boundary. Sync, Live, and Continuous
Graph consume Streams deliveries or relay cursors; they do not reach into
replication protocol internals.

## Correctness contract

- Delivery is ordered, transaction-preserving, and at least once. Exactly once is not claimed.
- Durable downstream handling precedes checkpoint persistence; checkpoint persistence precedes PostgreSQL feedback.
- Checkpoints are monotonic compare-and-swap records bound to a source identity and lease fencing token.
- Direct groups own independent slots. Streams also includes PostgreSQL relay
  fan-out from one slot.
- All memory, transaction, spool, acknowledgement-age, and WAL-lag queues are bounded.
- Exported snapshots restart with a new epoch after exporter/session loss; an expired snapshot is not resumable.
- Live uses CDC as invalidation and reruns an authorised bounded EF query before emitting client-visible data.
- Sync advances only after a destination confirms durable handling of the complete source transaction.

See the [public contracts](contracts.md), [delivery phases](delivery-plan.md), and accepted [architecture decisions](../architecture/decisions).

## Deploy a real-time pipeline

Provision one logical replication slot per intended source, choose durable
checkpoint storage, and establish who acknowledges each transaction. Grant
the consumer only its required replication and data permissions. Use the
[operating guide](operations.md) for lag alerts, replay, and failover.
