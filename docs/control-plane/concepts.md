# Control Plane concepts

This page explains the mental model behind the Control Plane: where its data
comes from, how it judges health, how operator actions are checked and
audited, and what it stores. For shared terms such as replication slot,
checkpoint, source identity and consumer group, read the
[core concepts](../getting-started/concepts.md) first.

## How the pieces fit

```text
  Streams relay tables ──┐
  Sync worker status ────┤                          ┌─► HTML pages  /bluetusk/...
  Live registry ─────────┼─► inventory services ───►│
  Graph registry ────────┤   (read only)            └─► JSON API    /bluetusk/api/v1/...
  Deployment store ──────┘

  Operator ─► POST /bluetusk/api/v1/operations ─► ControlPlaneOperationExecutor
              role check ─► confirmation check ─► audit "Requested"
              ─► your handler ─► audit "Succeeded" or "Failed"
```

The Control Plane runs inside your ASP.NET Core app. It has no agent of its
own and no background process. Each page or API call asks the inventory
services for a fresh view.

## Inventory

The inventory is a read-only, redacted view of what BlueTusk is running. Five
services supply it. The dashboard needs all five registered, even if some
products are not in use (return an empty overview for those, as the
[quick start](quickstart.md) does).

| Service | BlueTusk implementation | Reads |
| --- | --- | --- |
| `IControlPlaneQueryService` | `PostgreSqlControlPlaneQueryService` | Streams relay tables, plus `pg_replication_slots` on the source server |
| `IControlPlaneSyncQueryService` | `HostedSyncControlPlaneQueryService` | The status of hosted Sync workers (`IBlueTuskSyncStatusSource`) and the relay head from the query service above |
| `IControlPlaneLiveQueryService` | `HostedLiveControlPlaneQueryService` | The Live shared-subscription registry and invalidation log |
| `IControlPlaneContinuousGraphQueryService` | In the preview `BlueTusk.ContinuousGraph.ControlPlane` package | Registered graph queries |
| `IControlPlaneFleetQueryService` | `ManagedDeploymentFleetQueryService` | The managed-deployment store |

Redaction is built in. The inventory contains fingerprints, positions, counts
and stable diagnostic codes. It never contains connection strings,
credentials, lease-owner identities, row values, query parameters, Live
security scopes (only a category and a truncated hash), workload settings,
secret-reference names or exception messages.

## Instances and sources

A **Control Plane instance** is one `ControlPlanePostgreSqlSource`: a name, a
data source for the PostgreSQL server you capture from, and a data source for
the database that holds the relay tables. One query service can read many
instances in parallel.

