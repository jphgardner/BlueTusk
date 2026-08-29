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

Open `http://127.0.0.1:5217/bluetusk/sources`. The `/preview` endpoint reports
the non-production data mode, and both health endpoints are unauthenticated for
container probes. Public search indexing and response caching are disabled.
