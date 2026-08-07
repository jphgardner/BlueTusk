# BlueTusk Control Plane and Dashboard

`BlueTusk.ControlPlane` and `BlueTusk.Dashboard` provide the Phase 4 operational foundation plus the Phase 5 Sync, Phase 6 Live, and Phase 7 Continuous Graph projections. They are independently versioned under the Control Plane release train. The Control Plane implementation and upgrade gates are complete, but the release manifest remains non-publishable until the Sync release dependency has archived its required 24-hour endurance evidence.

## Read-only inventory

`PostgreSqlControlPlaneQueryService` reads registered relay sources from each configured control schema and combines them with source-server slot state. Its snapshots cover:

- source identity and epoch, last relay sequence, and appended commit position;
- logical-slot existence, activity, output plug-in, restart/confirmed positions, WAL status, and current byte lag;
- relay transaction count, byte use, retained sequence range, minimum group checkpoint, and oldest unacknowledged age;
- active and removed relay groups, checkpoints, generations, lease state, fencing history, and retention protection;
- snapshot epoch/state/progress size; and
- direct-consumer checkpoint format, output plug-in, mapping fingerprint, acknowledged position, generation, and lease state.

Each control-schema projection uses one `REPEATABLE READ` snapshot, so relay sources, groups, snapshots, and checkpoints are mutually coherent. Inventory rejects a missing or non-current relay schema version instead of interpreting columns from an unknown format. Slot state is observed separately on the source server and carries its own reachability signal.

`HostedSyncControlPlaneQueryService` combines that relay/source inventory with
the immutable hosted-worker status source. Its redacted projection covers
pipeline state, sampled transaction throughput, relay-head checkpoint lag,
snapshot progress, retries, throttle time, quarantine totals, failure totals,
reconciliation/rebuild state, and handoff completion. A missing source head or
a checkpoint ahead of its registered source is reported as a stable diagnostic
code instead of guessed lag. Worker exception messages never enter the
control-plane projection.

`HostedLiveControlPlaneQueryService` projects shared-subscription counts,
subscriber fan-out, invalidation lag, replay and resume activity, quota
pressure, authoritative-query counts, and disconnect causes. It never returns
query parameters or result rows. Subscription, plan, and parameter identities
remain fingerprints, while the default security-scope redactor preserves only
a safe category and a truncated one-way hash. Cursor regressions are reported
as a stable diagnostic instead of being rendered as misleading lag.

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
builder.Services.AddSingleton<IControlPlaneSyncQueryService,
    HostedSyncControlPlaneQueryService>();
builder.Services.AddSingleton<IControlPlaneLiveQueryService,
    HostedLiveControlPlaneQueryService>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("BlueTusk.ControlPlane.Read", policy =>
        policy.RequireRole("BlueTuskViewer", "BlueTuskOperator", "BlueTuskAdministrator"));
    options.AddPolicy("BlueTusk.ControlPlane.Mutate", policy =>
        policy.RequireRole("BlueTuskOperator", "BlueTuskAdministrator"));
});

