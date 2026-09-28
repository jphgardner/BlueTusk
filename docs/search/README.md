# BlueTusk.Search

BlueTusk.Search `0.1.0-preview.1` provides versioned PostgreSQL full-text ingestion and retrieval with tenant and permission filtering. BlueTusk.Search.PgVector adds schema-qualified vector storage, cosine retrieval and optional HNSW indexing. Both use `DbDataSource`; neither depends on Npgsql or EF Core. The adapter requires a separately installed pgvector extension, validated during initialization.

```csharp
await using var source = BlueTuskDataSource.Create(connectionString);
await using var search = new PostgreSqlSearchStore(source);
await search.InitializeAsync();
await search.UpsertAsync(new SearchDocument(
    "customer-a", "knowledge", "article-42", 7,
    "Warehouse guide", "Use barcode scanning when receiving stock.",
    principals: ["warehouse-readers"]));

var scope = new SearchScope("customer-a", "knowledge", ["warehouse-readers"]);
var page = await search.SearchAsync(scope, new SearchRequest { Text = "barcode", PageSize = 20 });
if (page.NextCursor is not null)
    page = await search.ContinueSearchAsync(scope, page.NextCursor);
```

The host must derive tenant, index and principals from authenticated authorization state. The library cannot authenticate supplied principal strings. Every ingestion identity, query candidate and result page is tenant/index scoped. Private documents are the default; an explicit public flag grants retrieval within the document's tenant and index only.

## Durable ingestion and deletion

Each `(tenant,index,document)` retains its highest source version and payload fingerprint. Versions must be positive and monotonically increasing for that logical source identity. A higher version atomically replaces all chunks and metadata in one PostgreSQL transaction. Same-version identical replay is idempotent; conflicting payload at the same version raises `SearchVersionConflictException`. Lower versions are ignored. Deletions retain a durable tombstone and remove all chunks, so delayed older ingestion cannot resurrect a document. No tombstone expiration is automatic: purging fences requires an independently proven upstream replay floor.

Titles, metadata and ACLs live once per document; chunks retain content, weighted full-text terms and optional embeddings. Chunk replacement and version advancement share the same commit boundary. Existing search snapshots recheck source version and deletion state, so removed or replaced documents are not returned from old snapshots.

Deterministic chunking preserves UTF-16 surrogate pairs, includes configurable overlap and rejects content exceeding the configured maximum chunks. Empty content produces one chunk so the title remains searchable. Defaults admit 4 MiB of source text/title/metadata, 64 KiB metadata, 4 KiB titles, 2048 chunks, 2048 characters per chunk and 128 characters overlap. Chunk and embedding model identity are stored as an immutable storage contract; incompatible initialization fails instead of silently mixing models.

Ingestion and automatic query embedding share a nonqueued capacity gate (default eight operations). Saturation immediately raises `SearchBackpressureException`; place callers behind an upstream durable bounded queue. Embedding batches are at most 32 texts by default. The application provides `ISearchEmbeddingProvider`, declares a stable model identity, and receives cancellation. The adapter validates dimensions, finite values and a nonzero cosine norm before any database mutation. Embedding failure leaves the previous document searchable. Delivery to an external embedding service requires its own timeout, request idempotency and data-handling policy.

`BlueTusk.Search.Jobs` durably snapshots a source version, content, metadata and ACL into a typed source-generated Jobs payload before embedding. Admission has a serialized byte limit and deduplicates one target/index/document/version identity, rejecting incompatible same-version payloads. An overload enqueues inside a caller-owned application transaction in the Jobs database. Configure a stable `IndexContract` identifying target schema/chunking/dimensions/model, register handlers through `SearchIngestionJobs.RegisterHandlers`, and run an ordinary scoped `JobWorker` with bounded concurrency, payloads, leases, heartbeats and retry policy.

