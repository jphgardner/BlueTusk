# BlueTusk.Streams.Tool

`BlueTusk.Streams.Tool` validates and safely provisions PostgreSQL sources for
direct Streams consumer groups or the durable PostgreSQL relay.

```powershell
dotnet tool install --global BlueTusk.Streams.Tool --version 0.1.0-preview.1
$env:BLUETUSK_STREAMS_SOURCE = "Host=source;Database=app;Username=streams;Password=..."
$env:BLUETUSK_STREAMS_CONTROL = "Host=control;Database=streams;Username=streams;Password=..."

bluetusk-streams provision --publication app_changes --slot app_streams `
  --table app.orders --table app.order_items
bluetusk-streams validate --publication app_changes --slot app_streams
```

Provisioning is idempotent: existing compatible publications and slots are
left in place, while missing relay tables are migrated to the current storage
format. The relay connection is required by default. `--direct-only` is an
explicit opt-out for slot-per-group deployments.

Source and control connection strings are accepted through environment
variables so secrets do not need to appear in shell history. Diagnostics
redact either connection string. Shared source/control databases require
`--allow-shared-control`, and their publications are rejected if they contain
the relay control schema.
