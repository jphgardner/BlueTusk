# Control Plane

This page helps you decide whether to use the BlueTusk Control Plane and where
to start. The Control Plane is a dashboard and a JSON API that you host inside
your own ASP.NET Core application. It shows the health of your Streams, Sync
and Live components and lets authorized operators act on them, with every
action written to an audit log.

## What it shows

| Area | What you see |
| --- | --- |
| Sources and Streams | Replication slot state and WAL lag, durable relay storage, consumer groups, direct checkpoints and snapshot runs |
| Sync pipelines | Pipeline state, throughput, checkpoint lag, retries, throttling, quarantined transactions and failures |
| Live subscriptions | Shared queries, connected clients, fan-out, invalidation lag, replay and resume activity, quota rejections |
| Continuous Graph (preview) | Registered graph queries and their limits |
| Managed deployments | Placement, desired and observed generation, workloads, requested capacity and delete protection |

The overview page combines all of these and lists the items that need
attention. Every row opens a detail page. The same data is available as
versioned JSON under `/bluetusk/api/v1/...` for scripts and agents.

The inventory is redacted. It never returns connection strings, credentials,
row values, query parameters or dead-letter payloads.

## What it lets you do

Operators can request actions such as retrying, reconciling or rebuilding a
Sync pipeline, and pausing, resuming, reconciling, rebuilding or deleting a
managed deployment. Every request needs the right role, a typed confirmation
and a reason. The Control Plane records the attempt before it calls your code
and records the outcome afterwards.

BlueTusk ships the handler for managed-deployment actions. For every other
action you write the handler, so nothing runs that you did not wire up. See
[Enable operator actions](operations.md).

**New in 1.1.0:** fleet operations on managed deployments, the
`/bluetusk/deployments` pages and `/api/v1/fleet`, and the
`BlueTusk.ControlPlane.Kubernetes` package.

## Use it when

- you run Streams, Sync or Live in production and need one place to see lag,
  failures and backlog;
- you want operator actions to be role-checked and audited instead of run as
  ad hoc SQL;
- you manage BlueTusk deployments for several tenants or through Kubernetes
  custom resources.

Do not use it as a general database administration tool. It only shows
BlueTusk state, and it has no query editor.

## Packages

| Package | Contains |
| --- | --- |
| `BlueTusk.ControlPlane` | Inventory services, the operation executor, the PostgreSQL audit store and the managed-deployment controller |
| `BlueTusk.Dashboard` | `MapBlueTuskDashboard`: the HTML pages and the JSON API |
| `BlueTusk.ControlPlane.Kubernetes` | **New in 1.1.0.** Reconciles `BlueTuskDeployment` custom resources; ships the CRD and RBAC manifests |

```powershell
dotnet add package BlueTusk.Dashboard
```

`BlueTusk.Dashboard` brings in `BlueTusk.ControlPlane`. See
[Install BlueTusk](../getting-started/install.md) for channels and version
pinning.

## The shortest version

```csharp
builder.Services.AddSingleton<IControlPlaneQueryService>(
    new PostgreSqlControlPlaneQueryService(
        [new ControlPlanePostgreSqlSource("local", dataSource, dataSource)]));
// ...register the other inventory services and your authentication...

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapBlueTuskDashboard();
```

The [quick start](quickstart.md) turns this into a complete, runnable app.

> **Warning:** The dashboard is an administrative surface. It reveals the
> shape of your data platform and, for operators, can change it. Always put it
> behind real authentication, HTTPS and a private network or VPN. BlueTusk
> requires an authorization policy on every route and never adds an anonymous
> fallback, but it cannot choose who your operators are. See
> [Security](../security.md).

## Status

The Control Plane is a Core family. It ships on the same version line as the
provider, Streams, Sync and Live. `1.0.0` is the current stable release and
`1.1.0-rc.1` is the current release candidate. `1.1.0` is not published yet.
The Continuous Graph pages depend on the Graph family, which is in preview.

## Next steps

1. [Quick start](quickstart.md): host the dashboard and see your first
   inventory in about 10 minutes.
2. [Concepts](concepts.md): inventory, health, operations, fleet, audit and
   roles.
3. [Enable operator actions](operations.md): turn on audited actions and fleet
   operations.
4. [Kubernetes](kubernetes.md): manage deployments with `BlueTuskDeployment`
   resources.
5. [Configuration](configuration.md): every option, route and default.
6. [Troubleshooting](troubleshooting.md): 401, 403, missing inventory and
   rejected operations.

Engineering detail lives in the [Control Plane reference](reference.md).
