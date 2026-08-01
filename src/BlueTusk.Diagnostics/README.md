# BlueTusk.Diagnostics

Dependency-free .NET `ActivitySource`, `Meter`, and redacted `EventSource`
contracts for BlueTusk PostgreSQL connections, commands, pools, prepared
statements, COPY, failover, and replication.

Register `BlueTusk.Diagnostics` with the application's OpenTelemetry tracing
and metric pipeline. Command spans use stable database-client attributes and
never include SQL text, parameter values, connection strings, exception
messages, passwords, or access tokens.

Opt in to redacted slow-command events on a long-lived data source:

```csharp
await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .ConfigureDiagnostics(new BlueTuskDiagnosticsOptions
    {
        SlowCommandThreshold = TimeSpan.FromSeconds(1),
    })
    .Build();
```

The `BlueTusk-Diagnostics` event source reports only operation, database,
elapsed time, and explicit leading-comment query tags. Full instrument names,
dimensions, lag definitions, and listener guidance are documented in the
repository's `docs/observability.md` guide.
