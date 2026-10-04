# Hosting and observability

This guide shows you how to run Streams consumers inside a .NET host, expose
their health, and collect their metrics and traces.

There are two hosted shapes:

| Shape | Registration | On restart |
| --- | --- | --- |
| Snapshot-then-stream consumer | `AddBlueTuskStreams().AddHostedConsumer<T>()` | Copies the tables again (with `ExistingSlotMode.RestartSnapshot`). |
| Worker that resumes from a checkpoint | `AddHostedService<T>()` with your own `BackgroundService` | Continues after the last checkpoint. See the [quick start](quickstart.md). |

## Run a snapshot consumer as a hosted service

Install `BlueTusk.Streams.DependencyInjection`, then register your consumer and
a source factory:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(BlueTuskDataSource.Create(connectionString));
builder.Services.AddSingleton<ReadModelConsumer>();
builder.Services
    .AddBlueTuskStreams()
    .AddHostedConsumer<ReadModelConsumer>(
        "orders-read-model",
        services => new PostgreSqlConsistentSnapshotSource(
            services.GetRequiredService<BlueTuskDataSource>(),
            snapshotOptions),
        new SnapshotThenStreamOptions { MaximumSnapshotAttempts = 3 });
```

- `ReadModelConsumer` implements `IChangeStreamConsumer`. Register it in the
  container yourself; the hosted service resolves it by type.
- `snapshotOptions` is a `PostgreSqlConsistentSnapshotOptions`, built as in
  [snapshot and catch-up](snapshot-bootstrap.md).
- The name (`orders-read-model`) must be unique. It identifies the worker in
  health data. Registering the same name twice throws
  `InvalidOperationException`.
- You can register several consumers. Each runs independently with its own
  source and slot.

If a consumer throws, its worker is marked `Faulted` and the exception reaches
the host. By default .NET then stops the host
(`BackgroundServiceExceptionBehavior.StopHost`), so your orchestrator can
restart it.

## Confirm positions to PostgreSQL

You do not need an observer for the slot to release WAL. When your consumer
acknowledges a transaction, the snapshot source's stream confirms its position
to PostgreSQL, so the slot's `confirmed_flush_lsn` moves forward while the
worker runs. See [how the slot releases WAL](concepts.md#how-the-slot-releases-wal).

This is enough for a consumer that rebuilds from a snapshot on every start.
Nothing records a position across restarts, though. To resume instead, pass an
`observerFactory` that returns a checkpointing observer, as shown in
[snapshot and catch-up](snapshot-bootstrap.md#3-copy-then-stream-then-resume).

## Check health

`AddBlueTuskStreams()` registers a health check named `bluetusk_streams` with
the tags `bluetusk`, `streams` and `ready`. Map it as a readiness endpoint:

```csharp
var app = builder.Build();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
});
app.MapGet("/streams", (BlueTuskStreamHealthRegistry registry) => registry.GetStatuses());

app.Run();
```

The health check reports:

| Result | When |
| --- | --- |
| `Unhealthy` | Any worker is `Faulted`. |
| `Degraded` | No worker is registered, or every worker is `Starting` or `Stopped`. |
| `Healthy` | Otherwise. |

`BlueTuskStreamHealthRegistry.GetStatuses()` returns one
`BlueTuskStreamWorkerStatus` per worker with `Name`, `State`, `ChangedAt`,
`SnapshotEpoch`, `SnapshotRows`, `Transactions` and `Error` (the exception
message only). `State` moves through:

```text
Starting → Snapshotting → CatchingUp → Running → Stopped
                                (any) → Faulted
