# Roadmap

This file tracks executable repository status. The product vision is broader; unchecked work is not implied by package names already present in the solution.

## Real-time platform release trains

- [x] Phase 0: accepted delivery, checkpoint, snapshot, spool, relay, Live security, and Sync connector decisions
- [x] Phase 0: independent Provider, Streams, Sync, Live, Control Plane, and Continuous Graph version properties and release workflow
- [x] Phase 0: machine-enforced application CDC boundary and family declarations
- [x] Phase 0: public contracts, delivery gates, and operational defaults documented
- [x] Phase 1: Streams transaction kernel, explicit row states, streamed assembly, bounded spool, public API baseline, PostgreSQL 15–19 acceptance, and checked-in performance baseline
- [x] Phase 2 slice: monotonic compare-and-swap checkpoint contract, exclusive leases, fencing tokens, and checkpoint-before-feedback crash boundaries
- [x] Phase 2 slice: atomic checksummed single-node file state store and public custom-store conformance kit
- [x] Phase 2 slice: PostgreSQL production state store, server-clock leases, and control-schema publication rejection across PostgreSQL 15–19
- [x] Phase 2 slice: versioned binary relay envelopes, fenced source append, independent relay groups, replay, retention, capacity limits, and health signals across PostgreSQL 15–19
- [x] Phase 2 slice: Redis server-clock checkpoint/lease store with atomic script conformance and a dedicated Redis 8 CI gate
- [x] Phase 2: checkpoint stores, leases, direct groups, and durable PostgreSQL relay
- [x] Phase 3 slice: typed core mappings, canonical schema/mapping fingerprints, explicit partial-row safety, decoding/drift policy, and snapshot/transaction consumer lifecycle
- [x] Phase 3 slice: exported consistent snapshot, bounded parallel keyset binary COPY, explicit restart epochs, and matching-position no-gap acceptance across PostgreSQL 15–19
- [x] Phase 3 slice: EF model-derived typed mappings with startup validation for table, key, publication-column, setter, and duplicate-binding safety
- [x] Phase 3 slice: in-process hosted consumers, standard health checks, worker status registry, and exporter-neutral activities/metrics
- [x] Phase 3 slice: CloudEvents 1.0 structured transaction events with stable IDs and versioned integrity-checked payloads
- [x] Phase 3 slice: idempotent source/relay CLI provisioning, PostgreSQL compatibility validation, canonical publication fingerprints, control isolation, and secret-safe diagnostics
- [x] Phase 3 slice: Aspire worker wiring with secret-preserving source/control references, relay-by-default configuration, and explicit direct-mode opt-out
- [x] Phase 3 slice: fail-closed cross-process snapshot-slot recovery with explicit new epochs and PostgreSQL 15-19 acceptance
- [x] Phase 3: typed mappings, safe snapshot bootstrap, hosted integration, and Streams 0.1.0-preview.1 release train
- [x] Phase 4 slice: opt-in prepared/commit-prepared/rollback-prepared lifecycle delivery, streamed spooling, live acceptance, and relay envelope format 1-to-2 compatibility
- [x] Phase 4 slice: transactional relay schema v1-to-v2 migration, envelope-protection/key-rotation hooks, confirmed consumer tombstones, bounded retention policies, and compaction
- [x] Phase 4 slice: consistent framed relay backup, empty-schema atomic restore, protected-envelope validation, tombstone/fencing preservation, and corruption rollback across PostgreSQL 15–19
- [ ] Phase 4: Streams hardening, API freeze, and Control Plane foundation
- [ ] Phase 5: PostgreSQL, NATS, Redis, and OpenSearch Sync connectors through 1.0
- [ ] Phase 6: authorised Live query platform and clients through 1.0
- [ ] Phase 7: Continuous Graph preview

The detailed contracts and release gates are maintained in the [real-time platform plan](realtime-platform/delivery-plan.md).

## 0.0.1 — Foundation (complete)

- [x] Repository and package structure
- [x] Shared SDK, formatting, analyzer, and CI rules
- [x] Architecture and compatibility decisions
- [x] PostgreSQL 15–18 Docker environments and a PostgreSQL 19 beta compose profile
- [x] Fragmentation-aware backend frame parser
- [x] Startup and simple-query message writer
- [x] Connection state machine
- [x] Initial catalogue type model and `int4` codec
- [x] Fake backend stream utilities
- [x] Scriptable fake PostgreSQL TCP server
- [x] Protocol packet-inspection file format
- [x] BenchmarkDotNet harness and checked-in baselines

## 0.0.2 — Authentication and simple queries (complete)

- [x] SSLRequest and TLS upgrade
- [x] Startup parameter/status processing
- [x] SCRAM-SHA-256 and SCRAM-SHA-256-PLUS
- [x] Legacy PostgreSQL MD5 challenge compatibility with overwrite-on-flush credential buffers and PostgreSQL 15–19 acceptance
- [x] Cleartext password compatibility over TLS, or by explicit plaintext-transport opt-in, with PostgreSQL 15–19 acceptance
- [x] PostgreSQL password-file resolution plus lazy synchronous/asynchronous password and access-token callbacks
- [x] PostgreSQL 18+ OAUTHBEARER with sync/async TLS conformance and a live validator gate
- [x] GSSAPI/Kerberos and SSPI with mutual authentication, sync/async conformance, and a live MIT Kerberos/PostgreSQL 18 gate
- [x] TLS client-certificate collections and explicit certificate selection with safe server validation defaults
- [x] Backend key data capture
- [x] Cancellation channel (delivered in 0.0.4)
- [x] Error and notice field parsing
- [x] Simple query operation
- [x] Initial ADO.NET connection, command, reader, and unpooled data source

## 0.0.3 — Extended queries and parameters (complete)

- [x] Parse/Bind/Describe/Execute/Sync writers
- [x] RowDescription and DataRow parsing
- [x] Typed parameter encoding
- [x] Multiple results
- [x] End-to-end `SELECT $1::int4 + $2::int4`

## 0.0.4 — Transactions and cancellation (complete)

- [x] Dedicated CancelRequest framing and sync/async transport
- [x] Cancellation-token and command-timeout integration
- [x] Drain through `ReadyForQuery` before connection reuse
- [x] Explicit `Cancel()` and `CancelAsync()`
- [x] PostgreSQL transaction isolation modes
- [x] Commit, rollback, rollback-on-disposal, and failed-transaction recovery
- [x] Typed and base-class ADO.NET transaction acceptance tests

## 0.0.5 — Connection pooling and data sources (complete)

- [x] Bounded per-data-source physical connection pool
- [x] Minimum/maximum size, idle lifetime, and maximum lifetime
- [x] Safe session reset and health validation
- [x] Waiter cancellation and pool draining
- [x] Pool diagnostics and live concurrency tests

## 0.0.6 — Core binary type codecs (complete)

