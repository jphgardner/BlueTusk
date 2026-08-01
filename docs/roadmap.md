# Roadmap

This file tracks executable repository status. The product vision is broader; unchecked work is not implied by package names already present in the solution.

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
  - [x] Common relational create-table, primary-key, foreign-key, index, default, and facet DDL
  - [x] PostgreSQL identity-column DDL and live schema-creation coverage
  - [x] Migration history repository, transactional locking, and idempotent migration scripts
  - [x] Live apply/revert coverage for alter, rename, sequence, index, and drop operations
- [x] Initial database reverse engineering
  - [x] EF design-time provider discovery and `UseBlueTusk` context generation
  - [x] Tables, views, columns, defaults, generated values, keys, foreign keys, indexes, and sequences
  - [x] Schema/table filtering, comments, store-type facets, and connection-ownership coverage
  - [x] Catalogue-only sequence discovery safe during concurrent schema changes
- [x] Initial EF Core relational specification-suite coverage
  - [x] Provider-service lifetime and isolation contracts
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

### Allocation discipline

- [x] Retain the span-based `BlueTuskReader`/`BlueTuskWriter` codec model
- [x] Profile complete parameter and result paths
  - [x] Per-command writers, per-parameter arrays, boxing, and text transcoding
  - [x] Structured codecs, COPY field buffers, and large-field materialisation
- [x] Introduce safe per-session reuse, pooling, sizing passes, or direct `IBufferWriter` encoding where measurements justify them
- [x] Check in end-to-end allocation baselines and explicit regression budgets
- [x] Describe inherently allocating returned CLR values accurately; no blanket “allocation-free” claim

### Enforced package boundaries and provider contract

- [x] Automated conformance test rejects reverse BlueTusk references and ADO.NET/EF leakage into lower layers
- [x] Remove the unused Client → Extensions.Abstractions reference
- [x] Document the narrow Data surface consumed by EF: data-source connection creation, parameter store-type identity, runtime type resolution, capabilities, and diagnostics
- [x] Keep protocol parsing independently testable without ADO.NET or EF
- [x] Run the architecture gate in every supported CI environment

### Extension seam

- [x] Carry an immutable feature registry through `BlueTuskDataSource.Build()` and expose real consumption semantics
- [x] Complete a `citext` vertical slice without extension-specific core dependencies
  - [x] Codec/type registration and data-source-builder ergonomics
  - [x] ADO.NET live tests, documentation, and package metadata
  - [x] Separate EF type-mapping/query/migration plug-in with scalar and array coverage
- [x] Extension-authoring template
- [x] Extension compatibility-test harness
- [ ] Stabilise extension APIs only after the vertical slice and compatibility gate

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

### Release truthfulness

- [x] Reconcile package version, README, roadmap, and implemented prepared/batch/EF scope
- [x] Label implemented, tested preview, production-ready, and planned capabilities distinctly
- [ ] Re-audit all public claims at each preview and 1.0 release gate

## 0.3.0 — PostgreSQL-specific EF translations (Milestone 6, query surface)

- [x] Complete PostgreSQL type mappings
  - [x] Built-in wire-native scalar mappings with exact parameter OIDs
  - [x] Network, geometric, bit-string, money, arbitrary numeric, full-text, JSON path, system identifier, transaction, and catalogue CLR values
  - [x] Explicit `json`, `jsonb`, `xml`, `cidr`, `bit`, and legacy snapshot store-type selection
  - [x] Built-in one- and multidimensional arrays with structural change tracking and exact element-family OIDs
  - [x] All six built-in range and multirange families, including their array types
  - [x] Runtime-registered enums, domains, composites, records, and their arrays
- [ ] PostgreSQL operators and operator-aware LINQ translations
  - [x] Parameterised `ILIKE` and POSIX regular-expression predicates
  - [x] Array, range/multirange, JSONB/JSONPath, network, and full-text predicates
  - [x] SQL-generation and PostgreSQL 15–19 live operator acceptance
  - [x] Typed comparison and pattern `ANY`/`ALL` over PostgreSQL array parameters
  - [x] Equal-arity row values and tuple comparisons across all six B-tree operators
  - [ ] Remaining operator forms
- [ ] PostgreSQL scalar, aggregate, and set-returning function translations
  - [x] Initial array, range/multirange, JSONB, regex, network, and full-text scalar functions
  - [x] Composable nested functions with typed result materialisation and PostgreSQL 15–19 acceptance
  - [x] Initial array, string, boolean, range-union, and range-intersection aggregates
  - [x] Aggregate ordering, `DISTINCT`, `FILTER`, typed results, and PostgreSQL 15–19 acceptance
  - [ ] Remaining scalar, JSON/statistical/ordered-set, and other PostgreSQL aggregate functions
  - [ ] Set-returning functions and lateral query roots
    - [x] Mapped-array `unnest` roots with ordinality, nullable elements, parameterized filters, and inner/outer lateral composition
    - [x] SQL-generation and PostgreSQL 15–19 live array-expansion acceptance
    - [x] Typed integer and bigint `generate_series` roots with parameterized standalone, correlated lateral, and compiled-query execution
    - [x] Typed numeric, timestamp, and timestamp-with-time-zone `generate_series` roots with exact argument mappings
    - [ ] JSON/recordset functions, multi-argument `unnest`, and user-defined table functions
- [ ] PostgreSQL-specific query roots and SQL constructs
- [ ] Enum, domain, composite, range, multirange, array, JSON, network, geometric, and full-text query support
  - [x] Array predicates, scalar functions, aggregates, lateral element expansion, and typed series roots
  - [ ] Remaining array forms and other PostgreSQL-native query families
- [ ] PostgreSQL-specific query diagnostics and translation tests
  - [x] Translation-only operator API with no client implementation or raw-string fallback
  - [x] Provider SQL-expression quoting, nullability processing, and operator-family tests

## 0.4.0 — Advanced migrations and scaffolding (Milestone 6, schema surface)

- [ ] Advanced indexes, operator classes, collations, storage parameters, and included columns
- [ ] Table partitioning metadata and migrations
- [ ] Row-level security policies
- [ ] Enum, domain, and composite type creation and alteration
- [ ] Functions and procedures
- [ ] Views and materialised views
- [ ] PostgreSQL-complete migrations SQL generation
- [ ] PostgreSQL-complete database reverse engineering and scaffolding
- [ ] Idempotent scripts, history-table behaviour, and version-aware DDL

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

- [ ] PostGIS package
- [ ] pgvector package
- [ ] hstore package
- [ ] ltree package
- [x] citext packages (tested ADO.NET and EF preview; extension SDK remains unstable)
- [ ] pg_trgm package
- [ ] TimescaleDB package
- [x] Extension-authoring template
- [x] Extension compatibility-testing kit

## 1.0.0 — Production-ready BlueTusk platform (Milestone 9)

- [ ] Stable ADO.NET APIs
- [ ] Stable extension APIs
- [ ] Full built-in PostgreSQL type support
- [ ] Production-grade connection pooling
- [ ] Reliable cancellation
- [ ] Production-ready `COPY`, notifications, large objects, and replication
- [ ] EF Core relational specification coverage
- [ ] PostgreSQL-specific EF support
- [ ] Security review
- [ ] Stress testing
- [ ] Competitive benchmarks
- [ ] Complete documentation
- [x] Supported-version CI

Every milestone requires unit, fake-server, conformance, and real-server acceptance coverage appropriate to its surface before the corresponding version is published. The 1.0 gate additionally requires the full PostgreSQL version matrix, differential testing, security review, stress testing, performance baselines, and complete documentation described by the product specification.