The handler validates tenant/queue/type/contract and database-clock lease fencing before external work. Search itself enforces atomic version/tombstone fencing when embeddings finish. A crash after Search commits and before Jobs completes is recovered by replay; known matching versions skip repeat embedding. Search and Jobs completion are separate commits, and a lease expiring during an external embedding request cannot roll that request back. Tests reopen persisted Jobs, recover a newer lease after the Search-commit/completion window, reject expired owners and incompatible scopes, roll back transactional admission, ignore delayed work after deletion, and execute a registered worker through a transient embedding retry.

### Durable embedding checkpoints

`PostgreSqlEmbeddingCheckpointProvider` in `BlueTusk.Search.Jobs` wraps an application embedding provider with PostgreSQL checkpoints. Initialize it before supplying it to either PostgreSQL Search or OpenSearch. Both ingestion and automatic query embedding propagate `SearchScope` through `IScopedSearchEmbeddingProvider.EmbedForScopeAsync`; ordinary `ISearchEmbeddingProvider` implementations keep their existing behavior. The checkpoint wrapper rejects its unscoped method to prevent accidental cross-tenant cache use.

```csharp
await using var embeddings = new PostgreSqlEmbeddingCheckpointProvider(
    dataSource, applicationEmbeddings,
    new SearchEmbeddingCheckpointOptions { Dimensions = 1536 });
await embeddings.InitializeAsync();
await using var search = new PostgreSqlSearchStore(dataSource,
    new SearchStoreOptions(), new PgVectorSearchAdapter(1536), embeddings);
await search.InitializeAsync();
```

Keys are `(tenant,index,model,UTF-8 text SHA-256)`. Exact repeated text within a batch is embedded once and returned in its original order. Completed vectors survive wrapper/store reopen and are shared within the same tenant/index/model, including changes to the principal set. The model identity must describe stable embedding semantics for that key; permission-sensitive embedding transformations need distinct model identities. Cached vectors do not authorize document retrieval. Raw text is never persisted by this wrapper and it emits no text, payload or provider-error logs. Text hashes and embeddings can still reveal sensitive input, so restrict database access and apply the deployment's encryption/retention policy. Jobs payloads and indexed documents separately retain source text under their own policies.

Missing vectors reserve fixed `dimensions * 4` bytes and a row before the provider call. A database-clock lease with a random owner and positive fence owns the whole provider batch. Claim/read/reserve and vector completion use bounded SQL batches under short metadata transactions; provider calls run outside those transactions. Completion verifies a live batch lease and every row's owner/fence/deadline, then commits all vectors atomically. A replaced row or expired owner commits none of its batch. Failures and cancellation release the batch admission lease and expire its owned pending rows for bounded cleanup. Cached vectors use validated little-endian float32 data; wrong dimensions, non-finite values, zero cosine norm and mismatched result counts cannot become completed checkpoints.

Defaults bound eight local operations, eight database-wide active provider batches, two per tenant, 256 input texts, 64 KiB per text and 1 MiB total input per call. The shared schema retains at most 128 model contracts, 100,000 rows and 256 MiB reserved vector bytes. A checked schema contract prevents hosts from silently disagreeing about global admission/storage limits or model dimensions. Capacity exhaustion rejects admission; active checkpoints are not evicted. Model registry entries remain bounded and do not automatically expire. Row and vector-byte counters describe logical retained data, not PostgreSQL table/index/TOAST/WAL overhead. Physical growth and vacuum behavior need deployment qualification.

Provider calls have a 30-second deadline inside a one-minute database-clock ownership lease; completed vectors expire after one day. All durations are bounded and configurable. Expired checkpoints become inaccessible before cleanup. Admission prunes at most 128 expired entries, and hosts must schedule `PruneExpiredAsync` for idle namespaces. The data source is borrowed by default, with an explicit owned option; the underlying application provider always remains caller-owned. Disposal drains local operations before disposing an owned data source.