- [x] Core scalar codec registry for boolean, integer, floating-point, numeric, character, binary, UUID, temporal, JSON, and XML values
- [x] Binary result negotiation and registry-driven decoding
- [x] Binary parameter encoding for numeric, UUID, and temporal values
- [x] Buffer-backed stream and text-reader accessors for `bytea`, text, and JSON values
- [x] Date/time infinity, 24:00 time, and arbitrary-precision numeric edge cases

## 0.0.7 — Advanced, dynamic, and structured types (complete)

- [x] Wire-specific scalar families: time with time zone, interval, bit strings, `pg_lsn`, and `tid`
- [x] Network and geometric scalar families
- [x] Money with `lc_monetary`-aware scale discovery
- [x] Full-text scalar family
- [x] Catalogue-specific scalar families
  - [x] Transaction identifiers and snapshot values
  - [x] Object-identifier aliases and catalogue vector values
  - [x] JSONPath, cursor, node-tree, and internal character values
  - [x] Text-only and opaque system-catalogue values
- [x] Catalogue discovery and per-data-source cache with explicit reload
- [x] Catalogue-driven arrays in binary and text formats
- [x] Catalogue-driven enums and domains with CLR enum mapping
- [x] Composite metadata plus named and anonymous record wire values
- [x] CLR composite mapping through `MapComposite<T>` with convention- and attribute-based member names
- [x] Optional source-generated composite codecs with compile-time shape diagnostics,
  catalogue-authoritative binding, generated construction/member access, array composition,
  and binary/text round-trip coverage
- [x] Catalogue-driven ranges and multiranges in binary and text formats
- [x] Public runtime codec registration by discovered catalogue name or explicit OID

## 0.0.8 — COPY and notifications (complete)

- [x] Binary, text, CSV, and raw COPY APIs
  - [x] Streaming raw COPY FROM/TO for text, CSV, and binary payloads
  - [x] Streaming `TextReader`/`TextWriter` helpers for text and CSV
  - [x] Typed binary importer and exporter
  - [x] Race-free initialization for immediate and empty COPY completion
- [x] `LISTEN`/`NOTIFY` asynchronous delivery
  - [x] Strict backend `NotificationResponse` decoding
  - [x] Quoted, idempotent channel subscriptions and explicit unlisten APIs
  - [x] Bounded asynchronous delivery without occupying the primary command session
  - [x] Close, reopen, cancellation, and PostgreSQL 15–18 lifecycle coverage
- [x] Large-object streams
  - [x] Transaction-aware create, open, and delete APIs
  - [x] Asynchronous read/write streams with 64-bit seek and truncate
  - [x] Implicit commit/rollback ownership and explicit-transaction composition
  - [x] PostgreSQL 15–18 lifecycle and failure-recovery coverage

## 0.0.9 — Replication preview (complete)

- [x] Physical and logical replication sessions
  - [x] Replication startup negotiation and duplex `COPY BOTH`
  - [x] WAL data, primary keepalives, and physical streaming
  - [x] Logical plugin options and raw custom-plugin output
- [x] Slot/publication discovery and feedback
  - [x] Physical and logical slot create/drop/read/list operations
  - [x] Publication, table, column, and row-filter discovery
  - [x] Standby status updates, keepalive replies, and hot-standby feedback
- [x] `pgoutput` decoding
  - [x] Relation/type metadata, DML tuples, truncate, origin, and logical messages
  - [x] Protocol-version-aware streamed transaction messages
  - [x] Two-phase and parallel-stream metadata
  - [x] PostgreSQL 15–18 and custom-plugin acceptance coverage

## 0.1.0 — First public ADO.NET preview

- [x] Prepared statements, batches, and multi-host connection attempts
  - [x] Explicit, automatic, named, and unnamed prepared statements
    - [x] Named statement Parse/Describe, Bind/Execute, Close, and asynchronous ADO.NET preparation
    - [x] Re-prepare when command text or parameter type identity changes
    - [x] Bounded automatic preparation cache with usage promotion, LRU eviction, and reset invalidation
    - [x] Deliberately selectable unnamed extended-query execution
    - [x] Synchronous preparation
  - [x] `DbBatch`/`DbBatchCommand`, parameters, transactions, cancellation, and multiple results
  - [x] Safe named-parameter rewriting and command execution-mode selection
  - [x] Ordered multi-host failover, target-session selection, and per-host pools
    - [x] Ordered and randomized host/port-list attempts with connected-endpoint reporting
    - [x] Primary, standby, read-write, read-only, and preferred-role target probes
    - [x] Authentication-stop and credential-redacted aggregate failure behavior
    - [x] Per-host pool partitioning and aggregate lifecycle/statistics
- [x] Genuine synchronous connection and query paths
  - [x] Synchronous transport, TLS, startup, authentication, and cancellation
  - [x] Synchronous pooling, transactions, commands, COPY, notifications, and large objects
    - [x] Per-host pooling, type discovery, commands, preparation, batches, transactions, and timeouts
    - [x] Streaming raw, text, and typed-binary COPY, notification subscriptions/waits, and large objects
- [x] Network-backed sequential and streaming reader modes
  - [x] Bounded named portals, suspension, and incremental row reads
  - [x] Sequential field access plus streaming `bytea`, text, and JSON
  - [x] Reader cancellation, disposal, and connection-reuse recovery
- [x] ADO.NET conformance, stress, differential, and performance baselines
  - [x] Provider-factory and base-class conformance suite
  - [x] Concurrent connection, cancellation, preparation, batch, and streaming stress suites
  - [x] Differential PostgreSQL behavior matrix and checked-in benchmark baselines

## 0.2.0 — EF Core CRUD preview (Milestone 5, complete)

- [x] `UseBlueTusk` options and provider-service registration
- [x] EF Core create, read, update, and delete operations
- [x] EF Core transaction and savepoint integration
  - [x] Explicit transaction begin and rollback
  - [x] Commit and savepoint coverage
- [x] Store-generated keys, defaults, computed values, and concurrency tokens
  - [x] Identity key propagation
  - [x] Defaults, computed values, and optimistic concurrency
- [x] Core relational type mappings and SQL generation
- [x] Core LINQ translation, query execution, and result materialisation
- [x] Initial migrations SQL generation and history repository
  - [x] Physical database existence/create/drop lifecycle with configurable admin database, sync/async paths, pool draining, catalogue refresh, quoted identifiers, and PostgreSQL 15–19 acceptance
  - [x] Common relational create-table, primary-key, foreign-key, index, default, and facet DDL
  - [x] PostgreSQL identity-column DDL and live schema-creation coverage
  - [x] Exact `ALWAYS`/`BY DEFAULT` identity metadata plus add, switch, drop, reverse-engineering, and generated fluent C# lifecycle
  - [x] Stored and PostgreSQL 18+ virtual generated columns, PostgreSQL 17+ expression alteration, destructive-change diagnostics, and server-version guards
  - [x] Table/column comment create, alter, clear, and reverse-engineering fidelity
  - [x] Table CHECK constraints with `NOT VALID`, `NO INHERIT`, PostgreSQL 18+ `NOT ENFORCED`, validation-only diffs, and generated migration C#
  - [x] Migration history repository, transactional locking, and idempotent migration scripts
  - [x] Live apply/revert coverage for alter, rename, sequence, index, and drop operations
