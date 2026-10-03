# Control Plane configuration

This page lists every setting you can change in the Control Plane: dashboard
options, authorization policies, routes, inventory sources, storage, managed
deployments and the Kubernetes reconciler. All configuration is in code; the
Control Plane reads no `appsettings.json` section of its own.

## Dashboard options

Pass options to `MapBlueTuskDashboard`:

```csharp
app.MapBlueTuskDashboard(options =>
{
    options.RoutePrefix = "/ops/bluetusk";
    options.BrandLabel = "Production EU";
    options.DataProvenanceNotice = "Live is not connected to this host.";
    options.ReadAuthorizationPolicy = "Ops.Read";
    options.MutationAuthorizationPolicy = "Ops.Mutate";
    options.GraphExecutionAuthorizationPolicy = "Ops.GraphExecute";
    options.ViewerRole = "ops-viewer";
    options.OperatorRole = "ops-operator";
    options.AdministratorRole = "ops-admin";
    options.GraphExecutorRole = "ops-operator";
});
```

`BlueTuskDashboardOptions`:

| Property | Type | Default | Meaning |
| --- | --- | --- | --- |
| `RoutePrefix` | `string` | `/bluetusk` | Base path for every page and API. Must start with `/`, must not end with `/`, and may contain only ASCII letters, digits, `/`, `-`, `_`, `.` and `~` |
| `BrandLabel` | `string` | `Control plane` | Text next to the BlueTusk name in the header. **New in 1.1.0** |
| `DataProvenanceNotice` | `string?` | `null` | Optional banner, for example to say which products this host is not connected to. **New in 1.1.0** |
| `ReadAuthorizationPolicy` | `string` | `BlueTusk.ControlPlane.Read` | Policy required for every route |
| `MutationAuthorizationPolicy` | `string` | `BlueTusk.ControlPlane.Mutate` | Extra policy for `POST .../operations` |
| `GraphExecutionAuthorizationPolicy` | `string` | `BlueTusk.ControlPlane.GraphExecute` | Extra policy for `POST .../graphs/{queryFingerprint}/run`. **New in 1.1.0** |
| `ViewerRole` | `string` | `BlueTuskViewer` | ASP.NET Core role mapped to the Control Plane `Viewer` role |
| `OperatorRole` | `string` | `BlueTuskOperator` | Role mapped to `Operator`. Shows operation buttons |
| `AdministratorRole` | `string` | `BlueTuskAdministrator` | Role mapped to `Administrator`. Shows the deployment **Delete** button |
| `GraphExecutorRole` | `string` | `BlueTuskOperator` | Role that sees the graph **Run** controls. **New in 1.1.0** |

Every property except `DataProvenanceNotice` must be non-empty. An invalid
value throws `ArgumentException` when `MapBlueTuskDashboard` runs.

## Authorization policies

You must register a policy for each of the three policy names. BlueTusk does
not create them, and a missing policy fails the request with
`The AuthorizationPolicy named: '...' was not found.`

| Policy | Applies to | Suggested requirement |
| --- | --- | --- |
| Read | Every page and API, including the POST endpoints | Any of the viewer, operator or administrator roles |
| Mutation | `POST /api/v1/operations` and `/api/operations` | Operator or administrator |
| Graph execution | `POST /api/v1/graphs/{queryFingerprint}/run` and the unversioned alias | Operator or administrator |

