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
- [ ] Cancellation channel (scheduled for 0.0.4)
- [x] Error and notice field parsing
- [x] Simple query operation
- [x] Initial ADO.NET connection, command, reader, and unpooled data source

## 0.0.3 — Extended queries and parameters (complete)

- [x] Parse/Bind/Describe/Execute/Sync writers
- [x] RowDescription and DataRow parsing
- [x] Typed parameter encoding
- [x] Multiple results
- [x] End-to-end `SELECT $1::int4 + $2::int4`

Later milestones follow the product plan: transactions/cancellation, pooling, the complete type system, COPY/notifications, replication, EF Core, SQL/PGQ, and extensions. Each milestone must have conformance and real-server acceptance tests before its version is published.
