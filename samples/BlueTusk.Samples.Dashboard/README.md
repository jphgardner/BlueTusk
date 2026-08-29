# BlueTusk Dashboard preview host

This sample hosts the real `BlueTusk.Dashboard` endpoint renderer with
representative, redacted 1.2 control-plane data. It exists so the dashboard UI
can be reviewed without exposing a production database or Kubernetes control
plane.

The preview has two independent mutation barriers:

- its authenticated preview identity has only the viewer role; and
- the `ControlPlaneOperationExecutor` uses an authorizer that denies every
  operation even if an endpoint policy is changed accidentally.

Run it locally:

```powershell
dotnet run --project samples/BlueTusk.Samples.Dashboard `
  --urls http://127.0.0.1:5217
```

Open `http://127.0.0.1:5217/bluetusk/overview`. The preview contains several
representative sources, pipelines, Live subscriptions, Continuous Graph queries,
and deployments in healthy, catching-up, and degraded states. Every inventory
row is linked to its complete redacted detail view, including nested consumer
groups, snapshots, and checkpoints. Search and health filters make the larger
inventories easy to inspect, and the layout adapts down to phone widths.

The `/preview` endpoint reports the non-production data mode, and both health
endpoints are unauthenticated for container probes. Public search indexing and
response caching are disabled. The preview identity remains viewer-only and its
operation authorizer denies every mutation independently of the UI.
The public ingress enforces HSTS at the TLS boundary. The host also emits HSTS
when it observes a validated HTTPS request, alongside its same-origin Content
Security Policy and anti-framing, MIME-sniffing, referrer, permissions, cache,
and indexing protections.
