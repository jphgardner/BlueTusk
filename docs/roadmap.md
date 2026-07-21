# Roadmap

This file tracks executable repository status. The product vision is broader; unchecked work is not implied by package names already present in the solution.

## 0.0.1 — Foundation (in progress)

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
- [ ] Protocol packet-inspection file format
- [ ] BenchmarkDotNet harness and checked-in baselines

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

## 0.0.5 — Connection pooling and data sources

- [ ] Bounded per-data-source physical connection pool
- [ ] Minimum/maximum size, idle lifetime, and maximum lifetime
- [ ] Safe session reset and health validation
- [ ] Waiter cancellation and pool draining
- [ ] Pool diagnostics and live concurrency tests

## 0.0.6 — Core binary type codecs

- [ ] Complete scalar built-in codec registry
- [ ] Binary result negotiation and decoding
- [ ] Streaming text, `bytea`, and JSON values
- [ ] Date/time infinity and numeric edge cases

## 0.0.7 — Dynamic and structured types

- [ ] Catalogue discovery and cache
- [ ] Arrays, enums, domains, and composites
- [ ] Ranges and multiranges
- [ ] Public runtime codec registration

## 0.0.8 — COPY and notifications

- [ ] Binary, text, CSV, and raw COPY APIs
- [ ] `LISTEN`/`NOTIFY` asynchronous delivery
- [ ] Large-object streams

## 0.0.9 — Replication preview

- [ ] Physical and logical replication sessions
- [ ] Slot/publication discovery and feedback
- [ ] `pgoutput` decoding

## 0.1.0 — First public ADO.NET preview

- [ ] Prepared statements, batches, and multi-host connection attempts
- [ ] Genuine synchronous connection and query paths
- [ ] Buffered, sequential, and streaming reader modes
- [ ] ADO.NET conformance, stress, differential, and performance baselines

Later milestones follow the full product specification: EF Core CRUD and migrations, PostgreSQL-native LINQ, advanced schema modelling and scaffolding, SQL/PGQ property graphs, and the extension ecosystem. Each milestone must have conformance and real-server acceptance tests before its version is published.
