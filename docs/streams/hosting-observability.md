# Hosting and observability

`BlueTusk.Streams.DependencyInjection` runs registered snapshot-then-stream consumers as in-process .NET hosted workers. Multiple workers share the host lifetime but keep independent sources, consumers, checkpoints, and failure state.

```csharp
services.AddSingleton<OrdersConsumer>();
services
    .AddBlueTuskStreams()
    .AddHostedConsumer<OrdersConsumer>(
        "orders",
        provider => provider.GetRequiredService<PostgreSqlConsistentSnapshotSource>(),
        new SnapshotThenStreamOptions { MaximumSnapshotAttempts = 3 });
```

Worker names are unique and become health/diagnostic identities. A source factory returning null, an unregistered consumer, or a worker exception faults that hosted worker and is surfaced through the host rather than being silently retried outside the stream's explicit retry policy.

## Health

`AddBlueTuskStreams` registers the standard `bluetusk_streams` health check with `bluetusk`, `streams`, and `ready` tags. `BlueTuskStreamHealthRegistry` exposes immutable status snapshots for dashboards or custom endpoints. States are starting, snapshotting, catching up, running, stopped, and faulted; status includes the current snapshot epoch, delivered snapshot rows, delivered transactions, transition time, and a redacted operator-facing error message.

The aggregate health check is unhealthy if any worker is faulted, degraded if no worker is active, and healthy otherwise. Applications should still expose liveness separately from this readiness-oriented check.

## Metrics and traces

Core Streams exposes exporter-neutral .NET diagnostics through `BlueTuskStreamsDiagnostics`:

- activity source and meter name: `BlueTusk.Streams`;
- snapshot attempt activities tagged with source fingerprint, slot, epoch, attempt, and row count;
- transaction and change delivery counters;
- snapshot-row counters; and
- transaction-size histograms.

Tags contain stable source/table identities and never connection strings, credentials, row values, or logical-message content. Any OpenTelemetry-compatible .NET setup can subscribe to the activity source and meter; Streams does not force a particular exporter.
