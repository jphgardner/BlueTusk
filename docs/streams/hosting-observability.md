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

### Investigating slow transaction spooling

Subscribe to the `BlueTusk.Streams` meter and inspect
`bluetusk.streams.spool.operation.duration` (seconds). It measures three separate
completion steps:

| `bluetusk.streams.spool.operation` | Measured boundary |
|---|---|
| `flush` | `FileStream.Flush(flushToDisk: true)`, including any buffered write |
| `close` | Closing the completed writer stream |
| `rename` | Moving the partial file to its ready name |

The other tag, `bluetusk.streams.spool.outcome`, is `success` or `failure`.
These are fixed values: no file paths, source IDs, transaction IDs or row data
are included. Timers are inactive when no listener subscribes to the histogram.
Only attempted steps are recorded; a flush failure does not produce a successful
close or rename measurement.
Exceptions raised while dispatching recorded measurements are isolated from
transaction completion; genuine filesystem exceptions still propagate.

This histogram is not total transaction latency: it excludes serialization,
earlier writes, replay, downstream processing and acknowledgement. Compare it
with delivery duration, runtime/GC counters and storage telemetry. High flush
latency warrants investigating the storage device and host contention; it is
not a reason to disable durable flushing. The spool format and acknowledgement
guarantees do not change when metrics are enabled.