- [x] Initial database reverse engineering
  - [x] EF design-time provider discovery and `UseBlueTusk` context generation
  - [x] Packaged `bluetusk scaffold` CLI with schema/table selection, naming options, safe overwrite behavior, and secure-by-default generated contexts
  - [x] Tables, views, columns, defaults, generated values, keys, foreign keys, indexes, and sequences
  - [x] Exact identity-generation modes, stored/virtual generated-column modes, server-normalized expressions, and comments
  - [x] Table CHECK expressions, validation/enforcement state, inheritance mode, and generated fluent C#
  - [x] Schema/table filtering, comments, store-type facets, and connection-ownership coverage
  - [x] Catalogue-only sequence discovery safe during concurrent schema changes
- [x] Initial EF Core relational specification-suite coverage
  - [x] Official EF Core 10.0.10 relational specification package pinned with an isolated, solution-level test assembly
  - [x] All official provider-service registration, idempotency, isolation, and lifetime contracts
  - [x] All official migrations SQL-generator cases with BlueTusk PostgreSQL golden baselines, including PostGIS seed literals
  - [x] Raw SQL composition, parameterization, compiled queries, and nullable projections
  - [x] Tracking modes, identity resolution, split-query includes, and relationship fix-up
  - [x] Relational command execution and bulk update/delete

## Cross-cutting architecture gates (required before 1.0)

### Data-source-first application model

- [x] `UseBlueTusk(BlueTuskDataSource)` preserves the source pool, configured codecs, and runtime catalogue
- [x] EF ownership, pool reuse, overload switching, and provider-service cache/debug-metadata coverage
- [x] ADO.NET, EF Core, and root samples lead with one long-lived data source per configuration
- [x] Directly constructed `BlueTuskConnection` documented as an unpooled compatibility/convenience path
- [x] Data-source-derived factory for dedicated, unpooled replication sessions

### PostgreSQL pipeline mode and transport evaluation

- [x] Client-layer PostgreSQL pipeline API with explicit `Sync` boundaries and ordered result groups
- [x] Pipeline error propagation, cancellation, disposal, and safe session-recovery semantics
- [x] Fake-server, conformance, stress, and live PostgreSQL pipeline-mode coverage
- [x] ADR separates PostgreSQL pipeline mode from `System.IO.Pipelines` and defines the evaluation gate
- [x] ArrayPool/Span/Memory versus `System.IO.Pipelines` prototype benchmarks
  - [x] Fragmented frames, large fields, COPY, cancellation, and TLS
  - [x] Genuine synchronous and asynchronous workloads
- [x] Retain ArrayPool/Span/Memory: measured benefits do not justify production `System.IO.Pipelines` complexity and regressions

### Transport contract

- [x] TCP and Unix-domain socket connections with genuine sync and async paths
- [x] Deterministic DNS expansion and ordered multi-address fallback
- [x] Shared address-attempt deadline plus async DNS/connect cancellation
- [x] TLS server validation, client certificates, and certificate selection
- [x] TCP keepalive/`NoDelay`, bounded socket buffers, and awaited-write backpressure
- [x] Stable connection-failure categories with ordered per-address diagnostics
- [x] Direct transport tests for fallback, failure, options, cancellation, deadlines, and Unix sockets

### Allocation discipline

- [x] Retain the span-based `BlueTuskReader`/`BlueTuskWriter` codec model
- [x] Profile complete parameter and result paths
  - [x] Per-command writers, per-parameter arrays, boxing, and text transcoding
  - [x] Structured codecs, COPY field buffers, and large-field materialisation
- [x] Introduce safe per-session reuse, pooling, sizing passes, or direct `IBufferWriter` encoding where measurements justify them
  - [x] Track untouched pool leases, share immutable data-source settings, and lazily allocate notification/large-object state while retaining dirty-lease reset isolation
  - [x] Collapse clean pool checkout arbitration to one lock, cache warm command plans and prepared scalar metadata, and retain only the first scalar response value
  - [x] Flatten sequential field `ValueTask` layers, read large caller buffers directly, and retain adaptive bounded protocol read-ahead for small reads
- [x] Check in end-to-end allocation baselines and explicit regression budgets
- [x] Describe inherently allocating returned CLR values accurately; no blanket “allocation-free” claim

### Enforced package boundaries and provider contract

- [x] Automated conformance test rejects reverse BlueTusk references and ADO.NET/EF leakage into lower layers
- [x] Remove the unused Client → Extensions.Abstractions reference
- [x] Document the narrow Data surface consumed by EF: data-source connection creation, parameter store-type identity, runtime type resolution, capabilities, and diagnostics
- [x] Keep protocol parsing independently testable without ADO.NET or EF
- [x] Run the architecture gate in every supported CI environment

### Diagnostics and observability

- [x] OpenTelemetry-compatible connection and command activities with stable database attributes and selected-endpoint identity
- [x] Command, query-duration, pool, protocol, prepared-statement, COPY-throughput, retry, failover, and replication-lag metrics
- [x] Bounded explicit query tags plus opt-in redacted slow-command events
- [x] No SQL text, parameters, connection strings, exception messages, passwords, or tokens in provider telemetry
- [x] Deterministic activity/meter/event listener tests plus live parameter-redaction acceptance

### Extension seam

- [x] Carry an immutable feature registry through `BlueTuskDataSource.Build()` and expose real consumption semantics
- [x] Complete a `citext` vertical slice without extension-specific core dependencies
  - [x] Codec/type registration and data-source-builder ergonomics
  - [x] ADO.NET live tests, documentation, and package metadata
  - [x] Separate EF type-mapping/query/migration plug-in with scalar and array coverage
- [x] Extension-authoring template
- [x] Extension compatibility-test harness
- [x] Stabilise extension APIs only after the vertical slice and compatibility gate
  - [x] Compiler-enforced shipped API/nullability baselines across the authoring seam

### Replication preview gate

- [x] Data-source-derived dedicated-session ergonomics without pooling replication connections
- [x] Allocation/backpressure benchmarks
- [x] Cancellation/disposal stress coverage
- [x] Reconnect/resume examples and explicit ownership/lifetime documentation
- [x] PostgreSQL 19 live coverage
- [x] Long-running durability, feedback, and failure-recovery matrix
  - [x] PostgreSQL 15–19 persistent-slot reconnect/resume acceptance
  - [x] Monotonic ordered feedback and exact pgoutput transaction checkpoints
  - [x] Wrong-system, missing, active, stale, and lost-WAL safety diagnostics
  - [x] Scheduled/manual PostgreSQL 19 1,000-epoch endurance job
- [x] Compiler-enforced shipped API/nullability baselines for both replication packages

### Release truthfulness

