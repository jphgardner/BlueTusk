# Choose a real-time product

BlueTusk's real-time products let your application react after PostgreSQL
commits a change. Use this page to pick the product you need. Most
applications need only one or two of them.

## Pick by outcome

| You want to | Use | Start with |
| --- | --- | --- |
| Run .NET code for every committed change, in commit order | [Streams](../streams/README.md) | [Streams quick start](../streams/quickstart.md) |
| Feed several independent consumers from one replication slot | Streams [durable relay](../streams/durable-relay.md) | [Durable relay](../streams/durable-relay.md) |
| Keep another PostgreSQL database, Redis, NATS, OpenSearch, Kafka, S3 or a webhook up to date | [Sync](../sync/README.md) | [Sync quick start](../sync/quickstart.md) |
| Show users a query result that updates by itself | [Live](../live/README.md) | [Live quick start](../live/quickstart.md) |
| See and operate the running components | [Control Plane](../control-plane/README.md) | [Control Plane quick start](../control-plane/quickstart.md) |
| Keep a graph query result current (preview) | [Continuous Graph](../continuous-graph/README.md) | [Graph guide](../graph/README.md) |

Not sure? Start with Streams. Sync and Live are built on it, and the Streams
quick start teaches the setup every real-time product needs.

## How the products connect

```text
PostgreSQL (wal_level = logical)
        │  logical replication
        ▼
     Streams ──────────► durable relay (optional, in PostgreSQL)
        │
        ├──► Sync ──────► another database, cache, broker, index or webhook
        │
        ├──► Live ──────► browsers and .NET clients
        │
        └──► Continuous Graph (preview)

 Control Plane shows and operates all of them.
```

- **Streams** is the only product that reads PostgreSQL's replication
  protocol. It turns the write-ahead log into complete, ordered, committed
  transactions.
- **Sync** and **Live** consume Streams. They never read the replication
  protocol directly.
- The **durable relay** stores committed transactions in PostgreSQL so several
  consumers can share one replication slot and acknowledge independently.
- **Control Plane** does not process changes. It shows the inventory and
  health of the other products and runs audited operations on them.

Build and test your Streams setup first. Add a relay when more than one
consumer needs the same changes. Add Sync or Live only for the outcome you need.

## What every product guarantees

All real-time products share one delivery model:

- Changes arrive as **whole transactions**, in **commit order**.
- Delivery is **at least once**. After a crash, the last unacknowledged
  transaction can arrive again. Every change has a stable identity so you can
  detect the repeat. BlueTusk does not claim "exactly once".
- A transaction is **acknowledged** only after its effect is durable, and the
  **checkpoint** moves only after that.
- A checkpoint belongs to one **source identity** (cluster, database, slot and
  publication). BlueTusk refuses to resume from a checkpoint that belongs to a
  different source, for example after a restore into a new cluster.
- Every queue, buffer and spool has a configured limit. When a limit is
  reached, BlueTusk stops with an error instead of dropping changes; Live
  disconnects a client that cannot keep up.
- **Live** uses a change only as a signal. It re-runs the registered query
  with the subscriber's permissions before it sends anything to a client.
- **Sync** moves its checkpoint only after the destination confirms it has
  stored the whole source transaction.

[Delivery guarantees](contracts.md) defines each rule exactly.
[Core concepts](../getting-started/concepts.md) explains the vocabulary.

## Before production

- [Recovery and rebuilds](operations.md): restart, replay, rebuild and
  failover procedures.
- [Security](../security.md): roles, replication privileges and what each
  product exposes.
- [Observability](../operations/observability.md): metrics, traces and
  alerts.
- [Production checklist](../operations/production-checklist.md).

For the design history, see the [architecture decisions](../architecture/decisions/)
and the [delivery plan](delivery-plan.md).
