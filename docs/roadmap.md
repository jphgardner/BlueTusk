# Roadmap

This file tracks executable repository status. The product vision is broader; unchecked work is not implied by package names already present in the solution.

## 0.0.1 — Foundation (complete)

- [x] Repository and package structure
- [x] Shared SDK, formatting, analyzer, and CI rules
- [x] Architecture and compatibility decisions
- [x] PostgreSQL 15–18 and 19 beta Docker environments
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

## 0.2.0 — EF Core CRUD preview (Milestone 5)

- [ ] `UseBlueTusk` options and provider-service registration
- [ ] EF Core create, read, update, and delete operations
- [ ] EF Core transaction and savepoint integration
- [ ] Store-generated keys, defaults, computed values, and concurrency tokens
- [ ] Core relational type mappings and SQL generation
- [ ] Core LINQ translation, query execution, and result materialisation
- [ ] Initial migrations SQL generation and history repository
- [ ] Initial database reverse engineering
- [ ] Initial EF Core relational specification-suite coverage

## 0.3.0 — PostgreSQL-specific EF translations (Milestone 6, query surface)

- [ ] Complete PostgreSQL type mappings
- [ ] PostgreSQL operators and operator-aware LINQ translations
- [ ] PostgreSQL scalar, aggregate, and set-returning function translations
- [ ] PostgreSQL-specific query roots and SQL constructs
- [ ] Enum, domain, composite, range, multirange, array, JSON, network, geometric, and full-text query support
- [ ] PostgreSQL-specific query diagnostics and translation tests

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

## 0.5.0 — SQL/PGQ graph preview (Milestone 7)

- [ ] Property-graph model metadata
- [ ] Property-graph migrations
- [ ] Native graph query root
- [ ] Typed graph-pattern API
- [ ] `GRAPH_TABLE` translation
- [ ] Relational and graph query composition
- [ ] Property-graph reverse engineering and scaffolding
- [ ] Graph-specific diagnostics

## Extension ecosystem (Milestone 8, pre-1.0)

- [ ] PostGIS package
- [ ] pgvector package
- [ ] hstore package
- [ ] ltree package
- [ ] citext package
- [ ] pg_trgm package
- [ ] TimescaleDB package
- [ ] Extension-authoring template
- [ ] Extension compatibility-testing kit

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
- [ ] Supported-version CI

Every milestone requires unit, fake-server, conformance, and real-server acceptance coverage appropriate to its surface before the corresponding version is published. The 1.0 gate additionally requires the full PostgreSQL version matrix, differential testing, security review, stress testing, performance baselines, and complete documentation described by the product specification.