- [x] Reconcile package version, README, roadmap, and implemented prepared/batch/EF scope
- [x] Label implemented, tested preview, production-ready, and planned capabilities distinctly
- [x] Re-audit all public claims at each preview and 1.0 release gate
  - [x] 2026-08-02 `0.3.0-preview.1` whole-product audit reconciles README, version/support matrix, EF scope, release readiness, package output, and PostgreSQL 19 beta assumptions

## 0.3.0 — PostgreSQL-specific EF translations (Milestone 6, query surface)

- [x] Complete PostgreSQL type mappings
  - [x] Built-in wire-native scalar mappings with exact parameter OIDs
  - [x] Network, geometric, bit-string, money, arbitrary numeric, full-text, JSON path, system identifier, transaction, and catalogue CLR values
  - [x] PostgreSQL 19 unsigned `oid8` and `regdatabase` scalars/arrays with exact wire formats and older-version catalogue isolation
  - [x] Explicit `json`, `jsonb`, `xml`, `cidr`, `bit`, and legacy snapshot store-type selection
  - [x] Built-in one- and multidimensional arrays plus mutable generic collections with structural change tracking and exact element-family OIDs
  - [x] All six built-in range and multirange families, including their array types
  - [x] Runtime-registered enums, domains, composites, records, and their arrays
- [x] PostgreSQL operators and operator-aware LINQ translations
  - [x] Parameterised `ILIKE` and POSIX regular-expression predicates
  - [x] Array, range/multirange, JSONB/JSONPath, network, and full-text predicates
  - [x] SQL-generation and PostgreSQL 15–19 live operator acceptance
  - [x] Typed comparison and pattern `ANY`/`ALL` over PostgreSQL array parameters
  - [x] Equal-arity row values and tuple comparisons across all six B-tree operators
  - [x] Negative regex, complete range/multirange positional and cross-family predicates, strict network containment, and `tsquery` containment
  - [x] Typed scalar-producing array, range/multirange, JSONB, full-text, network, and bit-string operators
  - [x] Geometric ordering, position, containment, intersection, relationship, distance, closest-point, arithmetic, and transformation operators
- [x] PostgreSQL scalar, aggregate, and set-returning function translations
  - [x] Initial array, range/multirange, JSONB, regex, network, and full-text scalar functions
  - [x] Typed date/time construction, extraction, truncation, binning, age, and interval-justification functions
  - [x] PostgreSQL box, path, circle, line-segment, polygon, and point scalar functions
  - [x] Composable nested functions with typed result materialisation and PostgreSQL 15–19 acceptance
  - [x] Initial array, string, boolean, range-union, and range-intersection aggregates
  - [x] Ordered JSON/JSONB/XML, integer/`bigint` bitwise, and double/numeric population/sample statistical aggregates
  - [x] Aggregate ordering, `DISTINCT`, `FILTER`, typed results, and PostgreSQL 15–19 acceptance
  - [x] Remaining scalar functions
    - [x] Array inspection/search/mutation/string conversion plus PostgreSQL 16 shuffle/sample and PostgreSQL 18 reverse
    - [x] Text, identifier quoting/parsing, bytea encoding/editing, numeric, bucketing, and value-formatting families
    - [x] JSONB mutation/pretty/strip, parameterized JSONPath variables, and typed query-array/first/predicate results
    - [x] Configured text/JSONB vector and query construction, weights, stripping, query trees/rewrites, normalization/cover-density rank, and text/JSONB headlines
    - [x] Ordered JSON/JSONB object aggregates with typed tuple inputs
    - [x] Paired correlation, population/sample covariance, and complete linear-regression aggregate family
    - [x] Ordered-set scalar `mode`, continuous percentile, and discrete percentile with native `WITHIN GROUP`
    - [x] Array-valued continuous/discrete percentiles with typed result arrays
    - [x] Hypothetical rank, dense rank, percent rank, and cumulative distribution
    - [x] Bytea aggregation, range/multirange input variants, smallint/bit-string bitwise aggregates, generic mode/discrete percentiles, and interval continuous percentiles
    - [x] PostgreSQL 16+ `any_value` plus strict/unique JSON and JSONB aggregate variants
  - [x] Set-returning functions and lateral query roots
    - [x] Mapped-array `unnest` roots with ordinality, nullable elements, parameterized filters, and inner/outer lateral composition
    - [x] SQL-generation and PostgreSQL 15–19 live array-expansion acceptance
    - [x] Typed integer and bigint `generate_series` roots with parameterized standalone, correlated lateral, and compiled-query execution
    - [x] Typed numeric, timestamp, and timestamp-with-time-zone `generate_series` roots with exact argument mappings
    - [x] JSONB array-element, text-element, object-key, and JSONPath-query roots with exact mappings, ordinality, lateral composition, and compiled-query execution
    - [x] JSONB object key/value record roots with JSONB/text value mappings, nullable JSON-null text, and compiled-query execution
    - [x] Typed two-array integer/text `unnest` roots with unequal-length null padding, ordinality, lateral composition, and compiled parameters
    - [x] Schema-qualified, model-registered user-defined table functions with typed keyless rows, parameters, lateral composition, and compiled-query execution
    - [x] Model-derived JSONB-to-recordset roots with quoted column definitions, exact store types, nullable fields, lateral composition, and compiled-query execution
    - [x] Generic two-, three-, and four-array `unnest` with nullable padding, ordinality, compiled-query execution, and PostgreSQL 15–19 acceptance
    - [x] Typed `generate_subscripts` roots with dimension/reverse arguments, ordinality, correlation, and PostgreSQL 15–19 acceptance
    - [x] Regex match/split and nullable `string_to_table` roots with capture arrays, flags, null markers, parameters, and compiled-query execution
    - [x] Four-argument JSONPath-query roots with typed variables and silent-mode arguments
- [x] PostgreSQL-specific query roots and SQL constructs
  - [x] Ordered `DISTINCT ON` with leftmost-order validation, projection composition, compiled queries, and PostgreSQL 15–19 acceptance
  - [x] `TABLESAMPLE SYSTEM`/`BERNOULLI` with typed percentages, optional repeatable seeds, scope validation, and PostgreSQL 15–19 acceptance
  - [x] `FOR UPDATE`, `FOR NO KEY UPDATE`, `FOR SHARE`, and `FOR KEY SHARE` with wait, `NOWAIT`, and `SKIP LOCKED` behavior
  - [x] Typed ranking, distribution, bucket, offset, and value window functions with partitioning, ascending/descending ordering, nullable results, and compiled queries
  - [x] Explicit typed `tableoid`/transaction/command/tuple system-column mappings with migration exclusion and `xmin` concurrency
  - [x] Recursive/materialized CTEs and PostgreSQL data-modification query constructs
    - [x] Composable named CTEs with default, `MATERIALIZED`, and `NOT MATERIALIZED` planning, identifier validation, parameter preservation, ordered projections, compiled queries, and PostgreSQL 15–19 acceptance
    - [x] Typed recursive hierarchy CTEs with mapped key/parent selectors, parameterized multi-root traversal, cycle-safe `UNION`, opt-in `UNION ALL`, compiled queries, and PostgreSQL 15–19 acceptance
    - [x] PostgreSQL data-modification query constructs
      - [x] Typed single-table `DELETE ... RETURNING` and single-/multi-setter `UPDATE ... RETURNING`, parameterized projections, no-tracking materialization, compiled single-setter/delete queries, and PostgreSQL 15–19 acceptance
      - [x] Typed single-row `INSERT ... ON CONFLICT ... RETURNING` with mapped object initializers, single/composite conflict targets, `DO NOTHING`, selected-column `EXCLUDED` updates, compiled queries, and PostgreSQL 15–19 acceptance
      - [x] Typed single-row `MERGE` with model-derived identifiers, mapped parameters, composite match keys, matched update/delete/do-nothing actions, not-matched insertion, synchronous/asynchronous affected counts, EF transaction/diagnostics integration, and PostgreSQL 15–19 acceptance