Each instance can hold several **sources**: one per source identity
registered in its relay. A source's key is `<instance name>:<source
fingerprint>`, and it owns:

- one **replication slot** on the source server;
- **relay storage**: retained transactions and their sequence range;
- **consumer groups**: each with its own checkpoint, lease and fencing token;
- **snapshot runs**: initial copies made with the snapshot bootstrap;
- **direct checkpoints**: consumers that read the slot without the relay.

A source appears in the inventory only after a relay worker has registered it.
See [Durable relay](../streams/durable-relay.md).

## Health

The overview counts each item as healthy or "needs attention" with these
rules:

| Item | Healthy when |
| --- | --- |
| Source | The source server is reachable, the slot exists and is active, and there is no diagnostic code |
| Sync pipeline | State is `Running`, nothing is quarantined, and there is no diagnostic or lag diagnostic code |
| Live subscription | It is started, its invalidation lag is 0, and there is no lag diagnostic code |
| Managed deployment | State is `Ready`, it is not paused, observed generation equals desired generation, and there is no diagnostic code |

When a value cannot be measured, the inventory reports a diagnostic code
instead of a guess:

| Code | Meaning |
| --- | --- |
| `source-unavailable` | The source server could not be reached to read slot state |
| `slot-missing` | The registered slot does not exist on the source server |
| `source-head-unavailable` | A Sync pipeline's source is not in the relay inventory, so lag is unknown |
| `checkpoint-ahead-of-source` | A Sync checkpoint is ahead of the relay head |
| `invalidation-cursor-regressed` | A Live subscription's cursor is ahead of the invalidation log head |

**Freshness.** Every response carries `observedAt`. The PostgreSQL inventory
is cached for 250 ms by default, so bursts of requests share one read. Sync
throughput is the change between two observations, so the first view of a
pipeline shows no rate. Pages do not refresh by themselves; use **Refresh**.

## Operations

An **operation** is a request to change something. It has an operation ID
(a client-generated GUID), a kind, a target, a confirmation and a reason.

| Kinds | Target from the dashboard | Required role | Handler provided |
| --- | --- | --- | --- |
| `RetryPipeline`, `ReconcilePipeline`, `RebuildPipeline`, `ReplayQuarantine` | `pipeline:<pipeline id>` | Operator | No, you write it |
| `PauseSource`, `ResumeSource`, `PauseConsumerGroup`, `ResumeConsumerGroup` | None (API only) | Operator | No |
| `RemoveConsumerGroup`, `RewindCheckpoint`, `DeleteSlot` | None (API only) | Administrator | No |
| `PauseDeployment`, `ResumeDeployment`, `ReconcileDeployment`, `RebuildDeployment` | `deployment:<deployment id>` | Operator | Yes |
| `DeleteDeployment` | `deployment:<deployment id>` | Administrator | Yes |

The **confirmation** must equal `<Kind>:<Target>` exactly, for example
`PauseDeployment:deployment:orders`. The dashboard asks the operator to type
it. Destructive kinds (the four that need Administrator) are never one-click.

BlueTusk deliberately ships no handler for slot deletion, checkpoint rewind or
pipeline control. Your `IControlPlaneOperationHandler` decides what each kind
does in your system. See [Enable operator actions](operations.md).

## Fleet operations

**New in 1.1.0.** A **managed deployment** is a desired-state record for one
tenant's BlueTusk workloads (Streams, Sync, Live, ControlPlane, Dashboard,
ContinuousGraph) in one provider and region. `ManagedDeploymentController`
reconciles it through an `IManagedInfrastructureProvider` that you supply.

- **Desired generation** increases by one each time the desired state
  changes. **Observed generation** is the generation last reconciled.
- **State** is one of `Pending`, `Planning`, `Applying`, `Ready`, `Degraded`,
  `Paused`, `Deleting`, `Deleted` or `Failed`.
- Each reconciliation holds a lease and passes a **fencing token** to the
  provider, so two Control Plane hosts never apply the same deployment at
  once.

`ManagedDeploymentControlPlaneOperationHandler` turns fleet operations into
controller calls:

| Operation | What happens |
| --- | --- |
| `PauseDeployment` / `ResumeDeployment` | Sets `Paused` in the desired state (a new generation), then reconciles |
| `ReconcileDeployment` | Reconciles now |
| `RebuildDeployment` | Calls your `IManagedDeploymentRebuildHandler`, then reconciles |
| `DeleteDeployment` | Deletes through the provider. Stops with `delete-protection-enabled` if delete protection is on |

Delete protection cannot be overridden from the dashboard. Turn it off in the
desired state first.

## Audit

`ControlPlaneOperationExecutor` writes audit records through an
`IControlPlaneAuditStore` in this order:

1. If the actor lacks the role: `Denied` with detail `role-denied`. Stop.
2. If the confirmation does not match: `Rejected` with detail
   `confirmation-mismatch`. Stop.
3. `Requested`. If this write fails, the handler is never called.
4. Your handler runs.
5. `Succeeded`, or `Failed` with the exception type name (or `cancelled`) as
   the detail.

Records hold the operation ID, time, actor ID, kind, target, status, reason
and detail code. They never hold exception messages. If the handler succeeds
but the `Succeeded` record cannot be written, the API reports a failure; look
the operation up by its ID before retrying.

`PostgreSqlControlPlaneAuditStore` keeps records in an append-only table. A
trigger rejects `UPDATE` and `DELETE`.

## Authorization

Two layers protect the Control Plane:

1. **Endpoint policies.** ASP.NET Core authorization policies guard routes:
   a read policy for every page and GET API, a mutation policy for
   `/api/v1/operations`, and a graph-execution policy for running graph
   queries. You define the policies.
2. **Control Plane roles.** For operations, the dashboard maps the signed-in
   user's ASP.NET Core roles to `Viewer`, `Operator` and `Administrator`
   (role names are configurable). `RoleControlPlaneAuthorizer` allows an
   operation when the user has the required role or a higher one.

The actor ID comes from the `NameIdentifier` claim, or the identity name.
Clients cannot submit an actor. Operation buttons appear only for users in the
Operator or Administrator role, but the server checks every request anyway.

## Persistence

The dashboard itself is stateless. These tables hold everything it shows or
records:

| Data | Schema (default) | Created by |
| --- | --- | --- |
| Relay sources, transactions, consumer groups, snapshot runs, checkpoints | `bluetusk_streams` | `PostgreSqlDurableChangeRelay.InitializeAsync` (Streams) |
| Audit log | `bluetusk_control` | `PostgreSqlControlPlaneAuditStore.InitializeAsync` |
| Managed deployments, leases | `bluetusk_control` | `PostgreSqlManagedDeploymentStore.InitializeAsync` |

Both Control Plane stores record a schema version and refuse to write when the
database was migrated by a newer version. `InMemoryManagedDeploymentStore` is
for tests and single-process development only.

## Next steps

- [Enable operator actions](operations.md)
- [Configuration](configuration.md)
- [Full Control Plane reference](reference.md)
