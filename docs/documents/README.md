# BlueTusk.Documents

BlueTusk.Documents `0.1.0-preview.1` provides typed PostgreSQL JSONB documents and optimistic atomic write sessions. The core accepts `DbDataSource`, depends on neither Npgsql nor EF Core, and requires explicit `JsonTypeInfo<T>` metadata for serialization and deserialization. BlueTusk's native data source works directly.

```csharp
await using var source = BlueTuskDataSource.Create(connectionString);
await using var store = new DocumentStore(source); // Borrows source; caller disposes it.
await store.InitializeAsync();                     // Run during deployment.

var orders = new DocumentCollectionDefinition<Order>("orders", OrderJsonContext.Default.Order);
using var session = store.OpenSession("customer-a");
session.Insert(orders, "order-42", new Order("pending", 12));
var writes = await session.SaveChangesAsync();

var order = await store.LoadAsync("customer-a", orders, "order-42");
session.Replace(orders, order!.Id, order.Value with { Status = "paid" }, order.Revision);
await session.SaveChangesAsync();
```

The application supplies its `OrderJsonContext` using System.Text.Json source generation. Every document must serialize to a JSON object. Schemas, collection names, identifiers and tenant identifiers are separate from type names; no reflection-based serializer fallback is available.

## Storage and concurrency

The primary key is `(tenant, collection, id)`, using PostgreSQL `C` collation for stable key ordering. Every read and write includes all required tenant and collection predicates. This is an application isolation contract, not a database privilege boundary: configure PostgreSQL privileges or RLS separately when untrusted callers can execute SQL.

A store borrows its data source by default. Pass `DocumentDataSourceOwnership.Owned` to transfer disposal responsibility. Store disposal invalidates its sessions. Sessions exclusively own the connection and transaction for each save. An application cannot attach an externally owned transaction to a session.

Inserts use `ON CONFLICT DO NOTHING`, replacements and deletes use revision compare-and-swap, and every failed precondition raises `DocumentConcurrencyException`. The exception contains tenant, collection, ID, expected revision and the observed current revision (or null when missing). All staged operations roll back when any operation conflicts, including writes completed in an earlier batch. Failed sessions retain their pending writes so the application can inspect and clear them; success clears them.

Revisions come from a non-cycling database sequence. Updates advance revisions and delete/reinsert cannot reuse a revision. Gaps caused by failed transactions are expected. Sequence values represent concurrency tokens, not commit ordering or event stream positions.

Sessions stage one mutation per document key. Explicitly combine patches or construct the final typed replacement before staging; duplicate staging fails early. Saves acquire row locks in ordinal collection/ID order to reduce deadlocks. PostgreSQL can still report deadlocks, serialization errors or connection failures; the application chooses its retry policy. Cancellation or connection failure during commit can leave commit outcome uncertain. Read the document/revision before retrying an insert, and use a business idempotency key where required.

Providers that support `DbBatch` execute bounded command chunks in one protocol cycle. Other providers use sequential commands under the same transaction. Defaults are 256 commands per batch, 4096 writes and 32 MiB staged payload per session, 4 MiB per document, and a 30-second command timeout. Counts and byte budgets are configurable within hard count limits. Serialized writes are admitted through a bounded stream; database constraints additionally enforce the normalized JSONB text size.

## Reads, patches and evolution

`ReadPageAsync` uses an ID keyset cursor, limits row count, and uses a SQL cumulative byte budget before payloads leave PostgreSQL. Defaults are 1000 maximum rows and 16 MiB of JSON text. A page that stops on its byte budget returns the last emitted ID; resume using that cursor with the same tenant, collection and filter. Pages do not provide a snapshot across separate transactions: concurrent inserts before a cursor are not emitted later. An optional JSON containment filter uses `body @> @contains::jsonb`.

`DocumentPatch.Set` and `Remove` snapshot their path and JSON input. Paths are parameterized as JSON arrays and converted to PostgreSQL `text[]`; arbitrary SQL cannot be supplied. Operations apply in order and use the same revision checks and atomic transaction as typed replacements. Empty/root paths are rejected. Paths support PostgreSQL array index semantics. `jsonb_set` creates a missing terminal key when its parent exists; it does not synthesize missing intermediate objects. Set/remove retain the collection's schema version and cannot silently upgrade or downgrade a document.