- [x] Enum, domain, composite, range, multirange, array, JSON, network, geometric, and full-text query support
  - [x] Array predicates, scalar functions, aggregates, lateral element/subscript expansion, typed series/JSONB roots, and generic multi-array expansion
  - [x] Regex match/split and delimiter-table native query roots
  - [x] Parameterized runtime-enum equality and domain ordering plus flat typed-composite and lossless-record field predicates/projections with compiled queries and PostgreSQL 15–19 acceptance
  - [x] Parameterized two-dimensional `ARRAY[...]` construction plus one- through four-dimensional projected subscripting/slicing with compiled queries and PostgreSQL 15–19 acceptance
  - [x] Catalogue-resolved nested typed-composite and lossless-record-root field traversal with loaded-catalogue diagnostics, compiled queries, and PostgreSQL 15–19 acceptance
  - [x] EF structural-JSON scalar and nested-document traversal with typed PostgreSQL JSON operators, primitive-array expansion, typed structural-recordset expansion, and live complex-graph/ad-hoc query acceptance
  - [x] Focused 57-case native type, operator, function, root, UDT, and query-construct gate across PostgreSQL 15–19
- [x] PostgreSQL-specific query diagnostics and translation tests
  - [x] Translation-only operator API with no client implementation or raw-string fallback
  - [x] Provider SQL-expression quoting, nullability processing, and operator-family tests
  - [x] Focused diagnostics for invalid `DISTINCT ON` ordering/composition, sampling scope, duplicate locking clauses, and translation-only window markers
  - [x] Official ad-hoc complex/JSON query regressions plus offline SQL-shape tests for primitive and structural JSON collection expansion

## 0.4.0 — Advanced migrations and scaffolding (Milestone 6, schema surface)

- [x] PostgreSQL table CHECK constraints
  - [x] Standard EF CHECK metadata plus typed `NOT VALID`, `NO INHERIT`, and PostgreSQL 18+ `NOT ENFORCED` options
  - [x] Inline validated creation, deferred initial `NOT VALID` creation, manual add/validate operations, validation-only diffs, capability guards, and destructive diagnostics for non-in-place changes
  - [x] Direct `pg_constraint` discovery with canonical expressions, extension/inheritance/partition-clone exclusion, validation/inheritance/enforcement retention, and fluent C# scaffolding
  - [x] PostgreSQL 15–19 enforcement, validation lifecycle, reverse-engineering, and scaffolding acceptance
- [x] Advanced indexes, operator classes, collations, storage parameters, and included columns
  - [x] Built-in and extension-provided access methods, partial and trusted-expression indexes
  - [x] Per-key operator classes, collations, sort direction, and null ordering
  - [x] Included mapped columns, null-distinct unique indexes, and validated storage parameters
  - [x] Transaction-suppressed concurrent create/drop operations
  - [x] Column-based advanced-index catalogue discovery and fluent C# scaffolding
  - [x] Standalone and mixed expression-index discovery with canonical key SQL, fluent C# scaffolding, replay, and PostgreSQL 15–19 lifecycle acceptance
- [x] PostgreSQL exclusion constraints
  - [x] Typed column/expression elements, schema-qualified operators, access methods, collations, operator classes and parameters, sort/null ordering, included columns, storage parameters, tablespaces, partial predicates, and deferrability
  - [x] Create/drop/rename/replacement diffs with relational dependency ordering, rename-aware tables, destructive diagnostics, default-`RESTRICT` drops, and partitioned-root rejection
  - [x] Exact `pg_constraint`/backing-index discovery with canonical expression retention, schema filters, snapshots, and generated fluent C#
  - [x] PostgreSQL 15–19 enforcement, partial-predicate behavior, lifecycle, reverse-engineering, and scaffolding acceptance
- [x] PostgreSQL table, view, and foreign-table triggers
  - [x] Typed timing, INSERT/column-specific UPDATE/DELETE/TRUNCATE events, row/statement orientation, trusted `WHEN`, transition tables, schema-qualified functions, and literal arguments
  - [x] Constraint-trigger referenced tables and deferrability, origin/disabled/replica/always firing modes, extension dependency, safe replace guard, rename, and default-`RESTRICT` drop
  - [x] Function/view/table dependency ordering plus canonical `pg_trigger` discovery with clone, internal, and extension-owned exclusion and generated fluent C#
  - [x] PostgreSQL 15–19 execution, firing-mode lifecycle, reverse-engineering, and scaffolding acceptance
- [x] PostgreSQL rewrite rules
  - [x] Typed INSERT/UPDATE/DELETE/SELECT events, `ALSO`/`INSTEAD`, trusted conditions and actions, and origin/disabled/replica/always firing modes
  - [x] Create/replace/drop/rename/mode diffs with relation dependency ordering, rename-aware tables, destructive diagnostics, and default-`RESTRICT` drops
  - [x] Canonical `pg_rewrite`/`pg_get_ruledef` discovery with generated fluent C#, extension-owned exclusion, and ordinary view `_RETURN` de-duplication
  - [x] PostgreSQL 15–19 execution, lifecycle, reverse-engineering, and scaffolding acceptance
- [x] Logical-replication publications
  - [x] Typed table/schema/all-table membership, column lists, trusted row filters, DML selection, partition-root publishing, and empty publications
  - [x] PostgreSQL 18 generated-column publishing plus PostgreSQL 19 all-sequence and all-table exclusion metadata with explicit capability guards
  - [x] In-place membership/options changes, rename, destructive all-object mode transitions, relation dependency ordering, and default-`RESTRICT` drops
  - [x] Cross-version `pg_publication`/`pg_publication_rel`/`pg_publication_namespace` discovery, fluent C# scaffolding, and PostgreSQL 15–19 acceptance
- [x] Logical-replication subscriptions
  - [x] Typed connection-string, PostgreSQL 19 foreign-server, publication, slot, streaming, synchronous-commit, two-phase, origin, failover, error, owner, password-policy, and PostgreSQL 19 retention/receiver-timeout metadata
  - [x] Create/alter/drop/rename diffs plus explicit publication/sequence refresh and skipped-transaction operations, dependency ordering, destructive diagnostics, and transaction suppression where PostgreSQL requires it
  - [x] Cross-version `pg_subscription` discovery with PostgreSQL 15 boolean/16+ mode handling, credential-redacted database-first scaffolding, generated migration C#, and execution-time PostgreSQL 16/17/19 capability guards
  - [x] PostgreSQL 15–19 disconnected lifecycle, option alteration, rename, exact catalogue round-tripping, scaffolding, and PostgreSQL 19 foreign-server acceptance