These checkpoints reduce repeated completed work, but external provider calls remain **at least once**. A crash after the provider returns and before checkpoint commit can repeat a call. Timeout or owner replacement may leave an uncooperative provider task running; the wrapper observes later faults but cannot stop external effects. Database admission bounds live logical owners. Physical request concurrency requires the provider to honor cancellation/deadlines or supply a separately bounded transport. No provider idempotency contract is assumed. Fair scheduling and time-based provider quotas remain host requirements. Jobs translates checkpoint ownership loss and provider deadline failure into retryable sanitized failure codes. A Jobs lease expiring during a call still does not fence Search's transaction against that lease; Search uses its independent source-version/tombstone fence.

The crash-recovery test first commits embedding checkpoints, injects a failing Search chunk insert, expires the Jobs lease, then reopens checkpoint/Search/Jobs stores under a newer owner. Search commits using the cached vectors without another provider call. It then expires that owner before job acknowledgement and reopens again; the final lease replays the committed source version and acknowledges successfully, while both older owners are rejected. Current metadata and ACL behavior are checked at each boundary. Additional tests replace checkpoint owners and individual batch fences, inject checkpoint completion failure, validate input/vector/schema/model bounds, enforce database-wide/per-tenant capacity, expire/prune exact retention counters, and cancel a timed-out provider. A bounded workload persists 1024 distinct 1 KiB texts for eight tenants in 32 batches and reopens every cached batch without another external call. Actual OpenSearch ingestion and automatic vector queries also reuse scope-isolated checkpoints after reopen.

## Retrieval, ranking and pagination

Full-text retrieval uses PostgreSQL `simple` text search, weighted titles and `websearch_to_tsquery`, backed by a GIN index. Configure language-specific analysis through a future versioned index definition; the current tokenizer is deliberately fixed as part of the storage contract.

For vector ingestion, supply `PgVectorSearchAdapter(dimensions)` and an embedding provider to the store. Queries can supply a vector directly or embed their text through the same provider. Hybrid queries combine the authorized full-text and vector candidate ranks using weighted reciprocal rank fusion. `FullTextWeight`, `VectorWeight` and `ReciprocalRankConstant` are explicit bounded parameters. Every candidate list applies permissions before ranking. Stable ties use document ID and chunk ordinal.