app.MapBlueTuskDashboard();
```

Every route is assigned the configured authorization policy. The package does not install a permissive fallback policy; a host that omits authorization configuration fails closed at request time.

The dashboard includes the `/pipelines` page and `/api/sync` projection.
Throughput is calculated from successive control-plane observations, so the
first observation deliberately reports no rate. Checkpoint lag is the byte
difference between the durable relay head and the worker's last confirmed
commit LSN for the same source fingerprint.

The `/live` page and `/api/live` projection show shared-query counts, active
clients, fan-out ratio, redacted scopes, invalidation lag, replay usage, resume
rejections, quotas, and slow-client disconnect causes. Both routes use the same
mandatory read policy as the rest of the dashboard.

The optional `BlueTusk.ContinuousGraph.ControlPlane` adapter supplies
`HostedContinuousGraphControlPlaneQueryService`, which projects registered graph query
fingerprints, graph/database identities, explicit element aliases, exact
relational dependencies, result bounds, and capabilities. It does not expose
bound parameters or graph result rows. The authorised `/graphs` and
`/api/graphs` endpoints HTML-encode every application-provided value.
The Control Plane core does not reference the optional ContinuousGraph adapter.

## Versioned agent API

Automation and operational agents should use the explicitly versioned routes:

- `GET /api/capabilities`
- `GET /api/v1/overview`
- `GET /api/v1/sync`
- `GET /api/v1/live`
- `GET /api/v1/graphs`
- `POST /api/v1/operations`

The capability response declares the current, minimum, and complete supported
contract-version set. Every successful v1 response is a
`ControlPlaneApiResponse<T>` containing `contractVersion` and `data`; agents
must reject a version they do not understand instead of interpreting a future
payload as v1. The version is part of the route as well as the envelope so
proxies cannot silently rewrite content negotiation.

The original unversioned `/api/overview`, `/api/sync`, `/api/live`,
`/api/graphs`, and `/api/operations` routes remain compatibility aliases for
the stable 1.x line. New fields may be added compatibly within v1;
removing or changing the meaning or JSON type of an existing v1 field requires
a new API version. The dashboard itself uses the v1 mutation endpoint.

## Mutating operations and audit

`ControlPlaneOperationExecutor` is the only provided command path. Every request has a client-generated operation ID, target, reason, and exact confirmation. `ControlPlaneOperationPolicies` requires an Operator for normal mutations and an Administrator for consumer-group removal, checkpoint rewind, and slot deletion. The required confirmation is the ordinal string `<OperationKind>:<Target>` and must be presented explicitly by the operator.

The executor records denied and confirmation-rejected attempts. An accepted request writes `Requested` before calling the host's `IControlPlaneOperationHandler`, followed by `Succeeded` or `Failed`. If the initial audit append fails, the handler is never invoked. Exception messages are not copied into audit records; failures use a non-sensitive type/detail code. If the handler completes but the success audit fails, the executor reports a reconciliation-required error containing no false `Failed` record; operators reconcile by the stable operation ID.

BlueTusk deliberately does not provide default slot-deletion or checkpoint-rewind handlers. A host must bind commands to its own coordinator and database permissions, keeping those destructive actions out of one-click read-only dashboard paths.

The Sync pipelines page renders retry, reconcile, and rebuild controls only for
principals in the configured Operator or Administrator role. Its mutation API
has a separate authorization policy in addition to the dashboard read policy.
The server derives the actor ID and roles from the authenticated principal;
clients cannot submit an actor identity.

Every operation request is bounded to 16 KiB of JSON, requires a non-empty
reason, the exact `<OperationKind>:<Target>` confirmation, and an
`X-BlueTusk-Operation-Id` header matching its client-generated body ID. The
non-simple header prevents cross-origin HTML form submission; hosts must not
enable credentialed cross-origin access to the dashboard API. The dashboard
script is served as a same-origin external asset so a Content Security Policy
can use `script-src 'self'` without permitting inline script. Handler failures
return only a stable code and operation ID guidance, while the full exception
is available to host logging and never copied to the browser or audit detail.

The endpoint delegates every accepted request to
`ControlPlaneOperationExecutor`, so audit-before-mutation, role escalation, and
destructive-operation confirmation cannot be bypassed by the UI. The host still
owns `IControlPlaneOperationHandler`, including its durable retry,
reconciliation, rebuild, pause/resume, checkpoint, and slot coordinators.

`PostgreSqlControlPlaneAuditStore` creates an append-only `audit_log` plus a database trigger that rejects `UPDATE` and `DELETE`. `InitializeAsync` serializes migrations with a transaction-scoped advisory lock and records `CurrentSchemaVersion` in `storage_metadata`. Version 2 adds an explicit record format, preserving and backfilling rows created by the legacy pre-metadata schema. Initialization is idempotent, rejects future schema versions, and commits a migration atomically. `AppendAsync` writes only when the stored schema is exactly the version supported by the running package, preventing an older process from writing through an incompatible migration. `GetSchemaVersionAsync` exposes the persisted version for readiness checks.

Run `InitializeAsync` with a migration owner before starting operation workers, then give the application identity only schema usage, sequence usage, and insert privileges. Take a database backup before package upgrades. Database owners can still drop database objects, so production audit retention also requires restricted ownership, PostgreSQL backups, and external log export appropriate to the organisation's compliance boundary.

## Managed hosting reconciliation

Managed hosting is a versioned desired-state controller in the Control Plane.
It is deliberately provider-neutral: an AWS, Azure, Google Cloud, Kubernetes,
or private-infrastructure adapter implements `IManagedInfrastructureProvider`
without moving provider SDKs or credentials into the core package.

`ManagedDeploymentSpec` identifies one tenant, provider, and region and
contains bounded workload resources for Streams, Sync, Live, the Control
Plane, the Dashboard, and Continuous Graph. Placement identity is immutable;
updates advance the generation by exactly one through `PutAsync` compare and
swap. Canonical fingerprints exclude the generation itself, sort maps and
workloads, and let a provider distinguish a version bump from an actual desired
change.

Workloads contain `ManagedSecretReference` values only. There is no control
plane type or callback that accepts a password, token, certificate, or secret
value. A provider adapter resolves its references inside its own identity and
audit boundary. Plans and results are bounded non-sensitive metadata and are
validated before status can advance.

```csharp
var store = new PostgreSqlManagedDeploymentStore(controlDataSource);
await store.InitializeAsync();

