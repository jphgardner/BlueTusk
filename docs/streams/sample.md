# Snapshot-then-stream sample

The `BlueTusk.Samples.Streams` worker demonstrates the complete first-run path:
an exported PostgreSQL snapshot, bounded binary COPY batches, transition to the
matching `pgoutput` position, and transaction acknowledgement by a hosted
consumer.

Create the sample table and provision the publication. The snapshot source must
create the logical slot itself so it can export the matching snapshot; therefore
this setup deliberately uses `--skip-slot`.

```sql
CREATE SCHEMA IF NOT EXISTS app;
CREATE TABLE app.orders (
    id bigint PRIMARY KEY,
    description text NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
```

```powershell
$env:BLUETUSK_STREAMS_SOURCE = "Host=localhost;Database=app;Username=streams;Password=..."
bluetusk-streams provision --direct-only --skip-slot `
  --publication app_changes --slot app_sample_stream --table app.orders

$env:BlueTusk__Streams__Slot = "app_sample_stream"
$env:BlueTusk__Streams__Publications__0 = "app_changes"
dotnet run --project samples/BlueTusk.Samples.Streams
```

The sample logs raw snapshot batches and CDC change types. A real destination
must durably and idempotently apply a complete source transaction before it
acknowledges the delivery. Configure a checkpoint store and feedback observer as
described in [state stores](state-stores.md); memory state is only suitable for
tests and ephemeral development.

The sample's table shape is intentionally fixed so binary column ordinals and
PostgreSQL type OIDs remain explicit. Production mappings should use the typed
mapping builder or the [EF-derived mapping adapter](typed-mappings.md).