- [x] Foreign-data wrappers, servers, user mappings, and foreign tables
  - [x] Typed wrapper handler/validator/PostgreSQL 19 connection functions, wrapper/server/mapping options, server type/version, and keyless foreign-table/column options
  - [x] Dependency-ordered create/alter/drop/rename diffs, generated migration C#, replacement diagnostics, PostgreSQL 19 capability guards, and default-`RESTRICT` removal
  - [x] Cross-version `pg_foreign_data_wrapper`/`pg_foreign_server`/`pg_user_mapping`/`pg_foreign_table` discovery, extension-owned wrapper exclusion, credential-redacted mappings, and fluent C# scaffolding
  - [x] PostgreSQL 15–19 lifecycle, option alteration, rename, exact catalogue round-tripping, foreign-table scaffolding, and PostgreSQL 19 connection-function acceptance
- [x] Table partitioning metadata and migrations
  - [x] Typed RANGE, single-key LIST, multi-key HASH, expression-key, default-partition, and recursive subpartition metadata
  - [x] Create/add/drop/rename/schema-move diffs with destructive bound-change and unsupported root-strategy diagnostics
  - [x] Manual attach and normal/concurrent/finalize detach operations with transaction suppression where PostgreSQL requires it
  - [x] Exact catalogue key/bound discovery, child-table de-duplication, snapshot retention, and generated fluent C#
  - [x] PostgreSQL 15–19 row-routing, lifecycle, reverse-engineering, and scaffolding acceptance
- [x] Row-level security policies
  - [x] Typed enable/force state plus permissive/restrictive policies for every PostgreSQL command scope and role-target form
  - [x] Create/alter/drop/rename/replacement diffs, trusted `USING`/`WITH CHECK` predicates, snapshots, and operation C# scaffolding
  - [x] `pg_class`/`pg_policies` reverse engineering with fluent model regeneration
  - [x] PostgreSQL 15–19 non-owner filtering, check enforcement, lifecycle, discovery, and scaffolding acceptance
- [x] Direct table inheritance
  - [x] Ordered multiple-parent metadata, typed entity-parent and explicit table-parent configuration, snapshots, and generated fluent C#
  - [x] Rename-aware add/remove/reorder diffs plus manual `INHERIT`/`NO INHERIT` migration operations
  - [x] `pg_inherits` direct-parent discovery without conflating declarative partitions
  - [x] PostgreSQL 15–19 inherited scans, `ONLY` behavior, lifecycle, discovery, and scaffolding acceptance
- [x] PostgreSQL collation schema objects
  - [x] Typed libc, ICU, and PostgreSQL 17+ built-in providers with locale, determinism, PostgreSQL 16+ ICU rules, and recorded version options
  - [x] Collation-first create, dependency-preserving rename/schema moves, explicit copy/refresh operations, replacement diagnostics, and default-`RESTRICT` drops
  - [x] Cross-version `pg_collation` discovery with system/extension exclusion, exact provider state, schema filters, and fluent model regeneration
  - [x] PostgreSQL 15–19 comparison, capability guard, lifecycle, reverse-engineering, and scaffolding acceptance
- [x] PostgreSQL extension installation lifecycle
  - [x] Typed install, version pin/update, schema relocation, dependency declaration, remove, snapshots, and generated migration C#
  - [x] Extension-first create and extension-last default-`RESTRICT` drop ordering around provider-owned schema objects
  - [x] `pg_extension`/`pg_depend` discovery with exact installed version, schema, dependency edges, schema filters, and fluent model regeneration
  - [x] PostgreSQL 15–19 install, relocation, lifecycle, reverse-engineering, and scaffolding acceptance
- [x] Enum, domain, composite, range, and multirange type creation and alteration
  - [x] Typed model metadata, snapshots, migration operations, generated C#, identifier/literal quoting, and trusted SQL-fragment boundaries
  - [x] Dependency-ordered create/drop, schema/name moves, enum add/rename, domain default/nullability/check lifecycle, and composite attribute lifecycle
  - [x] Explicit diagnostics for enum removal/reordering, domain base/collation replacement, and composite reordering/insertion, with staged guidance for rename-plus-alter changes
  - [x] `pg_type`/`pg_enum`/`pg_constraint`/`pg_attribute` discovery with extension and table-row-type exclusion plus fluent model regeneration
  - [x] PostgreSQL 15–19 enforcement, alteration, lifecycle, reverse-engineering, and scaffolding acceptance
  - [x] Typed custom range metadata for subtype, B-tree operator class, collation, canonical function, subtype-difference function, and paired multirange identity
  - [x] Dependency ordering through both range and multirange names, pair-aware rename/schema moves, explicit replacement diagnostics, and default-`RESTRICT` drops
  - [x] `pg_range` discovery with exact referenced-object identities, system/extension exclusion, schema filters, and fluent model regeneration
  - [x] PostgreSQL 15–19 range/multirange execution, lifecycle, reverse-engineering, and scaffolding acceptance
  - [x] Document the PostgreSQL shell-type boundary for canonical functions; provider-model routines do not synthesize the required shell/function/final-definition cycle
- [x] Functions and procedures
  - [x] Typed SQL/PLpgSQL builders for overloads, parameter modes/defaults, scalar/`SETOF` results, language, planner/null/parallel/security attributes, and local configuration
  - [x] Signature-aware create/replace/drop/rename operations, generated migration C#, snapshots, destructive diagnostics, and collision-safe initial `CREATE`
  - [x] UDT-first ordering plus relational dependency phases for string and SQL-standard tracked bodies
  - [x] `pg_proc` canonical discovery for normal/window functions and procedures with aggregate, system, and extension-owned exclusion
  - [x] PostgreSQL 15–19 overloaded execution, procedure lifecycle, replacement, reverse-engineering, and scaffolding acceptance
- [x] Views and materialised views
  - [x] Typed ordinary/recursive and materialised definitions with output columns, security/check options, dependencies, access method, storage parameters, tablespace, and population state
  - [x] Dependency-ordered create/drop, constrained ordinary replacement, rename/schema moves, materialised auxiliary alterations, and manual normal/concurrent refresh operations
  - [x] Destructive materialised-query replacement with transitive provider-owned dependent reconstruction and default-`RESTRICT` drops
  - [x] `pg_class`/`pg_rewrite`/`pg_depend`/`pg_get_viewdef` discovery with system/extension exclusion plus fluent model regeneration
  - [x] PostgreSQL 15–19 execution, check enforcement, concurrent refresh, lifecycle, reverse-engineering, and scaffolding acceptance
