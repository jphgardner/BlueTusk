# BlueTusk Control Plane and Dashboard

`BlueTusk.ControlPlane` and `BlueTusk.Dashboard` provide the Phase 4 operational foundation. They are independently versioned under the Control Plane release train and remain non-publishable until the later product dashboards and upgrade gates are complete.

## Read-only inventory

`PostgreSqlControlPlaneQueryService` reads registered relay sources from each configured control schema and combines them with source-server slot state. Its snapshots cover:

- source identity and epoch, last relay sequence, and appended commit position;
- logical-slot existence, activity, output plug-in, restart/confirmed positions, WAL status, and current byte lag;
- relay transaction count, byte use, retained sequence range, minimum group checkpoint, and oldest unacknowledged age;
- active and removed relay groups, checkpoints, generations, lease state, fencing history, and retention protection;
- snapshot epoch/state/progress size; and
- direct-consumer checkpoint format, output plug-in, mapping fingerprint, acknowledged position, generation, and lease state.

Each control-schema projection uses one `REPEATABLE READ` snapshot, so relay sources, groups, snapshots, and checkpoints are mutually coherent. Inventory rejects a missing or non-current relay schema version instead of interpreting columns from an unknown format. Slot state is observed separately on the source server and carries its own reachability signal.

Configure source and control data sources separately in production. Connection strings, credentials, lease-owner identities, snapshot progress payloads, row values, and dead-letter payloads are never returned by the inventory contract. A source-server connection failure produces the stable `source-unavailable` diagnostic for slot state rather than exposing an exception message. A missing logical slot is reported separately as `slot-missing`.

```csharp
var queries = new PostgreSqlControlPlaneQueryService(
    [new ControlPlanePostgreSqlSource(
        "production-eu",
        sourceDataSource,
        controlDataSource,
        "bluetusk_streams")]);
```

## Dashboard

`MapBlueTuskDashboard` adds an HTML dashboard and a JSON overview API. The initial pages cover sources, slots and WAL lag, relay storage, snapshots, consumer groups, and direct checkpoints. All values rendered into HTML are encoded.

```csharp
builder.Services.AddSingleton<IControlPlaneQueryService>(queries);
builder.Services.AddAuthorization(options =>
    options.AddPolicy("BlueTusk.ControlPlane.Read", policy =>
        policy.RequireRole("BlueTuskViewer", "BlueTuskOperator", "BlueTuskAdministrator")));

app.MapBlueTuskDashboard();
```

Every route is assigned the configured authorization policy. The package does not install a permissive fallback policy; a host that omits authorization configuration fails closed at request time.

## Mutating operations and audit

`ControlPlaneOperationExecutor` is the only provided command path. Every request has a client-generated operation ID, target, reason, and exact confirmation. `ControlPlaneOperationPolicies` requires an Operator for normal mutations and an Administrator for consumer-group removal, checkpoint rewind, and slot deletion. The required confirmation is the ordinal string `<OperationKind>:<Target>` and must be presented explicitly by the operator.

The executor records denied and confirmation-rejected attempts. An accepted request writes `Requested` before calling the host's `IControlPlaneOperationHandler`, followed by `Succeeded` or `Failed`. If the initial audit append fails, the handler is never invoked. Exception messages are not copied into audit records; failures use a non-sensitive type/detail code. If the handler completes but the success audit fails, the executor reports a reconciliation-required error containing no false `Failed` record; operators reconcile by the stable operation ID.

BlueTusk deliberately does not provide default slot-deletion or checkpoint-rewind handlers. A host must bind commands to its own coordinator and database permissions, keeping those destructive actions out of one-click read-only dashboard paths.

`PostgreSqlControlPlaneAuditStore` creates an append-only `audit_log` plus a database trigger that rejects `UPDATE` and `DELETE`. Run `InitializeAsync` with a migration owner, then give the application identity only schema usage, sequence usage, and insert privileges. Database owners can still drop database objects, so production audit retention also requires restricted ownership, PostgreSQL backups, and external log export appropriate to the organisation's compliance boundary.

## Verification status

The unit gate covers role escalation, exact confirmation, handler failure, non-sensitive audit details, authorization metadata on every dashboard endpoint, and hostile HTML values. Live PostgreSQL 15–19 acceptance creates a real logical slot, relay group, snapshot run, and direct checkpoint, verifies their inventory projections, initializes the audit schema idempotently, and proves stored audit rows reject update and delete attempts.
