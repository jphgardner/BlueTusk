# Control Plane troubleshooting

This page helps you fix common Control Plane problems: sign-in errors,
startup failures, missing or stale inventory, rejected operations and
Kubernetes reconciler issues.

## Sign-in and permissions

| Symptom | Cause | Fix |
| --- | --- | --- |
| Every route returns `401` | The request has no authenticated user | Sign in first. Check that `app.UseAuthentication()` and `app.UseAuthorization()` run before `MapBlueTuskDashboard()`. API clients must send your app's credentials (cookie or token) |
| Unauthenticated requests redirect to `/Account/Login` | Cookie authentication's default behavior | Add a login page, or return `401` as the [quick start](quickstart.md#5-write-the-code) does |
| Pages return `403` | The user fails the read policy | Give the user a role the read policy accepts. If your identity provider sends roles in a claim such as `roles`, set the authentication handler's role claim type so `IsInRole` sees them |
| `500` with `The AuthorizationPolicy named: 'BlueTusk.ControlPlane.Read' was not found.` | The policy is not registered | Register all three policies, or set the option names to policies you have. See [Authorization policies](configuration.md#authorization-policies) |
| Startup fails with `Unable to find the required services. Please add all the required services by calling 'IServiceCollection.AddAuthorization'` | No authorization services | Call `AddAuthorization()` or `AddAuthorizationBuilder()` |
| No operation buttons appear | The user is not in `OperatorRole` or `AdministratorRole` | Check the role names in [dashboard options](configuration.md#dashboard-options) match your identity's roles |
| `POST .../operations` returns `403` with an empty body | The user fails the mutation policy, or has neither a `NameIdentifier` claim nor a name | Grant an operator role; make sure the identity has a user ID |
| `403` with `"code":"operation-denied"` | The user passed the policy but lacks the Control Plane role for this kind. `RemoveConsumerGroup`, `RewindCheckpoint`, `DeleteSlot` and `DeleteDeployment` need Administrator | Use an administrator, or a different operation. The audit log records `Denied` |

## The app fails at startup

**`InvalidOperationException: Body was inferred but the method does not allow
inferred body parameters`, listing a parameter named `queries`, `sync`,
`live`, `graphs` or `fleet`.** One of the five inventory services is not
registered. The dashboard needs `IControlPlaneQueryService`,
`IControlPlaneSyncQueryService`, `IControlPlaneLiveQueryService`,
`IControlPlaneContinuousGraphQueryService` and `IControlPlaneFleetQueryService`.
Register an empty implementation for products you do not run, as the
[quick start](quickstart.md#5-write-the-code) does.

> **Note:** `IControlPlaneFleetQueryService` is **new in 1.1.0**. A host that
> worked with `1.0.0` or `1.1.0-rc.1` must register it after upgrading, for
> example `new ManagedDeploymentFleetQueryService(new InMemoryManagedDeploymentStore())`.

**`ArgumentException: The dashboard route prefix must be an absolute path
without a trailing slash, query, or fragment.`** Fix `RoutePrefix`, for
example `/ops` instead of `ops/`.

## Components are missing from the inventory

| Symptom | Cause | Fix |
| --- | --- | --- |
| No sources | No relay worker has registered a source in the configured control schema | Start the Streams relay worker. Check `controlDataSource` and `controlSchema` point to the relay's database and schema |
| `500` with `Control-plane inventory requires relay schema version 2; found 0.` (`ControlPlaneStorageVersionException`) | The relay schema is missing its version row, or was created by a different Streams version | Run `PostgreSqlDurableChangeRelay.InitializeAsync` with the same BlueTusk version as the dashboard |
| `500` with a PostgreSQL "relation does not exist" error | The relay tables do not exist in that schema | As above |
| Source shows `slot-missing` | The registered slot does not exist on `sourceDataSource` | Check `sourceDataSource` points to the server that owns the slot, or recreate the slot |
| Source shows `source-unavailable` | The source server could not be reached | Check network, credentials and TLS for `sourceDataSource` |
| Sync pipelines or Live subscriptions are empty | `HostedSyncControlPlaneQueryService` and `HostedLiveControlPlaneQueryService` read in-process state | Host the dashboard in the same process as the workers, or implement the query interface to fetch status from them |
| Sync lag shows `source-head-unavailable` | The pipeline's source is not in the relay inventory | Add the relay instance that feeds the pipeline as a `ControlPlanePostgreSqlSource` |
| Deployments are empty | The fleet service reads a different store or schema, or an in-memory store in another process | Point `ManagedDeploymentFleetQueryService` at the same `PostgreSqlManagedDeploymentStore` the controller uses |

## Health looks stale or wrong

- **Nothing changes.** Pages do not refresh by themselves. Use **Refresh**;
  `observedAt` shows when data was read.
- **Several requests show the same values.** The PostgreSQL inventory is
  cached for `SnapshotCacheDuration` (250 ms by default).
- **Sync throughput is blank.** It needs two observations. Refresh again.
- **"Replication slot is not active".** Nothing is reading the slot. Start
  the relay worker. An inactive slot keeps WAL, so its WAL lag keeps growing.
- **Sync lag shows `checkpoint-ahead-of-source`**, or Live shows
  `invalidation-cursor-regressed`. The consumer's position is ahead of the
  recorded head, usually after a restore or a mismatched source. Check source
  identity before resuming.

## Operations are rejected

| Response | Cause | Fix |
| --- | --- | --- |
| `400` `invalid-operation-body` | Body is not JSON, is empty, is over 16 KiB, or `kind` is a string | Send `application/json` with a numeric `kind` ([values](configuration.md#operation-kinds)) |
| `400` `operation-id-header-mismatch` | `X-BlueTusk-Operation-Id` is missing or differs from `operationId` | Send the same GUID in both |
| `400` `confirmation-mismatch` | `confirmation` is not exactly `<Kind>:<Target>` | Type it exactly. Audited as `Rejected` |
| `400` `invalid-operation-request` | Empty or too-long `target` or `reason`, an empty GUID, or a deployment target not in the form `deployment:<deployment-id>` | Fix the request |
| `500` `operation-failed` | The handler threw, or the audit log could not be written | Find the operation ID in the audit log. `detail_code` holds the exception type. Your app log has the full error |
| `500` with `Each parameter in the deserialization constructor on type 'BlueTusk.ControlPlane.ControlPlaneOperationExecutor' must bind...` | No executor registered | Register `ControlPlaneOperationExecutor`. See [Enable operator actions](operations.md#3-register-the-executor) |
| Dashboard alert "Operation rejected. Reference ..." | Any of the above | Look up the reference in the audit log |

Common `operation-failed` causes:

- `NotSupportedException`: `Operation '...' is not a managed-deployment
  operation.` You did not pass a `fallback` handler, or your handler does not
  support that kind.
- `ManagedDeploymentValidationException`: often delete protection. Turn it
  off in the desired state first.
- `ManagedDeploymentLeaseException`: `Deployment '...' is being reconciled by
  another owner.` Retry later.
- `InvalidOperationException`: `Control-plane audit writes require schema
  version 2.` Run `PostgreSqlControlPlaneAuditStore.InitializeAsync` with the
  current package. Nothing ran, because the `Requested` record failed first.
- `The operation completed but its success audit could not be stored;
  reconcile using the operation ID.` The action ran. Do not repeat it blindly.

`BlueTusk control-plane audit rows are immutable` means something tried to
change an audit row. That is intended.

## Kubernetes reconciler

The reconciler handles each resource on its own. A resource it cannot
reconcile gets `state: Failed` and a code in `status.diagnosticCode`, and
every other resource in the pass is still reconciled. The same code is in the
`DiagnosticCode` of that resource's `ReconcileAllAsync` result.

| Code | Cause | Fix |
| --- | --- | --- |
| `workload-version-invalid` | A `workloads[].version` does not start with a numeric version, for example `latest`. The CRD does not check this | Use a version such as `1.1.0` |
| `identifier-invalid` | `spec.tenantId`, `spec.provider` or `spec.region` is only spaces or contains control characters | Fix the value |
| `workload-kind-invalid` | A `workloads[].kind` is not one this package knows. The packaged CRD rejects unknown kinds, so the installed CRD is out of date or edited | Apply the CRD from the package, then fix `kind` |
| Other `...-invalid` or `...-duplicate` codes, for example `replica-count-invalid` or `workload-kind-duplicate` | The resource breaks a [desired state limit](configuration.md#managed-deployments) that the CRD does not check | Fix the field the code names |
| `resource-invalid` | The resource's metadata cannot be used: `<namespace>/<name>` is longer than 128 characters, or a namespace, name, UID, resource version or finalizer list is too long or contains control characters. The status may not be written; the code is always in the `ReconcileAllAsync` result | Use a shorter name, or fix the metadata |
| `reconcile-failed` | Something else failed for this resource only: your provider threw (the stored deployment, and the dashboard, show `provider-failure`), a Kubernetes call for this resource failed, or a database conflict occurred while several new resources were stored at once. The exception is not logged or returned | The next pass retries. Log inside your provider to see its exceptions |
| `tenant-quota-missing` | No quota for `spec.tenantId` and no default | Add the tenant to `ManagedDeploymentQuotaSource` |
| `quota-deployments-exceeded`, `quota-replicas-exceeded`, `quota-cpu-exceeded`, `quota-memory-exceeded`, `quota-storage-exceeded` | The tenant's quota would be exceeded | Raise the quota or reduce the request |
| `provider-not-registered` | No provider's `Name` matches `spec.provider` | Register one, or fix `spec.provider` |
| `provider-plan-mismatch`, `provider-plan-unbounded`, `provider-result-invalid` | Your provider returned a plan or result that does not match the desired state, or exceeds the limits | Fix the provider |
| `delete-protection-enabled` | The resource is being deleted while protection is on | See [delete a protected deployment](kubernetes.md#6-change-pause-and-delete) |
| `concurrent-update`, `lease-unavailable` | Another process changed or is reconciling the deployment | Usually clears on the next pass. Run one reconciler replica |
| `deployment-not-found` | The stored record disappeared during the pass | Check nothing else deletes from `bluetusk_control` |

Other symptoms:

- **`ReconcileAllAsync` throws.** Listing the resources failed, so nothing
  was reconciled in that pass. `HttpRequestException` with `403 (Forbidden)`
  means RBAC is missing; `404 (Not Found)` means the CRD is not installed.
  Apply both manifests. Other errors, such as a network failure, clear on a
  later pass. Keep the `try`/`catch` around `ReconcileAllAsync`.
- **No status at all, and every result is `reconcile-failed`.** The host can
  list resources but cannot patch them or their `status`. Apply the packaged
  RBAC manifest.
- **A `Failed` status appears only from the second pass.** On the first pass
  the reconciler adds its finalizer, which changes the resource version. A
  failure later in that pass can then not always be written to the status.
  The next pass writes it.
- **A pause made in the dashboard is undone.** The resource is the source of
  truth. Set `spec.paused` instead.
- **The resource stays `Terminating`.** Delete protection is on. See
  [Kubernetes](kubernetes.md#6-change-pause-and-delete).

See also the [full Control Plane reference](reference.md).
