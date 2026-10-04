# Enable operator actions

This guide shows you how to let operators act from the dashboard and the API:
store an audit log, write the handler that performs each action, and turn on
fleet operations for managed deployments. Read [Concepts](concepts.md#operations)
for how operations, roles and audit fit together.

## Before you start

Finish the [quick start](quickstart.md). This guide adds to its `Program.cs`.
Out of the box, the quick start answers every operation request with
`403 Forbidden`, because its user is only a viewer, and it has no executor to
run operations.

## 1. Create the audit store

Add this after `var dataSource = ...`:

```csharp
var audit = new PostgreSqlControlPlaneAuditStore(dataSource);
await audit.InitializeAsync();
```

`InitializeAsync` creates the `bluetusk_control` schema, the append-only
`audit_log` table and a trigger that rejects updates and deletes. It is safe to
run on every start. In production, run it once from a deployment step with a
database owner, and give the app's own login only what it needs:

```sql
GRANT USAGE ON SCHEMA bluetusk_control TO bluetusk_app;
GRANT SELECT ON bluetusk_control.storage_metadata TO bluetusk_app;
GRANT INSERT ON bluetusk_control.audit_log TO bluetusk_app;
```

The trigger stops changes, but a database owner can still drop the table.
Back the database up, and copy the audit log to a separate log system if you
must prove that no records were removed.

## 2. Write an operation handler

The handler is the code that actually does the work. BlueTusk calls it only
after the role check, the confirmation check and the `Requested` audit record.
Add this class at the end of `Program.cs`:

```csharp
// Handles the operations that are not fleet operations.
sealed class PipelineOperations : IControlPlaneOperationHandler
{
    public ValueTask ExecuteAsync(
        ControlPlaneOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        switch (request.Kind)
        {
            case ControlPlaneOperationKind.RetryPipeline:
                // Call your Sync worker's retry here. request.Target is "pipeline:<id>".
                return ValueTask.CompletedTask;
            default:
                throw new NotSupportedException($"Operation '{request.Kind}' is not enabled.");
        }
    }
}
```

Rules for a handler:

- Throw for any kind you do not support. The executor records `Failed` with
  the exception type name, and the API returns `operation-failed`.
- Make each action safe to repeat. A caller that saw a failure may retry the
  same operation ID.
- Do not put secrets or row data in exception messages. They are not audited
  or returned, but they do reach your logs.

## 3. Register the executor

The dashboard's `POST /bluetusk/api/v1/operations` endpoint needs a
`ControlPlaneOperationExecutor` in dependency injection. Add this before
`var app = builder.Build();`:

```csharp
builder.Services.AddSingleton(new ControlPlaneOperationExecutor(
    new RoleControlPlaneAuthorizer(),
    audit,
    new PipelineOperations()));
```

`RoleControlPlaneAuthorizer` allows an operation when the user has the
required role or a higher one (Viewer, then Operator, then Administrator).
Replace it with your own `IControlPlaneAuthorizer` to add rules such as
change windows.

## 4. Give the operator a role

In `/dev/login`, change the role claim from `BlueTuskViewer` to
`BlueTuskOperator`:

```csharp
new Claim(ClaimTypes.Role, "BlueTuskOperator")
```

Restart the app and sign in again.

## 5. Run an operation

In the dashboard, operation buttons appear on Sync pipeline and deployment
pages for operators. To call the API yourself, send the operation ID twice:
in the body and in the `X-BlueTusk-Operation-Id` header. `kind` is a number
(see [operation kinds](configuration.md#operation-kinds)); `4` is
`RetryPipeline`.

```powershell
curl.exe -c cookies.txt http://127.0.0.1:5217/dev/login
$id = [guid]::NewGuid().ToString()
@{ operationId = $id; kind = 4; target = "pipeline:orders";
   confirmation = "RetryPipeline:pipeline:orders"; reason = "Destination recovered" } |
  ConvertTo-Json | Set-Content op.json
curl.exe -b cookies.txt -H "Content-Type: application/json" -H "X-BlueTusk-Operation-Id: $id" `
  --data-binary "@op.json" http://127.0.0.1:5217/bluetusk/api/v1/operations
```

```json
{"contractVersion":1,"data":{"operationId":"6f14c851-e615-4dc3-8316-8ea6c43eb544","status":"succeeded"}}
```

Check the audit log:

```sql
SELECT operation_kind, target, status, actor_id, detail_code
FROM bluetusk_control.audit_log
ORDER BY audit_sequence;
```

```text
 operation_kind |     target      |  status   | actor_id | detail_code
----------------+-----------------+-----------+----------+-------------
 RetryPipeline  | pipeline:orders | Requested | dev-user |
 RetryPipeline  | pipeline:orders | Succeeded | dev-user |
```

A wrong confirmation returns `400` with `confirmation-mismatch` and records
`Rejected`. An operator who asks for `DeleteSlot` gets `403` with
`operation-denied` and the log records `Denied`.

## 6. Enable fleet operations

**New in 1.1.0.** Fleet operations act on managed deployments. BlueTusk ships
the handler; you supply a store, a controller and an infrastructure provider.

Create the store and the controller, and replace the quick start's
`IControlPlaneFleetQueryService` registration:

```csharp
var deployments = new PostgreSqlManagedDeploymentStore(dataSource);
await deployments.InitializeAsync();

var controller = new ManagedDeploymentController(
    store: deployments,
    leases: deployments,
    quotas: new ManagedDeploymentQuotaSource(
        deployments,
        new Dictionary<string, ManagedTenantQuota>
        {
            ["commerce"] = new(
                MaximumDeployments: 10,
                MaximumReplicas: 50,
                MaximumCpuMillicores: 50_000,
                MaximumMemoryBytes: 64L * 1024 * 1024 * 1024,
                MaximumStorageBytes: 1024L * 1024 * 1024 * 1024),
        }),
    providers: new ManagedInfrastructureProviderResolver([new LocalProvider()]),
    owner: Environment.MachineName);

builder.Services.AddSingleton<IControlPlaneFleetQueryService>(
    new ManagedDeploymentFleetQueryService(deployments));
```

Every tenant needs a quota, or reconciliation stops with
`tenant-quota-missing`. The `owner` names this host in reconciliation leases;
use a value that is unique per running instance.

Then route fleet operations to BlueTusk's handler and everything else to
yours. Replace the executor registration from step 3:

```csharp
builder.Services.AddSingleton(new ControlPlaneOperationExecutor(
    new RoleControlPlaneAuthorizer(),
    audit,
    new ManagedDeploymentControlPlaneOperationHandler(
        deployments,
        controller,
        new NoRebuildPreparation(),
        fallback: new PipelineOperations())));
```

Add the provider and the rebuild hook at the end of the file. This provider
creates nothing; a real one creates and deletes your workloads. For Kubernetes,
see [Kubernetes](kubernetes.md).

```csharp
// A provider that "deploys" nothing. Replace it with one that creates real workloads.
sealed class LocalProvider : IManagedInfrastructureProvider
{
    public string Name => "local";

    public ValueTask<ManagedDeploymentPlan> PlanAsync(
        ManagedDeploymentSpec desired,
        ManagedDeploymentStatus current,
        CancellationToken cancellationToken = default)
    {
        var fingerprint = ManagedDeploymentValidation.GetFingerprint(desired);
        return ValueTask.FromResult(new ManagedDeploymentPlan(
            desired.DeploymentId,
            desired.Generation,
            DesiredFingerprint: fingerprint,
            PlanFingerprint: fingerprint,
            RequiresChange: current.AppliedPlanFingerprint != fingerprint,
            Actions: [new ManagedDeploymentAction("apply", desired.DeploymentId, "Apply desired state")]));
    }

    public ValueTask<ManagedProviderResult> ApplyAsync(
        ManagedDeploymentSpec desired,
        ManagedDeploymentPlan plan,
        long fencingToken,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ManagedProviderResult(
            ProviderResourceId: "local/" + desired.DeploymentId,
            AppliedPlanFingerprint: plan.PlanFingerprint));

    public ValueTask DeleteAsync(
        ManagedDeploymentSpec desired,
        long fencingToken,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

sealed class NoRebuildPreparation : IManagedDeploymentRebuildHandler
{
    public ValueTask RebuildAsync(
        ManagedDeployment deployment,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
```

Create one deployment so there is something to operate. Add this before
`var app = builder.Build();`:

```csharp
if (await deployments.GetAsync("orders") is null)
{
    await deployments.PutAsync(
        new ManagedDeploymentSpec(
            DeploymentId: "orders",
            TenantId: "commerce",
            Provider: "local",
            Region: "local",
            Generation: 1,
            Paused: false,
            DeleteProtection: true,
            Workloads:
            [
                new ManagedWorkloadSpec(
                    ManagedWorkloadKind.Streams,
                    Version: "1.1.0",
                    new ManagedResourceRequest(
                        Replicas: 1,
                        CpuMillicoresPerReplica: 500,
                        MemoryBytesPerReplica: 512L * 1024 * 1024,
                        StorageBytes: 0),
                    SecretReferences: [],
                    Settings: new Dictionary<string, string>()),
            ],
            Labels: new Dictionary<string, string>()),
        expectedGeneration: 0);
    await controller.ReconcileAsync("orders");
}
```

Run the app and open `/bluetusk/deployments`. The `orders` deployment is
**Ready** at generation 1 with **Pause**, **Reconcile** and **Rebuild**
buttons. Pause it from the API (`11` is `PauseDeployment`):

```powershell
$id = [guid]::NewGuid().ToString()
@{ operationId = $id; kind = 11; target = "deployment:orders";
   confirmation = "PauseDeployment:deployment:orders"; reason = "Maintenance window" } |
  ConvertTo-Json | Set-Content op.json
curl.exe -b cookies.txt -H "Content-Type: application/json" -H "X-BlueTusk-Operation-Id: $id" `
  --data-binary "@op.json" http://127.0.0.1:5217/bluetusk/api/v1/operations
curl.exe -b cookies.txt http://127.0.0.1:5217/bluetusk/api/v1/fleet
```

The fleet response now shows `"desiredGeneration":2`, `"observedGeneration":2`,
`"paused":true` and `"state":5`. Enum values are numbers in the JSON API; `5`
is `Paused` (see [JSON enum values](configuration.md#json-enum-values)).
Resume it the same way with kind `12` and confirmation
`ResumeDeployment:deployment:orders`.

`DeleteDeployment` needs the Administrator role, and it stops with
`delete-protection-enabled` while `DeleteProtection` is `true`.

## Production checklist

- Use your organization's identity provider. Map groups to the
  `BlueTuskViewer`, `BlueTuskOperator` and `BlueTuskAdministrator` roles (or
  [your own names](configuration.md#dashboard-options)).
- Serve the dashboard over HTTPS on a private network. If a proxy terminates
  TLS, configure forwarded headers and do not expose the app directly.
- Do not enable credentialed CORS for the dashboard. The required
  `X-BlueTusk-Operation-Id` header blocks cross-site form posts only while
  cross-origin requests stay blocked.
- The dashboard script is served from `/bluetusk/assets/dashboard.js`, so a
  Content Security Policy can use `script-src 'self'`.
- Run `InitializeAsync` for the audit and deployment stores from a migration
  step, and grant the app login only the rights it needs.
- Watch `bluetusk.control_plane.operations` and
  `bluetusk.control_plane.operation.duration` (meter `BlueTusk.ControlPlane`).
  They cover managed-deployment reconcile and delete calls.