var quotas = new ManagedDeploymentQuotaSource(
    store,
    new Dictionary<string, ManagedTenantQuota>
    {
        ["tenant-a"] = new(
            MaximumDeployments: 10,
            MaximumReplicas: 100,
            MaximumCpuMillicores: 100_000,
            MaximumMemoryBytes: 512L * 1024 * 1024 * 1024,
            MaximumStorageBytes: 10L * 1024 * 1024 * 1024 * 1024),
    });
var controller = new ManagedDeploymentController(
    store,
    store,
    quotas,
    new ManagedInfrastructureProviderResolver([kubernetesProvider]),
    owner: instanceIdentity);

await store.PutAsync(desired, expectedGeneration: 0);
await controller.ReconcileAsync(desired.DeploymentId);
```

Every reconciliation owns a renewable lease and passes its monotonically
increasing fencing token to plan application or deletion. Desired generation
and observed status revision are independently compared and swapped. A
concurrent specification change, lease loss, over-quota request, mismatched
provider plan, or future stored document format fails closed. Provider failure
messages are not written to status; operators see a stable diagnostic code and
correlate it with protected host logs.

Provider `ApplyAsync` and `DeleteAsync` implementations must be idempotent for
deployment ID, generation, plan fingerprint, and fencing token. They must
reject a token older than the last accepted token even if cancellation arrives
late. Delete protection additionally requires the exact expected desired
generation and an explicit override. See
[ADR 0014](../architecture/decisions/0014-managed-hosting-reconciliation.md)
for the complete failure and security contract.

## Verification status

The unit gate covers role escalation, exact confirmation, handler failure,
non-sensitive audit details, Sync rate/lag/failure projection, authorization
metadata on every dashboard endpoint, Live scope redaction and lag/fan-out
projection, hostile source, pipeline, Live, and graph HTML values, capability
discovery, v1 envelopes, legacy route
preservation, and versioned operation responses. Live PostgreSQL 15–19
acceptance creates a real logical slot, relay
group, snapshot run, and direct checkpoint, verifies their inventory
projections, upgrades a legacy audit table to schema version 2 without losing
rows, initializes a fresh schema idempotently, proves stored audit rows reject
update and delete attempts, and rejects a future schema version.

The managed-hosting gate adds canonical-fingerprint, validation, quota,
generation/revision CAS, lease exclusion, fencing, idempotent convergence,
delete-protection, and non-sensitive failure tests. Its PostgreSQL acceptance
initializes the durable schema idempotently, round-trips secret references,
rejects concurrent updates, advances fencing tokens, and rejects a future
desired-document format.

See the [API and format compatibility policy](api-compatibility.md) and
[1.0.0 release record](release-notes-1.0.0.md) for the exact
candidate gate and remaining publication dependency.
