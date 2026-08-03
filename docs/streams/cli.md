# Streams validation and provisioning CLI

`BlueTusk.Streams.Tool` provides the `bluetusk-streams` .NET tool. It validates
the PostgreSQL version, `wal_level`, publications, table selection, logical slot,
source/control isolation, relay-schema exclusion, and canonical publication
fingerprint before a worker starts.

```powershell
dotnet tool install --global BlueTusk.Streams.Tool --version 0.1.0-preview.1
$env:BLUETUSK_STREAMS_SOURCE = "Host=source;Database=app;Username=streams;Password=..."
$env:BLUETUSK_STREAMS_CONTROL = "Host=control;Database=streams;Username=streams;Password=..."

bluetusk-streams provision --publication app_changes --slot app_streams `
  --table app.orders --table app.order_items
bluetusk-streams validate --publication app_changes --slot app_streams
```

The control connection is required during provisioning unless `--direct-only`
explicitly selects slot-per-group operation. Provisioning is idempotent: an
existing compatible publication or `pgoutput` slot is retained, and relay
storage runs its versioned `CREATE IF NOT EXISTS` migration path.

Using one database for the source and relay is not the default. It requires
`--allow-shared-control`; `--all-tables` is then rejected, and validation fails
if any configured publication includes the relay control schema. This prevents
the relay from consuming its own writes.

Use `--skip-slot` only when another deployment step owns slot creation. A
missing slot remains visible in the validation report. Connection strings may
be supplied as arguments for automation, but environment variables keep them
out of interactive shell history. Errors redact both source and control values.
