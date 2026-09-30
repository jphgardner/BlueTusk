# Durable joined orders with Live SSE

This executable .NET 10 sample uses BlueTusk.Data, Streams, Projections, Projections.Live and the
existing Live PostgreSQL replay/SSE packages. It does not use EF interception or a second CDC service.

Run `deployment.sql` explicitly against a development PostgreSQL database with `wal_level=logical`
and a role permitted to create/use the publication and logical slot, read PostgreSQL control functions
and emit transactional logical messages. The source's text columns and
`REPLICA IDENTITY FULL` match the example definition's full-row decoding contract. Existing publication
creation is intentionally an explicit deployment step. It will fail if its name is already in use.

```powershell
$env:BLUETUSK_SAMPLE_CONNECTION_STRING = '<development PostgreSQL connection string>'
$env:BLUETUSK_SAMPLE_API_KEY = '<provisioned random API key, at least 32 UTF-8 bytes>'
$env:BLUETUSK_SAMPLE_RESUME_KEY = '<base64 of at least 32 random signing-key bytes>'
$env:BLUETUSK_SAMPLE_TENANT = 'first'
$env:ASPNETCORE_URLS = 'http://127.0.0.1:5080'
dotnet run --project samples/BlueTusk.Projections.Orders.Live -c Release
```

The first run creates an exported consistent snapshot on `orders_live_sample`, imports both source
tables into durable source mirrors, builds joined order documents and exact tenant totals, publishes
version 1, then consumes retained pgoutput WAL. It renews the destination worker lease every ten seconds.
On subsequent process starts it reacquires the version lease and resumes that same source slot from
the target checkpoint. An abrupt death requires the one-minute lease to expire before replacement.
Committed effects/checkpoints precede source feedback. Shutdown does not delete retained slots or data.

Each authorized query also acquires a database-backed Live publisher lease for 30 seconds, validated on
refresh/connect and renewed every ten seconds. Replay append and ownership fencing share one transaction.
Another HTTP node receives 503 while that owner remains active; route reconnects to the current owner.
After release/expiry a replacement uses the same retained sequence and emits a `ServerRestart` reset.
Fenced nodes close local subscribers. Provision stable keys and query identity across all nodes; the
sample deliberately provides no additional inter-node proxy or transport.

Authenticate every request with `X-API-Key`. POST `/bluetusk/live/sse` using this JSON:

```json
{"query":"orders","parameters":{"minimumAmount":0}}
```

The response uses the existing BlueTusk SSE format and returns signed resume tokens. To reconnect,
repeat the request with its `resumeToken` property. The resolver derives the tenant from the authenticated
principal, accepts only the registered decimal filter, bounds it to 0..1000000, and permits at most 32
parameter variants, 1000 subscribers per variant, a 64-message client queue, 1024 replay events per
connect, 100 source documents per query page, and 1 MiB of document payloads. The filter applies within
that first ordinal key page. This is a bounded key-window demonstration, not a global sorted/filtered
report. Slow clients and expired/unavailable replay use the existing explicit Live failure/reset contract.

`GET /sample/total` returns the authenticated tenant's exact published order total and version/revision.
`GET /sample/status` returns the published pointer. The sample API key provisions one configured tenant;
replace this mechanism with your application's identity/tenant authorization for a real deployment.
Use TLS outside a local development endpoint and provision stable signing keys across restarts.

Commit source changes normally, for example changing `orders_sample.customers.name`, inserting orders,
changing an order's amount, or deleting an order. The definition collapses repeated changes to a source
key to its final transaction image, adjusts each tenant's aggregate delta once, bulk-writes mirrors, then
recomputes/drains joined dependents. Live emits keyed changes and removals after projection commit.
Unrelated tenant rows do not appear in this subscription.

Additional environment variables select `BLUETUSK_SAMPLE_SOURCE_SCHEMA`,
`BLUETUSK_SAMPLE_PROJECTION_SCHEMA`, `BLUETUSK_SAMPLE_SLOT`, `BLUETUSK_SAMPLE_PUBLICATION`, and
`BLUETUSK_SAMPLE_VERSION`. These must match explicitly deployed source tables/publication and the
registered projection source contract.