- [x] Product-spec-complete PostgreSQL migrations SQL generation
  - [x] Every core and PostgreSQL-specific schema object in product-spec section 19 has typed metadata, dependency-aware operations, SQL generation, and generated migration C# where applicable
  - [x] Full native EF project acceptance across PostgreSQL 15–19: 301 cases per server, with only the environment-gated tablespace test and the PostgreSQL 16 aggregate capability test skipped where inapplicable
- [x] Product-spec-complete PostgreSQL database reverse engineering and scaffolding
  - [x] Every discovery category in product-spec section 21 retains its PostgreSQL semantics through provider annotations and generated fluent C#
  - [x] Owners, privileges, and application-specific grants remain intentionally outside generated model metadata and require explicit security-reviewed migrations
- [x] Remaining product-spec schema objects
  - [x] Event triggers
    - [x] Typed DDL-start/end, SQL-drop, table-rewrite, and PostgreSQL 17+ login events with command-tag filters and firing modes
    - [x] Routine-last create, migration-first drop, body replacement, rename, enable/disable/replica/always operations, generated migration C#, and default-`RESTRICT` removal
    - [x] Direct `pg_event_trigger` discovery with function identity, tags, firing mode, extension exclusion, schema-filter behavior, and fluent C# scaffolding
    - [x] PostgreSQL 15–19 execution, lifecycle, catalogue round-tripping, scaffolding, and PostgreSQL 17+ login capability acceptance
  - [x] Subscriptions
  - [x] Foreign-data wrappers, servers, user mappings, and foreign tables
  - [x] Operators, operator classes, operator families, casts, and aggregates
    - [x] Typed unary/binary operator metadata, implementation/planner functions, commutator/negator links, and hash/merge flags
    - [x] Access-method-qualified operator families/classes with exact search/order strategies, support-function signatures, storage types, and loose-versus-class-owned members
    - [x] Function, binary-coercible, and input/output casts with explicit, assignment, and implicit contexts
    - [x] Ordinary, ordered-set, hypothetical-set, partial, serialised, moving-state, sort-operator, state-space, final-modify, and parallel aggregate metadata
    - [x] Dependency-ordered create/alter/replace/drop diffs, destructive diagnostics, generated migration C#, snapshots, and default-`RESTRICT` removal
    - [x] Direct `pg_operator`/`pg_opfamily`/`pg_opclass`/`pg_amop`/`pg_amproc`/`pg_cast`/`pg_aggregate` discovery with extension exclusion and fluent C# scaffolding
    - [x] PostgreSQL 15–19 lifecycle, precise family-member alteration, catalogue round-tripping, and database-first scaffolding acceptance
  - [x] Tablespace lifecycle
    - [x] Typed cluster-wide name/location/owner/options/comment metadata with model-builder and manual-migration APIs
    - [x] Transaction-suppressed create/drop, dependency-safe ordering, rename, owner/option/comment alteration, reset semantics, and immutable-location rejection
    - [x] Direct `pg_tablespace`/`pg_tablespace_location`/shared-comment discovery, built-in exclusion, full-database fluent scaffolding, and generated migration C#
    - [x] PostgreSQL 15–19 filesystem-backed lifecycle, table placement, catalogue round-tripping, scaffolding, and empty-cluster-drop acceptance
- [x] Idempotent scripts, history-table behaviour, and version-aware DDL
  - [x] Live-tested ordinary idempotent up/down scripts, including safe repeated execution
  - [x] Custom schema/table history repositories with quoted identifiers, locking, conditional guards, and live row lifecycle
  - [x] Fail-fast idempotent generation for transaction-suppressed PostgreSQL DDL that cannot execute inside conditional `DO` blocks
  - [x] Runtime server-version guards for version-dependent collations, generated columns, publications, subscriptions, foreign-data wrappers, event triggers, and SQL/PGQ DDL

## 0.5.0 — PostgreSQL 19 SQL/PGQ graph preview (Milestone 7)

- [x] Phase A: provider compatibility against PostgreSQL 19 Beta 2
  - [x] Populate and expose real server capabilities; remove unused capability-only claims
  - [x] Executable PostgreSQL 19 CI/integration job, beyond the compose profile
  - [x] Live `CREATE`/`ALTER`/`DROP PROPERTY GRAPH` and `GRAPH_TABLE` raw-SQL tests
  - [x] Parameters, metadata, preparation, batches, cancellation, pooling, and mixed relational/graph coverage
  - [x] PostgreSQL 15–18 regression gate remains green
- [x] Phase B: property-graph metadata and schema
  - [x] Graph, vertex, edge, key, label, and property model metadata
  - [x] PostgreSQL 19 `information_schema`/documented-catalogue discovery
  - [x] Capability-guarded migrations and reverse engineering
  - [x] Correct graph, label, property, and table identifier quoting
- [x] Phase C: EF query support
  - [x] Native typed graph query root and graph-pattern representation
  - [x] Parameterised `GRAPH_TABLE` translation
  - [x] Relational/graph composition, projections, filters, aliases, and materialisation
  - [x] Explicit unsupported-construct diagnostics with no unsafe string fallback
  - [x] SQL-generation unit tests and live PostgreSQL 19 acceptance tests
- [x] Phase D: sample and tooling
  - [x] Executable PostgreSQL 19 graph sample replaces the placeholder
  - [x] Schema tooling displays property graphs
  - [x] Supported SQL/PGQ subset and raw-SQL-only remainder documented

## Extension ecosystem (Milestone 8, pre-1.0)

- [x] PostGIS ADO.NET transport package (live-tested EWKB/WKT geometry, geography, arrays, and spatial execution)
- [x] PostGIS NetTopologySuite geometry model and EF spatial integration (geometry/geography typmods, arrays, structural snapshots, typed translations, focused diagnostics, migrations, compiled queries, and PostgreSQL 18/PostGIS 3.6 acceptance)
- [x] pgvector ADO.NET package (live-tested `vector`, `halfvec`, `sparsevec`, arrays, and vector/bit distances)
- [x] pgvector EF package (live-tested type mappings, arrays, dimensions, migrations, and vector/bit distance translations)
- [x] hstore ADO.NET package (live-tested binary/text values, arrays, and operators)
- [x] ltree ADO.NET package (live-tested `ltree`, `lquery`, `ltxtquery`, arrays, and operators)
- [x] citext packages (tested ADO.NET and EF preview; stable extension-authoring seam)
- [x] pg_trgm ADO.NET package (live-tested parameterized functions and operators, including quoted schemas)
- [x] TimescaleDB ADO.NET package (live-tested hypertable creation, approximate counts, retention, current Hypercore columnstore, and continuous-aggregate policy/refresh lifecycle)
- [x] TimescaleDB query helpers and EF integration (schema-qualified temporal/integer `time_bucket`, typed `first`/`last`/`histogram` aggregates with modifiers, migrations, compiled queries, and PostgreSQL 17/TimescaleDB 2.29 acceptance)
- [x] Extension-authoring template
- [x] Extension compatibility-testing kit

## 1.0.0 engineering gate (Milestone 9, complete)

