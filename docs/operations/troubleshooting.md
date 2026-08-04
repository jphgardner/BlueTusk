# Troubleshooting

Troubleshoot BlueTusk from the lowest failing boundary upward: transport,
authentication, protocol, pool, command, type mapping, EF translation, then
real-time delivery.

## Capture the minimum useful context

Record:

- BlueTusk commit or package version;
- .NET runtime and operating system;
- PostgreSQL version and relevant extension versions;
- connection topology without credentials;
- whether pooling, multiplexing, pipeline mode or PgBouncer is involved;
- the exception type, SQLSTATE and inner exception chain;
- a minimal parameterized query or reproduction; and
- focused logs/traces with secrets removed.

Do not post connection strings, passwords, tokens, certificates or captured
production row data.

## Connection and authentication

### Connection refused or timeout

1. Resolve the hostname from the application environment.
2. Confirm the port is reachable.
3. Inspect PostgreSQL readiness and `pg_hba.conf`.
4. Verify the target host role when multi-host routing is enabled.
5. Compare connection timeout with DNS and network failure duration.

Pool acquisition timeout is different from socket connection timeout. Inspect
pool waiting/active counts before assuming the network is responsible.

### TLS or channel binding failure

Confirm:

- TLS mode matches server policy;
- certificate hostname and trust chain are valid;
- client certificate/key files are readable by the process;
- channel binding is supported by both sides; and
- a proxy is not terminating TLS in an unexpected place.

Never “fix” production certificate errors by disabling validation.

### SCRAM, OAuth, GSSAPI or cloud identity failure

Identify the selected mechanism and credential source. Check token expiry,
service principal/hostname agreement, clock skew and environment identity.
Cloud SDK default credential chains can select a different identity locally
and in deployment.

## Pool and session behavior

### Connections remain busy

Look for undisposed readers, transactions, COPY streams, notification listeners
or replication sessions. Session-affine operations intentionally keep a
physical session.

### State leaks between requests

Reproduce with pooling disabled. If the symptom disappears, identify temporary
tables, `SET`, advisory locks, prepared statements or application state that
outlives the logical connection. Do not rely on undocumented reset behavior.

### Multiplexing rejected

Bounded multiplexing fails closed for session-affine commands. Use a dedicated
or ordinary pooled connection for transactions, COPY, notifications,
replication, temporary objects and other stateful SQL. See
[multiplexing compatibility](../ado-net/multiplexing-compatibility.md).

## Commands and readers

### Command hangs after cancellation

Cancellation and protocol recovery are separate. Capture the original timeout,
whether the server received cancellation and whether the session was discarded
or recovered. Avoid immediately reusing a session whose protocol state is
unknown.

### Sequential read error

With `CommandBehavior.SequentialAccess`, consume fields and field segments in
the documented order. Do not access an earlier field after advancing beyond
it. See [sequential readers](../ado-net/sequential-readers.md).

### SchemaOnly or KeyInfo throws

This is intentional V1 behavior. Both modes are excluded rather than silently
ignored. Use ordinary result-schema discovery or EF reverse engineering.

## Type mapping

When the server reports an unknown OID:

1. identify the PostgreSQL type in the connected database;
2. confirm the required extension is installed;
3. confirm its BlueTusk extension package was registered before building the
   data source;
4. reload/rebuild the catalogue snapshot through the documented lifecycle; and
5. specify an explicit provider type when CLR inference is ambiguous.

Do not hard-code an OID copied from another cluster; extension OIDs are
installation-specific.

## EF Core

### Query cannot be translated

Inspect the LINQ shape and the supported translation documentation. BlueTusk
fails explicitly instead of silently evaluating unsupported server expressions
on the client. Reduce to a minimal query and compare generated SQL for a nearby
supported form.

### Migration differs from expected schema

Inspect model annotations, current database catalogue and generated operations.
Owners, privileges and some deployment security decisions remain explicit SQL
by design.

### Scaffolding omits an object

Confirm the authenticated role can see it, and that the object kind is part of
the documented database-first surface. Capture the relevant `pg_catalog` row
without credentials or production data.

## Streams and replication

### Slot lag grows

Check consumer progress, checkpoint writes, relay storage, long-running
transactions and destination latency. WAL retention can exhaust primary
storage; alert before the slot becomes an incident.

### Source identity mismatch

Stop and investigate. The checkpoint may belong to a different cluster,
publication or recreated environment. Do not overwrite the identity guard
without a documented bootstrap/recovery decision.

### Schema fingerprint mismatch

The relation or typed mapping changed. Deploy compatible mappings, migrate the
checkpoint format when supported, or perform a controlled snapshot/rebuild.

### Duplicate destination effects

Review destination idempotency/version keys and whether acknowledgement occurred
after the destination write. BlueTusk does not promise end-to-end exactly-once
effects.

## Diagnostic commands

```powershell
dotnet --info
docker compose -f eng/compose/postgres.yml ps
docker compose -f eng/compose/postgres.yml logs postgres18
dotnet test tests/BlueTusk.Data.Tests/BlueTusk.Data.Tests.csproj -c Release
dotnet test tests/BlueTusk.IntegrationTests/BlueTusk.IntegrationTests.csproj `
  -c Release `
  --filter "FullyQualifiedName~RelevantFixture"
```

Use the narrowest relevant test project/filter before running the entire
solution.

## Reporting a defect

A useful report contains a minimal reproduction, expected PostgreSQL behavior,
actual BlueTusk behavior and the smallest relevant log/trace. Suspected
security vulnerabilities must follow `SECURITY.md` and must not be opened as a
public issue.
