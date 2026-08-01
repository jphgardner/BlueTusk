# Diagnostics and observability

BlueTusk exposes .NET-native tracing, metrics, and redacted slow-command events
without requiring an OpenTelemetry runtime dependency. Register the activity
source and meter named `BlueTusk.Diagnostics` with the application's chosen
exporter.

```csharp
services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(BlueTuskDiagnostics.InstrumentationName))
    .WithMetrics(metrics => metrics.AddMeter(BlueTuskDiagnostics.InstrumentationName));
```

The provider follows the stable OpenTelemetry database-client conventions for
command spans and `db.client.operation.duration`. PostgreSQL command spans use
`ActivityKind.Client` and low-cardinality names such as `SELECT app`. They can
carry:

- `db.system.name=postgresql`;
- `db.namespace`;
- `db.operation.name` and `db.query.summary`;
- the selected `server.address` and `server.port`;
- `error.type` on failure; and
- bounded `bluetusk.query.tags` from leading `--` comments.

Connection opens create `CONNECT host:port` client activities. Buffered,
sequential, synchronous, asynchronous, data-source-owned, and batch command
paths all use the same contract. A sequential-reader span ends after the portal
is established; later network consumption remains under the reader's lifetime
and cancellation rules.

BlueTusk never emits `db.query.text`, parameter values, a connection string, an
exception message, a password, or an access token. SQL can contain literals,
so omitting command text is deliberate rather than a missing configuration
switch. Leading line comments are explicit telemetry tags: use short,
low-cardinality labels and never place user data or credentials in a query tag.
At most eight tags of 256 characters each are exported. PostgreSQL block
comments are skipped and are not treated as tags.

## Metrics

The process-wide `BlueTusk.Diagnostics` meter publishes:

| Instrument | Unit | Meaning |
| --- | --- | --- |
| `db.client.operation.duration` | `s` | Stable database command duration with endpoint, database, operation, and error-type dimensions |
| `bluetusk.commands.executed` | `{command}` | Completed command and batch attempts |
| `bluetusk.commands.failed` | `{command}` | Failed command and batch attempts |
| `bluetusk.connections.opened` / `failed` | `{connection}` | Physical connection outcomes |
| `bluetusk.connections.retries` / `failovers` | `{attempt}` / `{connection}` | Multi-host retries and non-first-host selections |
| `bluetusk.pool.*` | mixed | Physical connections, leases, waiters, reuse, resets, discards, and checkout duration |
| `bluetusk.prepared_statements` | `{statement}` | Explicit, automatic, and batch prepare/reuse/evict/invalidate actions |
| `bluetusk.protocol.message.size` | `By` | Backend protocol message size |
| `bluetusk.copy.bytes` | `By` | COPY throughput with direction |
| `bluetusk.replication.receive_lag` | `s` | Non-negative local receive time minus PostgreSQL WAL-sender clock |
| `bluetusk.replication.wal_lag` | `By` | Non-negative server WAL end minus the received WAL end |

The existing `bluetusk.commands.duration` instrument remains as the low-level
Client operation timer. `db.client.operation.duration` is the tagged ADO.NET
client-operation metric for OpenTelemetry consumers.

Prepared-statement metrics use `bluetusk.prepared.kind` (`explicit`,
`automatic`, or `batch`) and `bluetusk.prepared.action` (`prepare`, `reuse`,
`evict`, or `invalidate`). Retry reasons and endpoint dimensions contain no
credentials.

## Slow-command events

Slow-command logging is opt-in per immutable data-source configuration:

```csharp
await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .ConfigureDiagnostics(new BlueTuskDiagnosticsOptions
    {
        SlowCommandThreshold = TimeSpan.FromSeconds(1),
    })
    .Build();
```

Enable the `BlueTusk-Diagnostics` `EventSource` with an `EventListener`, ETW, or
the application's event pipeline. Event 1 is a warning containing only the
operation, database, elapsed seconds, and explicit query tags. It does not
contain SQL or exception text. A null threshold disables the event; zero is
useful for testing an event pipeline.

Instrumentation performs no SQL parsing or timestamp work when no matching
activity, meter, or configured slow-event listener is enabled. Metrics and
activities have deterministic listener tests with secret-leak assertions, and
the normal PostgreSQL version matrix executes a tagged, parameterized command
through the public data-source API.

See OpenTelemetry's current
[database client span](https://opentelemetry.io/docs/specs/semconv/db/database-spans/)
and [database client metric](https://opentelemetry.io/docs/specs/semconv/db/database-metrics/)
conventions for exporter behavior.