This milestone tracks the repository's 1.0 engineering gates. It does not declare a
stable or production-endorsed release: published packages remain `0.3.0-preview.1`
while the provider accumulates external production experience and PostgreSQL 19
syntax remains beta-sensitive. The product-spec audit is complete: every
explicit environment and workload gate below is executable and checked.

- [x] Stable ADO.NET APIs
  - [x] Compiler-enforced shipped API/nullability baselines for Transport, Protocol, Security, Diagnostics, TypeSystem, Client, and Data
- [x] Stable extension APIs
  - [x] Compiler-enforced shipped API/nullability baselines for Extensions.Abstractions and Extensions.Testing after the citext vertical slice and compatibility harness
- [x] Full built-in PostgreSQL type support
  - [x] PostgreSQL 15–19 catalogue gate requires a codec for every queryable built-in base, range, and multirange type
- [x] Production-grade connection pooling
  - [x] Bounded per-host capacity, cancellable waiters, warm-up, lifetime/health rotation, credential refresh, reset, clear/drain, and disposal invariants
  - [x] PostgreSQL 15–19 acceptance, scheduled concurrency stress, metrics/statistics, and checkout benchmarks
- [x] Reliable cancellation
  - [x] Dedicated PostgreSQL cancellation channel with `ReadyForQuery` draining across commands, batches, readers, pipeline groups, COPY, notifications, and replication
  - [x] Sync/async token, timeout, explicit-cancel, transaction-state, connection-reuse, and cancellation-storm coverage
- [x] Production-ready `COPY`, notifications, large objects, and replication
  - [x] Bounded streaming, lifecycle/transaction ownership, abort/recovery, and PostgreSQL 15–19 acceptance for native data paths
  - [x] Replication API compatibility baselines, live version matrix, checkpoint/failure recovery, feedback safety, and scheduled endurance
- [x] Product-spec EF Core relational specification coverage
  - [x] Official service-registration and migrations SQL-generator suites (55 offline tests, no skips)
  - [x] Official live data-annotation, composite-key, field-mapping, constructor-materialization, and property-values fixtures (506 passed, four upstream EF skips)
  - [x] Official live updates, store-generated fixup, and complex-types tracking fixtures (391 passed, 50 upstream EF skips)
  - [x] Official live complex-type, ad-hoc complex-type, and structural-JSON query fixtures across PostgreSQL 15–19 (221 passed, two upstream EF skips per server)
  - [x] Official live migrations and database-model reverse-engineering fixture on PostgreSQL 19 (132 passed, two upstream EF skips)
  - [x] Official generic relational model-builder fixture (682 passed, 66 upstream EF skips) plus broad query, migrations, and scaffolding fixtures
  - [x] Complete PostgreSQL 19 official assembly gate: 2,111 discovered, 1,987 passed, and 124 upstream EF skips; the scope boundary is explicit and does not claim every Microsoft test base
- [x] PostgreSQL-specific EF support
  - [x] Native query-family matrix across PostgreSQL 15–19 covers operators, functions, aggregates, set-returning roots, query constructs, data modification, JSON, UDTs, and mappings
  - [x] Native 301-case provider project passes on every supported server with only capability- and environment-inapplicable skips
  - [x] Product-spec-complete migrations, reverse engineering, generated C#, database lifecycle, extension integration, and capability-guarded PostgreSQL 19 SQL/PGQ
- [x] Security review
  - [x] Repository threat model and control review covering credentials, TLS, authentication, protocol bounds, pooling, cancellation, SQL trust boundaries, diagnostics, and supply chain
  - [x] Secure-by-default `Persist Security Info=false` behavior for connection and data-source public properties
  - [x] Direct/transitive, all-severity NuGet audit enforced by restore with no advisory suppressions
  - [x] SCRAM channel binding, legacy MD5 compatibility, gated cleartext passwords, secret redaction, and overwrite-on-flush authentication buffers
  - [x] PostgreSQL password-file (`pgpass`) resolution and password callbacks
  - [x] Access-token callbacks with per-new-physical-connection refresh lifecycle
  - [x] TLS client-certificate authentication and certificate-selection callbacks
  - [x] PostgreSQL 18+ native OAUTHBEARER with TLS enforcement, ready-token callbacks, sensitive buffers, error acknowledgement, and real validator acceptance
  - [x] GSSAPI/Kerberos and SSPI with platform security contexts, sensitive binary-token handling, mutual authentication, and real KDC acceptance
  - [x] Optional AWS RDS/Aurora, Azure Database for PostgreSQL, and Google Cloud SQL identity adapters with SDK-native credential chains, TLS-before-token enforcement, per-physical-connection refresh, deterministic contract tests, and opt-in account acceptance
- [x] Stress testing
  - [x] PostgreSQL 15–19 pool churn, cancellation, preparation, batch, sequential-reader, pipeline, and replication lifecycle stress
  - [x] Scheduled/manual PostgreSQL 19 elevated-concurrency provider stress plus separate 1,000-epoch replication endurance
- [x] Competitive benchmarks
  - [x] Equivalent live BlueTusk/Npgsql warm-pool, parameterized, prepared, 1,000-row, and 1 MiB streaming workloads
  - [x] Checked-in PostgreSQL 19 MediumRun latency/allocation baseline with explicit fairness limits and no runtime Npgsql dependency
  - [x] Eliminate sequential-reader portal-suspension round trips, reuse streamed row state, and add buffered typed-scalar fast paths with cancellation/recovery acceptance
  - [x] Record lower mean latency and managed allocation than Npgsql on all five paired workloads, with non-overlapping latency intervals for parameterized scalar, warm checkout, and 1,000-row streaming
- [x] Complete documentation
  - [x] Data-source-first ADO.NET, EF, replication, extension, graph, security, observability, testing, versioning, and release-readiness guides match executable behavior
  - [x] Cross-platform CI gate validates every repository-local link across all tracked Markdown files
  - [x] Preview claims retain explicit production, optional-credential, raw-SQL, ownership/grant, and PostgreSQL 19 beta boundaries
- [x] Supported-version CI
- [x] Dedicated extension-image CI for pgvector, PostGIS, and TimescaleDB ADO.NET/EF gates
- [x] PgBouncer session- and transaction-pooling CI with live prepared/transaction acceptance
- [x] `en_GB`/London and `de_DE`/New York locale/time-zone CI with money and timestamp acceptance
- [x] Real primary/standby topology with target-session fallback, WAL replay, and read-only acceptance
- [x] Scheduled full-solution acceptance against the checksummed official PostgreSQL 19 nightly branch snapshot
- [x] Representative EF query compilation/materialisation/write and raw/typed SQL/PGQ traversal benchmarks with checked-in ShortRun allocation baselines

Every milestone requires unit, fake-server, conformance, and real-server acceptance coverage appropriate to its surface before the corresponding version is published. The 1.0 gate additionally requires the full PostgreSQL version matrix, differential testing, security review, stress testing, performance baselines, and complete documentation described by the product specification.
