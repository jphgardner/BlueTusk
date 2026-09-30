# Operate BlueTusk from the dashboard

BlueTusk Control Plane reads operational state from Provider, Streams, Sync,
Live, and Continuous Graph. `BlueTusk.Dashboard` presents that state as
server-rendered pages and versioned JSON APIs.

Use it when operators need to answer:

- Is the source reachable and is its replication slot healthy?
- How far behind is each consumer or Sync destination?
- Are snapshots, retries, quarantines, or rebuilds active?
- Which Live subscriptions or graph queries are under pressure?
- Which safe operator actions were requested, authorized, and audited?

The dashboard never needs row values, query parameters, connection strings, or
dead-letter payloads.

## Run the sample first

The repository contains an executable dashboard host:

```powershell
dotnet run --project samples/BlueTusk.Samples.Dashboard
```

Open the URL printed by ASP.NET Core, then start at `/bluetusk/overview`. The
sample makes unavailable integrations explicit; it does not invent healthy
telemetry for products that are not connected.

## 1. Register inventory sources

Use separate data sources for the PostgreSQL source and the relay/control
schema in production:

```csharp
var inventory = new PostgreSqlControlPlaneQueryService(
    [new ControlPlanePostgreSqlSource(
        "production-eu",
        sourceDataSource,
        controlDataSource,
        "bluetusk_streams")]);

builder.Services.AddSingleton<IControlPlaneQueryService>(inventory);
builder.Services.AddSingleton<IControlPlaneSyncQueryService,
    HostedSyncControlPlaneQueryService>();
builder.Services.AddSingleton<IControlPlaneLiveQueryService,
    HostedLiveControlPlaneQueryService>();
```

Register only the projections used by the deployment. Missing optional product
services render as unavailable rather than exposing fabricated data.

## 2. Require real authorization

```csharp
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("BlueTusk.ControlPlane.Read", policy =>
        policy.RequireRole("BlueTuskViewer", "BlueTuskOperator", "BlueTuskAdministrator"));
    options.AddPolicy("BlueTusk.ControlPlane.Mutate", policy =>
        policy.RequireRole("BlueTuskOperator", "BlueTuskAdministrator"));
    options.AddPolicy("BlueTusk.ControlPlane.GraphExecute", policy =>
        policy.RequireRole("BlueTuskOperator", "BlueTuskAdministrator"));
});
```

Configure the host's authentication before mapping the dashboard. The package
does not add a permissive fallback identity.

## 3. Map the dashboard

```csharp
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapBlueTuskDashboard(options =>
{
    options.ReadAuthorizationPolicy = "BlueTusk.ControlPlane.Read";
    options.MutationAuthorizationPolicy = "BlueTusk.ControlPlane.Mutate";
    options.GraphExecutionAuthorizationPolicy = "BlueTusk.ControlPlane.GraphExecute";
});
```

Put the dashboard behind HTTPS. If a reverse proxy terminates TLS, configure
trusted forwarded headers and ensure the application is not directly exposed.

## What operators can inspect

The overview links to drill-down pages for sources, replication slots, relay
storage, consumer groups, direct checkpoints, snapshots, Sync pipelines, Live
subscriptions, graph queries, and managed deployments. Each page displays the
complete redacted projection available for that resource.

Graph execution is separately authorized. Only server-registered fingerprints
can run, with bounded time, nodes, edges, and concurrency.

## Production checklist

- Use a read-only database role for inventory queries.
- Keep read, mutation, and graph-execution policies separate.
- Back every mutation with an immutable audit store.
- Keep dangerous actions multi-step; slot deletion and checkpoint rewind are
  not one-click defaults.
- Set inventory timeouts, bounded cross-instance concurrency, and cache lifetime.
- Export dashboard/API latency and operation outcome metrics.
- Treat `source-unavailable`, `slot-missing`, and checkpoint inconsistency as
  different incidents.

The [full Control Plane reference](reference.md) covers every projection,
managed deployments, agents, Kubernetes resources, reconciliation, audit
records, and verification status.