A POST request must pass the read policy and its own policy. The
[quick start](quickstart.md#5-write-the-code) shows a working set. Role checks
for individual operations happen after the policy check; see
[Authorization](concepts.md#authorization).

## Routes

All paths are relative to `RoutePrefix`.

### Pages

| Path | Shows |
| --- | --- |
| `/` | Redirects to `/overview` |
| `/overview` | All products and the items that need attention |
| `/sources`, `/sources/{sourceKey}` | Streams sources, slots and relay storage |
| `/sources/{sourceKey}/consumer-groups/{groupName}` | One consumer group |
| `/sources/{sourceKey}/snapshots/{snapshotEpoch}` | One snapshot run |
| `/sources/{sourceKey}/checkpoints/{consumerGroup}` | One direct checkpoint |
| `/snapshots`, `/consumer-groups`, `/checkpoints` | Fleet-wide lists |
| `/pipelines`, `/pipelines/{pipelineId}` | Sync pipelines |
| `/live`, `/live/{subscriptionFingerprint}` | Live subscriptions |
| `/graphs`, `/graphs/{queryFingerprint}` | Continuous Graph queries (preview) |
| `/deployments`, `/deployments/{deploymentId}` | Managed deployments. **New in 1.1.0** |
| `/assets/dashboard.js` | The dashboard's script (same origin) |

### JSON API

| Method and path | Service used | Response `data` |
| --- | --- | --- |
| `GET /api/capabilities` | None | `currentVersion`, `minimumSupportedVersion`, `supportedVersions` (not wrapped) |
| `GET /api/v1/overview` | `IControlPlaneQueryService` | `ControlPlaneOverview` |
| `GET /api/v1/sync` | `IControlPlaneSyncQueryService` | `ControlPlaneSyncOverview` |
| `GET /api/v1/live` | `IControlPlaneLiveQueryService` | `ControlPlaneLiveOverview` |
| `GET /api/v1/graphs` | `IControlPlaneContinuousGraphQueryService` | `ControlPlaneContinuousGraphOverview` |
| `GET /api/v1/fleet` | `IControlPlaneFleetQueryService` | `ControlPlaneFleetOverview`. **New in 1.1.0** |
| `POST /api/v1/operations` | `ControlPlaneOperationExecutor` | `operationId`, `status` |
| `POST /api/v1/graphs/{queryFingerprint}/run` | `IControlPlaneContinuousGraphExecutionService` | `ControlPlaneContinuousGraphRunResult` |

Every `/api/v1/...` response is `{"contractVersion":1,"data":...}`. Reject a
`contractVersion` you do not recognize. New fields can appear within version
1; a removed or changed field means a new version. The unversioned
`/api/overview`, `/api/sync`, `/api/live`, `/api/graphs`, `/api/fleet`,
`/api/operations` and `/api/graphs/{queryFingerprint}/run` routes return the
same data without the envelope and remain for 1.x compatibility. See
[API compatibility](api-compatibility.md).

### Operation requests

| Rule | Value |
| --- | --- |
| Content type | `application/json` |
| Body size | 1 byte to 16 KiB |
| Header | `X-BlueTusk-Operation-Id`, equal to the body's `operationId` (GUID, `D` format) |
| Body fields | `operationId`, `kind` (number), `target`, `confirmation`, `reason` |
| Confirmation | Exactly `<Kind>:<Target>` |
| Length limits | `target` 1024, `confirmation` 2048, `reason` 2048, actor ID 512 characters |

A graph run request has the same size limit and at most 64 parameters.

### Operation kinds

`kind` is sent as a number:

| Value | Kind | Value | Kind |
| --- | --- | --- | --- |
| 0 | `PauseSource` | 8 | `RewindCheckpoint` |
| 1 | `ResumeSource` | 9 | `DeleteSlot` |
| 2 | `PauseConsumerGroup` | 10 | `ReplayQuarantine` |
| 3 | `ResumeConsumerGroup` | 11 | `PauseDeployment` |
| 4 | `RetryPipeline` | 12 | `ResumeDeployment` |
| 5 | `ReconcilePipeline` | 13 | `ReconcileDeployment` |
| 6 | `RebuildPipeline` | 14 | `RebuildDeployment` |
| 7 | `RemoveConsumerGroup` | 15 | `DeleteDeployment` |

Values 11 to 15 are **New in 1.1.0**.

### JSON enum values

Enums in API responses are also numbers:

| Enum | Values |
| --- | --- |
| `ManagedDeploymentState` (`state`) | 0 `Pending`, 1 `Planning`, 2 `Applying`, 3 `Ready`, 4 `Degraded`, 5 `Paused`, 6 `Deleting`, 7 `Deleted`, 8 `Failed` |
| `ManagedWorkloadKind` (`workloadKinds`) | 0 `Streams`, 1 `Sync`, 2 `Live`, 3 `ControlPlane`, 4 `Dashboard`, 5 `ContinuousGraph` |

Sync pipeline `state` is a string, such as `Running`.

## Inventory sources

`ControlPlanePostgreSqlSource` describes one instance:

| Parameter | Default | Meaning |
| --- | --- | --- |
| `instanceName` | Required | Unique name. Becomes the first part of each source key |
| `sourceDataSource` | Required | The PostgreSQL server you capture from. Used to read `pg_replication_slots` |
| `controlDataSource` | Required | The database that holds the relay tables |
| `controlSchema` | `bluetusk_streams` | Relay schema. One unquoted identifier |

`PostgreSqlControlPlaneQueryOptions`, passed to
`PostgreSqlControlPlaneQueryService.Create`:

| Property | Default | Meaning |
| --- | --- | --- |
| `MaximumParallelInstances` | Processor count, clamped to 2 to 8 | Instances read at the same time |
| `SnapshotCacheDuration` | 250 ms | How long one inventory read is reused. `TimeSpan.Zero` disables the cache |

```csharp
var inventory = PostgreSqlControlPlaneQueryService.Create(
    [
        new ControlPlanePostgreSqlSource(
            instanceName: "production-eu",
            sourceDataSource: sourceDataSource,
            controlDataSource: controlDataSource,
            controlSchema: "bluetusk_streams"),
    ],
    new PostgreSqlControlPlaneQueryOptions
    {
        MaximumParallelInstances = 4,
        SnapshotCacheDuration = TimeSpan.FromSeconds(1),
    });
builder.Services.AddSingleton<IControlPlaneQueryService>(inventory);
```

Use a login with read-only access for both data sources. Instance names must
be unique, and you need at least one instance.

### Sync and Live

`HostedSyncControlPlaneQueryService` needs `IBlueTuskSyncStatusSource`, which
`AddBlueTuskSync()` registers, and an `IControlPlaneQueryService`:

```csharp
builder.Services.AddBlueTuskSync();
builder.Services.AddSingleton<IControlPlaneSyncQueryService, HostedSyncControlPlaneQueryService>();
```

`HostedLiveControlPlaneQueryService` takes the `LiveSharedSubscriptionRegistry`
and `ILiveInvalidationLog` your Live host uses, plus an optional
`IControlPlaneLiveScopeRedactor` (default
`FingerprintControlPlaneLiveScopeRedactor`).

## Storage

| Class | Constructor | Tables | Schema version |
| --- | --- | --- | --- |
| `PostgreSqlControlPlaneAuditStore` | `(DbDataSource dataSource, string controlSchema = "bluetusk_control")` | `storage_metadata`, `audit_log` | 2 |
| `PostgreSqlManagedDeploymentStore` | `(DbDataSource dataSource, string controlSchema = "bluetusk_control")` | `managed_hosting_metadata`, `managed_deployments`, `managed_deployment_leases` | 1 |

```csharp
var audit = new PostgreSqlControlPlaneAuditStore(controlDataSource, controlSchema: "bluetusk_control");
var deployments = new PostgreSqlManagedDeploymentStore(controlDataSource, controlSchema: "bluetusk_control");
```

Call `InitializeAsync` on each before use. `GetSchemaVersionAsync` returns the
installed version for readiness checks. Schema names must match
`^[A-Za-z_][A-Za-z0-9_$]*$`.

## Managed deployments

`ManagedDeploymentController(store, leases, quotas, providers, owner,
leaseDuration, timeProvider)`:

| Parameter | Default | Meaning |
| --- | --- | --- |
| `store` | Required | `IManagedDeploymentStore` |
| `leases` | Required | `IManagedDeploymentLeaseStore`. Both PostgreSQL and in-memory stores implement it |
| `quotas` | Required | `IManagedTenantQuotaSource`, usually `ManagedDeploymentQuotaSource` |
| `providers` | Required | `ManagedInfrastructureProviderResolver` with at least one provider, unique by `Name` |
| `owner` | Required | Lease owner for this host, up to 512 characters. Make it unique per instance |
| `leaseDuration` | 2 minutes | Between 15 seconds and 1 hour |

`ManagedDeploymentQuotaSource(store, tenantQuotas, defaultQuota = null)` uses
the tenant's entry, then `defaultQuota`. A tenant with neither fails with
`tenant-quota-missing`.

Desired state limits (`ManagedDeploymentValidation`):

| Item | Limit |
| --- | --- |
| Deployment, tenant, provider and region IDs | 1 to 128 printable characters |
| Workloads per deployment | 1 to 32, one per kind |
| Replicas per workload | 1 to 256 |
| CPU per replica | 10 to 1,000,000 millicores |
| Memory per replica | 16 MiB to 16 TiB |
| Storage per workload | 0 to 16 TiB |
| Secret references per workload | 128 |
| Settings per workload, labels per deployment | 256, 128; values up to 4096 characters |
| Workload version | Starts with a numeric version, such as `1.1.0` |

## Kubernetes reconciler

**New in 1.1.0.** `KubernetesManagedDeploymentOperator(store, controller,
client, maximumConcurrency, pageSize, timeProvider)`:

| Parameter | Default | Range | Meaning |
| --- | --- | --- | --- |
| `maximumConcurrency` | 4 | 1 to 64 | Resources reconciled at the same time |
| `pageSize` | 100 | 1 to 500 | Resources per Kubernetes list request |

`KubernetesApiManagedDeploymentClient(httpClient, resourceNamespace = null)`
needs an `HttpClient` with an absolute `BaseAddress`, authentication and TLS
trust. With `resourceNamespace` set, it lists only that namespace; otherwise it
lists all namespaces.

| Constant | Value |
| --- | --- |
| `KubernetesApiManagedDeploymentClient.ApiGroup` | `controlplane.bluetusk.io` |
| `KubernetesApiManagedDeploymentClient.ApiVersion` | `v1alpha1` |
| `KubernetesApiManagedDeploymentClient.Plural` | `bluetuskdeployments` |
| `KubernetesManagedDeploymentOperator.Finalizer` | `controlplane.bluetusk.io/finalizer` |

The custom resource fields are in [Kubernetes](kubernetes.md#4-write-a-bluetuskdeployment).

## Metrics

Meter and activity source: `BlueTusk.ControlPlane`. These cover managed
deployment reconcile and delete calls.

| Instrument | Type | Tags |
| --- | --- | --- |
| `bluetusk.control_plane.operations.active` | Up-down counter | `bluetusk.control_plane.operation` |
| `bluetusk.control_plane.operations` | Counter | `bluetusk.control_plane.operation`, `bluetusk.control_plane.outcome` |
| `bluetusk.control_plane.operation.duration` | Histogram (s) | Same as above |

`operation` is `reconcile` or `delete`. `outcome` is one of `changed`,
`no_change`, `paused`, `deleted`, `failed`, `canceled`, `lease_lost`,
`lease_unavailable` or `abandoned`.

For everything else, see the [full Control Plane reference](reference.md).