```

`CatchingUp` means the copy is complete and no transaction has been delivered
yet. The `/streams` endpoint above returns, for example:

```json
[{"name":"orders-read-model","state":3,"changedAt":"2026-10-03T09:54:05.9303762+00:00","snapshotEpoch":"0f80575c-e8d4-4860-8c6c-7f64c34887e2","snapshotRows":9,"transactions":1,"error":null}]
```

Expose a separate liveness endpoint; this check is about readiness.

## Collect metrics and traces

Streams publishes .NET `Meter` and `ActivitySource` data named
`BlueTusk.Streams` (`BlueTuskStreamsDiagnostics.InstrumentationName`). With
OpenTelemetry (`OpenTelemetry.Extensions.Hosting` plus the exporter you use):

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(BlueTuskStreamsDiagnostics.InstrumentationName))
    .WithTracing(tracing => tracing.AddSource(BlueTuskStreamsDiagnostics.InstrumentationName));
```

| Instrument | Type | Unit | Tags |
| --- | --- | --- | --- |
| `bluetusk.streams.transactions.delivered` | Counter | `{transaction}` | `bluetusk.source`, `bluetusk.slot` |
| `bluetusk.streams.changes.delivered` | Counter | `{change}` | `bluetusk.source`, `bluetusk.slot` |
| `bluetusk.streams.transaction.bytes` | Histogram | `By` | `bluetusk.source`, `bluetusk.slot` |
| `bluetusk.streams.transactions.spooled` | Counter | `{transaction}` | `bluetusk.source`, `bluetusk.slot` |
| `bluetusk.streams.snapshot.rows` | Counter | `{row}` | `bluetusk.source`, `bluetusk.table` |
| `bluetusk.streams.deliveries.active` | UpDownCounter | `{delivery}` | `bluetusk.source`, `bluetusk.streams.spooled` |
| `bluetusk.streams.deliveries.settled` | Counter | `{delivery}` | as above, plus `bluetusk.streams.delivery.outcome` (`acknowledged`, `nacked`, `disposed`) |
| `bluetusk.streams.delivery.duration` | Histogram | `s` | as `deliveries.settled` |
| `bluetusk.streams.delivery.settlement.failures` | Counter | `{failure}` | `bluetusk.source`, `bluetusk.streams.spooled`, `bluetusk.streams.delivery.operation` |
| `bluetusk.streams.spool.operation.duration` | Histogram | `s` | `bluetusk.streams.spool.operation`, `bluetusk.streams.spool.outcome` |

`bluetusk.source` is the source fingerprint. Tags never contain connection
strings, credentials, row values or logical-message content.

Each snapshot attempt is a `bluetusk.streams.snapshot` activity tagged with
`bluetusk.source`, `bluetusk.slot`, `bluetusk.snapshot.epoch`,
`bluetusk.snapshot.attempt` and, on success, `bluetusk.snapshot.rows`.

`delivery.duration` measures from delivery to acknowledgement, so a growing
value usually means your own work is slow. A rising
`delivery.settlement.failures` count means acknowledgements are failing, for
example because a checkpoint write failed or the lease was lost.

## Find out why spooling is slow

> **New in 1.1.0:** `bluetusk.streams.spool.operation.duration` is not in
> 1.0.0 or 1.1.0-rc.1.

When large transactions are slow, check the spool timing histogram. It measures
three steps of finishing a spool file:

| `bluetusk.streams.spool.operation` | What is timed |
| --- | --- |
| `flush` | Flushing the file to disk, including buffered writes. |
| `close` | Closing the finished file. |
| `rename` | Renaming the partial file to its ready name. |

`bluetusk.streams.spool.outcome` is `success` or `failure`. Only steps that
were attempted are recorded, and timing is off when nothing listens to the
histogram. High `flush` times point at the storage device or host contention;
put `SpoolDirectory` on faster local storage rather than turning off durable
flushing. This histogram excludes serialization, replay and your own work;
compare it with `delivery.duration`.

## Related pages

- [Configuration: health and telemetry names](configuration.md#health-and-telemetry)
- [Troubleshooting](troubleshooting.md)
- [Control Plane](../control-plane/README.md) shows Streams health across a deployment.