`StoredDocument<T>.SchemaVersion` records the typed payload version. A replacement may explicitly upgrade but cannot downgrade it. `MigratePageAsync` transforms raw stored JSON from one specified version into a target `DocumentCollectionDefinition<T>` through a deterministic `DocumentMigration<T>`. Each bounded page atomically commits with revision checks. A competing writer makes the whole page fail; retry the same cursor after resolving the conflict. Restarting an already completed migration does not repeat transformed rows. Keep transforms deterministic and free of external side effects; callbacks receive JSON valid only during that callback.

## Indexes and deployment

`EnsureIndexAsync` installs either a JSONB containment GIN index (`jsonb_path_ops`) or a collection-scoped text-path B-tree index with tenant and ID columns. Declared SQL and PostgreSQL's normalized catalog definition are recorded. Repeated matching definitions are idempotent; definition changes, dropped indexes and invalid indexes fail explicitly. The application still needs an access pattern-specific index and query plan review. Keyset scans use the primary key, while containment filters can use the declared GIN index.

Provisioning currently creates indexes transactionally and can block writes. Schedule it during deployment on empty storage or in an agreed maintenance period. Online concurrent index provisioning and resumable index builds remain required before large live installations can treat index rollout as unattended operations.

Storage initialization takes a PostgreSQL advisory transaction lock and validates installed storage version and document byte constraints. Different configured document-size constraints cannot share a schema. Use unique schema names for isolated tests. Storage migrations between future library versions require an explicit release migration; initialization does not overwrite unknown metadata.

## Streams and Live integration

`BlueTusk.Documents.Streams` maps committed Streams transactions and consistent snapshot batches into a tenant/collection-scoped typed view. It retains the original `ChangeId`, commit position and transaction, enforces transaction count/byte limits, decodes PostgreSQL text/binary JSONB and integer representations, and preserves unavailable old columns and unchanged TOAST states. It never fetches a later database value to pretend it was the original WAL image. Deletes under ordinary key replica identity retain their key and causal change identity even when the old revision/body is unavailable. Updates whose key crosses a scope boundary become an insertion or deletion within that scope.

Historical or future document schema versions are explicit and are not deserialized through an incompatible typed contract. `RequireCompleteNewDocuments` opts into rejecting partial or different-schema new images. `DocumentStreamDeployment.EnableFullReplicaIdentityAsync` is a deployment operation that takes a table lock and increases WAL volume; publishing all document columns with full replica identity enables complete old images and safe unchanged-TOAST fallback. Truncation raises `DocumentStreamResetRequiredException` and requires a fresh consistent snapshot. Prepared transaction lifecycle deliveries require a durable staging consumer and are rejected by this adapter.

`DocumentTransactionConsumer<T>` calls the application only after bounded mapping succeeds, then acknowledges after the callback returns. The application must commit all business effects and causal identities atomically inside that callback; acknowledgement failures can still cause redelivery. Callback, mapping and cancellation failures nack the upstream delivery. Snapshot lifecycle and derived state cutover remain owned by the Streams snapshot coordinator/application.

`BlueTusk.Documents.Live` creates an ID-ordered bounded query plan over the typed store. It snapshots JSONB containment filters, fingerprints schema/collection/schema version/filter/window/limit, declares the document-table dependency and compares rows using document revisions. The tenant resolver receives the server's authenticated `LiveSecurityScope`; no tenant query argument is accepted from the client. The caller versions authorization/query behavior through the plan and scope policy versions. The result is the first bounded page, including the store's byte limit; it is a live window rather than a full collection subscription.

Use the existing `PostgreSqlLiveInvalidationStore` and `LiveInvalidationConsumer` to persist committed Streams invalidations before acknowledging WAL, then refresh Live sessions authoritatively. Table dependencies currently invalidate all windows over that table; query execution rechecks each authenticated tenant filter. This does not provide row-level ACL policy automatically. The actual PostgreSQL integration test publishes the document table, consumes real pgoutput, maps one atomic replace/insert transaction, deduplicates its durable invalidation, and verifies tenant-scoped Live update/add/remove diffs.

## Operator readiness

`DocumentStore.ReadHealthAsync(tenant, collection)` uses one read-only statement to validate the singleton storage version/document-byte contract and perform an indexed existence check over the selected tenant/collection. It returns database time and payload-free metadata; it reads no JSON bodies, creates no schema and repairs nothing. An empty collection is ready. This is a storage readiness seam, not a scan of document payload schema versions, declared indexes or physical PostgreSQL resources.