To rebuild concurrently, start a second process with the same source/publication/projection schema and
stable API/resume keys, a new `BLUETUSK_SAMPLE_SLOT`, `BLUETUSK_SAMPLE_VERSION=2`, and a separate HTTP
port. It captures actual immutable source lineage, takes its own exported snapshot, and catches up through
its own retained WAL without publishing over version 1. Do not replace the active slot or snapshot over
a published version. Source/publication DDL must remain unchanged throughout both snapshot and WAL.

On the candidate server, authenticated `POST /sample/promote` accepts:

```json
{"requiredPosition":0,"expectedActiveVersion":1,"allowEquivalentSourceLineage":true}
```

This explicit policy captures fresh actual lineage, emits a verified transactional source barrier and
waits at most 30 seconds for the candidate checkpoint to cover it. The core atomically verifies matching
persisted lineage, complete snapshot coverage, the expected active pointer, valid fencing and coverage
through the locked active checkpoint. It then promotes version 2; existing version-1 Live servers detect
the published pointer and emit a `SchemaChanged` reset. `requiredPosition` may impose an additional
source LSN barrier; zero cannot bypass the emitted barrier. Omitting the policy flag uses strict exact
source identity and rejects a different slot. `POST /sample/barrier` emits a source barrier separately.

Publication identity includes actual OID, operation flags, full membership/columns/types/keys and
filters; the current seam rejects filtered or partial-column publications. Independent database histories,
timeline/failover changes and historical DDL reversal are not certified. Keep the old worker/version until
the rollback window closes, then stop it and use guarded retirement plus bounded derived-state pruning.
Source slot cleanup remains an explicit deployment operation.

For controlled source DDL, authenticated `POST /sample/maintenance` accepts
`{"expectedActiveVersion":2,"maintenanceId":"<stable-guid>","reason":"<deployment reason>"}`.
It irreversibly fences the old projection worker before DDL while published reads remain available.
Stop that process (its next lease renewal stops it), execute the controlled source DDL transaction,
and deploy a compatible new version with a fresh slot. Configure the candidate with
`BLUETUSK_SAMPLE_RECOVERY_ID=<stable-guid>`, `BLUETUSK_SAMPLE_RECOVERY_ACTIVE_VERSION=2`, and
`BLUETUSK_SAMPLE_RECOVERY_REASON=<permanent incident/deployment reason>`. All three fields are required;
malformed/partial configuration fails startup. The worker records or reopens the durable ticket before
starting its fresh exported snapshot. After bootstrap, authenticated `POST /sample/recover` captures
fresh target evidence, waits for the verified WAL barrier, then atomically completes that ticket.
Ordinary `/sample/promote` cannot bypass a maintenance/recovery fence. Signed subscribers receive
`SchemaChanged` on recovery cutover and later normal WAL updates.

After timeline failover, point the configured new-version candidate at the authoritative promoted
source and use the same explicit recovery configuration. Fence the old primary externally first and
verify replication/restore preservation of acknowledged commits; neither the ticket nor these sample
endpoints certify that infrastructure decision. Persist stable keys/query identities and route Live
requests to their durable current owner. The demonstration API key authorizes operator endpoints too;
replace it with separate tenant query and operator recovery authorization in a real deployment.
See [recovery boundaries and actual physical-promotion evidence](../../docs/projections/RECOVERY.md).

`OrdersLiveSampleTests` deploys the actual process using its own random resources and tests exported
snapshot, two-table WAL, authentication/tenant filtering, SSE updates/deletes, signed reconnect, abrupt
process restart, replay reset, checkpoint catch-up and a second process's real concurrent rebuild/promotion.
It also verifies publisher contention, owner death, signed replay takeover and new WAL updates served
by the replacement node.
It then calls the maintenance endpoint, applies actual source DDL, launches a third configured recovery
process, completes the ticket and verifies SSE resets plus subsequent WAL updates.
Full production performance, high availability, security deployment, automated rebuild scheduling/recovery
and endurance gates remain required.