Vector retrieval is exact by default, including after an HNSW index exists. `ApproximateVectorSearch=true` explicitly permits an ANN order scan. ANN trades recall for speed and permission filters can underfill the result set; an approximate result count is not a completeness guarantee. `CreateHnswIndexAsync` installs a cosine index for up to 2000-dimensional vectors during deployment. Storage supports up to 16000 dimensions without that HNSW index. The package validates installed pgvector 0.8.0 or newer and the extension schema. These limits follow the [pgvector documentation](https://github.com/pgvector/pgvector).

`ISearchRankingExtension` receives only authorized bounded candidates and returns finite scores for a subset of their existing identities. Duplicate identities, invented candidates, non-finite scores and output beyond the candidate bound fail. The reranking payload is capped in SQL before it leaves PostgreSQL (default 8 MiB). Custom ordering is persisted before serving its first page.

Searches persist a bounded rank snapshot (default 200 candidates, maximum 1000) for two minutes. Pagination uses the snapshot UUID and last emitted rank. Scope fingerprints bind cursors to the tenant, index and exact sorted principal set. Every page rechecks current ACLs, source versions and deletion state. New documents do not alter a snapshot's rank order; changed/revoked/deleted candidates disappear. Expired, missing and wrong-scope cursors raise `SearchCursorExpiredException` without returning another scope's data.

Pages bound both rows (default maximum 100) and bytes (default 8 MiB, enforced by a SQL cumulative budget). Byte-limited pages resume at the last emitted rank. Candidate snapshots are capped per tenant/index (default 128 retained queries) under an advisory transaction lock. Expired queries are pruned during admission; `PruneExpiredQueriesAsync` supports bounded concurrent cleanup with `SKIP LOCKED`. Schedule this cleanup for idle scopes. Snapshot creation currently serializes searches within one tenant/index admission lock, including ranking work; reducing this critical section is a performance follow-up that requires failure/recovery evidence.

## OpenSearch adapter

`BlueTusk.Search.OpenSearch` uses a caller-supplied `HttpClient` with explicit borrowed/owned disposal. Configure trusted endpoint, physical index name, shard/replica counts, optional vector dimensions and embedding model. Credentials and TLS policy stay on that client. `InitializeAsync` must create/validate the index before use. It persists an immutable model/chunking/vector contract and checks strict mappings, dimensions, vector method and payload fields. Index rollover/alias cutover and cluster-wide deployment orchestration remain host responsibilities.

Each authorized source document is one OpenSearch parent containing bounded nested chunks. Real-time sequence-number/primary-term compare-and-swap replaces that whole parent atomically, including its metadata, ACL, chunks and optional embeddings. Replays compare the source version/fingerprint; incompatible same-version writes fail and older writes are ignored. Deletion retains a parent tombstone, rather than relying on an engine deletion-version expiration period. Embedding failures do not alter the previous parent. Administrator deletion of the physical index loses these durable fences; prevent automatic index creation and restore/rebuild under an explicit replay-floor policy.

Full-text ranking queries nested text with the standard analyzer. Vector ranking defaults to exact cosine script scoring over nested vectors. `ApproximateVectorSearch=true` selects nested Lucene HNSW; tenant/index/deletion/permission filters run inside kNN candidate admission, followed by the same outer authorization check. Both return one best matching chunk per source document. Hybrid retrieval executes both bounded candidate queries under one point-in-time context, combines document ranks with weighted reciprocal rank fusion, then releases that server context. The actual ANN test places 24 unauthorized nearest vectors ahead of two authorized records, requests only two candidates, and receives both authorized records; later ACL revocation is rejected on page delivery. This verifies the filter placement for that fixture, not general recall or throughput. ANN recall/latency sweeps across representative data and permission selectivity remain necessary.

The returned `OpenSearchSearchSession` is a caller-owned bounded rank snapshot, scoped to its originating store and authenticated principals with a fixed expiration. Each page uses real-time multi-get to recheck current ACL, source version and tombstones; revoked/replaced records disappear while retained ranks stay stable. Newly ingested records do not enter an existing session. These sessions are process-local; host restart requires a fresh query, unlike PostgreSQL's persisted search cursors. HTTP requests/responses, candidates, snapshot payloads and result pages have separate byte limits, and nonqueued operation admission bounds concurrent embedding/HTTP work. Hosts must bound how many returned sessions they retain.

For pagination that survives host/store restart, configure a `PostgreSqlOpenSearchCursorStore` and call `SearchDurableAsync` / `ContinueDurableSearchAsync`. Source-generated rank JSON is persisted before the first page; the returned `SearchCursor` retains its UUID and consumed rank. Keys bind the physical OpenSearch index UUID, endpoint/index/model/chunk contract, tenant, logical index and exact sorted principal set. Each continuation checks the physical generation before and after delivering a page, reads its database-clock deadline, and rechecks current document ACL/version/deletion through real-time multi-get. Recreating an index invalidates its old cursors even if new documents reuse source versions. Hosts must authorize supplied scopes afresh on every continuation.

```csharp
await using var cursors = new PostgreSqlOpenSearchCursorStore(dataSource);
await cursors.InitializeAsync();
var page = await openSearch.SearchDurableAsync(scope,
    new SearchRequest { Text = "barcode", PageSize = 20 }, cursors);
if (page.NextCursor is not null)
    page = await openSearch.ContinueDurableSearchAsync(scope, page.NextCursor, cursors);
```

The optional cursor schema has a checked version/serialization contract and explicit borrowed/owned data-source lifetime. Defaults retain 1000 candidates/8 MiB serialized per snapshot, 128 snapshots per physical target/tenant/logical index, 4096 globally and 64 MiB total payload, with a two-minute deadline and eight nonqueued operations. Admission locks a metadata row briefly to prune at most 128 expired snapshots and update exact row/byte counters; ranking stays outside that critical section. `PruneExpiredAsync` supports scheduled bounded cleanup for idle scopes. Capacity exhaustion rejects admission rather than evicting an active cursor. Expiration makes data inaccessible before cleanup, but hosts must schedule pruning and qualify physical PostgreSQL table/TOAST/WAL growth. The retained snapshot's content is sensitive, so database access and encryption policy belong to the deployment.

The complete Search suite currently passes 53 tests with no skips, including 14 actual OpenSearch cases. Persisted-cursor cases reopen both stores, preserve existing ranks while ignoring new documents, recheck tenant/principals/current ACL, reject a recreated physical index, enforce database-clock expiration and row/byte capacity, bound cleanup, reject contract drift, and inject an insert-trigger failure proving retention counters/capacity roll back. The tests use independent disposable PostgreSQL schemas and OpenSearch indices; results do not establish distributed failover or sustained recall/performance qualification.

The live fixture uses OpenSearch 3.8.0 pinned at `opensearchproject/opensearch@sha256:fafe3fc3587088674669235575aa166228c48bdb940294a8cdbbc1da75236a40`, configured through `eng/compose/opensearch.yml`. Set `BLUETUSK_SEARCH_OPENSEARCH_ENDPOINT` to its disposable endpoint (`http://127.0.0.1:59200` locally). Actual nested full-text/vector/hybrid queries, competing updates, durable deletion fences, current-page revocation, payload budgets, invalid embedding preservation, mapping/model mismatch, HTTP cancellation/admission/ownership and a 32-parent concurrent workload passed. The fixture disables security and serves only loopback; production clients must supply their authenticated cluster transport. API behavior follows the official [nested vector search](https://docs.opensearch.org/latest/vector-search/specialized-operations/nested-search-knn/) and [document concurrency](https://docs.opensearch.org/latest/api-reference/document-apis/index-document/) contracts.

## Operator readiness

`PostgreSqlSearchStore.ReadHealthAsync(scope)` uses one read-only statement to validate its stored version/chunk/model/vector contract, perform a tenant/index primary-key existence check and observe at most `MaxActiveQueriesPerScope + 1` retained query rows through the existing scope/expiration index. It reads no source text, metadata, ACLs, vectors or rank payloads. Existence includes retained document identities/tombstones and does not grant retrieval permission. Query counts are bounded observations; an extra row is an over-capacity sentinel. Active counts use database time, while expired rows remain visible in the retained count until cleanup. The probe does not prune or repair anything, contact embedding/OpenSearch services, qualify ANN recall, validate every physical index or establish PostgreSQL disk/WAL health.

The optional `BlueTusk.Search.AspNetCore` package registers a fixed trusted host scope:

```csharp
builder.Services.AddHealthChecks().AddBlueTuskSearch(
    "knowledge-search-ready", search, new SearchScope("customer-a", "knowledge"));
app.MapHealthChecks("/ops/search", new HealthCheckOptions
{
    ResultStatusCodes = { [HealthStatus.Degraded] = 503 },
}).RequireAuthorization("operators");
```

The host configures its operator authorization policy and safe response writer. Registration accepts no client scope and maps no endpoint. Default admission is one nonqueued probe with a two-second deadline, bounded to 64 probes/ten seconds. Saturation and retained query capacity are degraded; missing/incompatible format/contract, permission/connection failure and timeout are unhealthy with fixed codes and no attached exception/error payload. Caller cancellation propagates. If a driver ignores cancellation, admission remains reserved until it actually finishes; late faults are observed without logging. A retained-capacity warning does not mean every query must fail: ordinary admission can prune expired rows. Scheduled cleanup keeps idle scopes ready. ASP.NET defaults degraded health to HTTP 200; the example uses HTTP 503 for readiness.

A SELECT-only probe role needs schema usage, SELECT on `storage_metadata`, and column SELECT on `documents(tenant,index_name)` and `queries(tenant,index_name,expires_at)`. Real fixtures deny that role title/metadata reads while the probe succeeds. Host-selected scope strings and operator authorization are application policy; use database privileges/RLS when callers can execute SQL directly.

`BlueTusk.Search.Health` emits `bluetusk.search.health.probes` and `bluetusk.search.health.duration` in seconds, with only fixed `status` tags (`healthy`, `degraded`, `unhealthy`, `cancelled`). It includes no tenant/index/principal/ID, SQL parameters, connection string, query/source text, payload or provider exception message. Duration covers the reported result rather than late-driver completion. Hosts attach exporters and alerts; ingestion/ranking/provider throughput and OpenSearch/checkpoint-store operational probes remain separate integrations.

## Operations and evidence

Initialization provisions versioned storage, full-text/ACL indexes and query retention tables. Run it during deployment. Index creation is currently transactional and can block live writes. The caller owns the data source by default; explicit `SearchDataSourceOwnership.Owned` transfers disposal. Store disposal drains active embedding/ingestion slots before disposing owned resources. The host should drain retrieval operations before disposing its data source.

Live tests use `BLUETUSK_TEST_CONNECTION_STRING` for ordinary PostgreSQL and `BLUETUSK_SEARCH_VECTOR_CONNECTION_STRING` for a database with pgvector installed:

```powershell
dotnet test tests/BlueTusk.Search.Tests/BlueTusk.Search.Tests.csproj -c Release -nr:false
```

The local vector fixture is isolated at loopback port 55419 with PostgreSQL 18.6 and pgvector 0.8.6, image `pgvector/pgvector@sha256:2ba9ca5f2e7daa0f0e7723cba1ee9167bab54efd3640516a44ac1a928dd67e7a`. Test schemas are unique and cleaned individually.

The suite covers tenant/index/ACL isolation, stable scope-bound pagination, current revocation checks, expiration and capacity, competing versions, idempotent replay and version conflicts, durable deletion fences, chunk replacement, true pgvector/hybrid retrieval, HNSW exact preservation, invalid embedding atomicity, overload rejection, authorized ranking extensions, injected server chunk failure rollback, storage-contract mismatch and cancellation. Six operator cases verify SELECT-only roles with denied payload access, scope isolation, bounded retained-query observations without side effects, actual role-protected HTTP readiness (401/403), storage/model-contract drift, metadata-lock deadlines/cancellation/recovery, retained admission for late driver work and status-only metrics. A bounded workload ingests 512 1 KiB documents through eight concurrent writers and pages every persisted candidate exactly once.

The core, PgVector and optional ASP.NET adapter enable NativeAOT/trimming analyzers. The combined `tests/BlueTusk.Edge.NativeAotSmoke` was published as win-x64 NativeAOT without warnings and run against real PostgreSQL/pgvector and SQLite fixtures; Search scoped ingestion/automatic query embedding, hybrid retrieval, ACL filtering and tombstone version fencing passed. The same executable verifies authenticated ASP.NET readiness, incompatible storage detection and recovery. Both `BLUETUSK_TEST_CONNECTION_STRING` and `BLUETUSK_SEARCH_VECTOR_CONNECTION_STRING` are required. Other native architectures and complete API paths require further qualification.

OpenSearch ANN recall/latency qualification, embedding-provider idempotency integration/fair scheduling/time-based quotas, Streams ingestion integration, online index rollout/rebuild, schema/format upgrade rehearsals, ingestion/ranking/provider telemetry and OpenSearch/checkpoint-store health integration, language analysis, latency/allocation/WAL reports, distributed failover/process-kill/network-partition tests and sustained endurance remain outstanding. Search.Jobs currently targets PostgreSQL Search; durable OpenSearch dispatch requires a corresponding handler/atomic effect policy. Optional Jobs/OpenSearch packages are not included in the native executable smoke. No production qualification or performance leadership claim follows from the short functional workload.