The optional `BlueTusk.Documents.AspNetCore` package integrates that seam with standard ASP.NET health checks:

```csharp
builder.Services.AddHealthChecks().AddBlueTuskDocuments(
    "orders-documents-ready", documents, "customer-a", "orders");
// Configure an operator authorization policy before mapping this host-owned endpoint.
app.MapHealthChecks("/ops/documents").RequireAuthorization("operators");
```

Registration captures the host-selected tenant/collection; the adapter maps no endpoint and takes no client-selected scope. The host owns its authentication, operator policy, response writer and store lifetime. Default admission permits one nonqueued probe with a two-second deadline; configurable limits are 64 probes and ten seconds. Saturation is degraded; missing/incompatible storage, permission/connection failure and timeout are unhealthy with fixed codes, no attached exception and no payload. Caller cancellation propagates. A driver that ignores cancellation retains its admission slot until late completion; late faults are observed without logging. ASP.NET normally returns HTTP 200 for degraded results; a host using this as readiness can select HTTP 503 for degraded health.

The probe can run with SELECT-only privileges on `storage_metadata` and column SELECT on `documents(tenant,collection)` plus schema usage. Actual tests grant that role access, deny JSON-body reads, and run the same core/host probe successfully. The tenant predicates are application isolation; use database privileges/RLS when untrusted callers can execute SQL themselves.

`BlueTusk.Documents.Health` emits `bluetusk.documents.health.probes` and `bluetusk.documents.health.duration` in seconds. Its only tag is fixed `status` (`healthy`, `degraded`, `unhealthy`, `cancelled`); it contains no tenant, collection, ID, SQL parameters, connection strings, payload or provider error messages. Duration measures the reported result, not eventual late-driver completion. Hosts supply metrics export and alerts. Session/write/query throughput instrumentation and physical database health remain separate integrations.

## Verification and production qualification

Run the unit and live PostgreSQL suite with `BLUETUSK_TEST_CONNECTION_STRING` set:

```powershell
dotnet test tests/BlueTusk.Documents.Tests/BlueTusk.Documents.Tests.csproj -c Release -nr:false
```

The suite passes 30 tests with no skips. Tests cover source-generated JSON round trips, competing inserts and updates, cross-batch atomic rollback, missing-document conflicts, tenant and collection separation, keyset/byte-bounded pagination, ordered patches, deletion/recreation revision fencing, migration and downgrade protection, declarative index/catalog drift, cancellation, source ownership, initialization and bounded admission. The serialization cases include exact UTF8 byte-limit and invalid-root rejection plus large/small owned JSON round trips. Five operator cases additionally check SELECT-only roles with denied payload access, fixed scope/role-protected actual HTTP readiness (401/403), storage/byte-contract drift, metadata-lock deadlines/cancellation/recovery, retained admission for late driver work and status-only metrics. A bounded concurrent workload checks committed row counts and no lost successful increments; it is functional workload evidence, not a throughput or endurance certification.

The core and optional ASP.NET adapter enable NativeAOT/trimming analyzers. The combined `tests/BlueTusk.Edge.NativeAotSmoke` executable was published as win-x64 NativeAOT without warnings and run against disposable PostgreSQL/pgvector databases and a real SQLite file. It verifies Documents source-generated JSON, CAS and complete session rollback, plus authenticated readiness, incompatible storage detection and recovery through the ASP.NET adapter. Search and Edge paths pass in the same executable. Set both `BLUETUSK_TEST_CONNECTION_STRING` and `BLUETUSK_SEARCH_VECTOR_CONNECTION_STRING` when running it. This qualifies the exercised paths on this architecture; the optional Streams/Live adapters are not included in that native smoke.

The [source-frozen 600-second Documents comparison](evidence/2026-09-28-maintenance-pair.md)
adds real process-kill recovery and measured PostgreSQL/TOAST/WAL resource profiles. Its
fixed-cardinality write workload still grew the Documents relation to 16.20 GB with package
defaults and 9.06 GB with aggressive fixture-only TOAST vacuum. Neither profile established a
physical storage bound, and the aggressive profile had lower throughput and worse tail latency.
Architecture-specific qualification beyond the exercised Windows x64 paths, ambiguous COMMIT
and connection-loss injection, longer endurance, sustained hot-key performance, online index
deployment, RLS guidance, session/query/write telemetry, two-phase document consumers,
snapshot-cutover application recipes and immutable cross-version release gates remain outstanding.
Production-scale performance and efficiency are not yet proven.
