# BlueTusk

**PostgreSQL, fully exposed to .NET.**

BlueTusk is a ground-up PostgreSQL provider ecosystem for .NET. Its long-term scope includes a native wire-protocol engine, ADO.NET, replication, Entity Framework Core, extension packages, and PostgreSQL SQL/PGQ support—without a runtime dependency on Npgsql.

> [!IMPORTANT]
> BlueTusk is an experimental `0.3.0-preview.1` provider, not a production-ready database driver. Executable tests currently cover pooled ADO.NET queries, prepared statements, batches, streaming APIs, PostgreSQL-native types, Client-layer PostgreSQL pipeline mode, a `citext` codec and separately packaged EF integration preview plus authoring template and compatibility harness, replication preview APIs, EF Core CRUD, initial migrations/scaffolding, PostgreSQL-native EF mappings plus typed native operators including quantified and row-value comparisons, scalar-function, aggregate-function, lateral array-expansion, typed `generate_series`, JSONB set-returning and model-derived recordset roots, initial multi-array `unnest` subsets, and model-registered user-defined table functions, and PostgreSQL 19 SQL/PGQ raw SQL, schema discovery/tooling, graph-aware migrations/reverse engineering, and a typed linear-path EF query subset. The measured transport evaluation retains ArrayPool/Span/Memory rather than adding `System.IO.Pipelines` to production packages. Stable extension APIs and the full production gate remain planned. Track exact implemented and pending scope in the [roadmap](docs/roadmap.md).

## Build

Prerequisites:

- .NET SDK 10.0.110 or a compatible later feature band
- Docker, only for PostgreSQL integration tests

```powershell
dotnet restore BlueTusk.slnx
dotnet build BlueTusk.slnx --no-restore
dotnet test BlueTusk.slnx --no-build
```

Integration tests are opt-in. Start one of the test databases and set `BLUETUSK_TEST_CONNECTION_STRING` before running the integration suite.

```powershell
docker compose -f eng/compose/postgres.yml up -d postgres18
$env:BLUETUSK_TEST_CONNECTION_STRING = "Host=localhost;Port=5418;Username=postgres;Password=postgres;Database=bluetusk_tests"
dotnet test tests/BlueTusk.IntegrationTests
```

## Architecture

The dependency direction is deliberately one-way:

```text
EntityFrameworkCore → Data → Client → Protocol → Transport
                              ↓          ↓
                          TypeSystem   Security

Replication.PgOutput → Replication → Client
```

See [Architecture](docs/architecture/overview.md), [ADRs](docs/architecture/decisions), [type mappings](docs/types/README.md), [extension SDK](docs/extensions/README.md), [replication](docs/replication/README.md), [PostgreSQL 19 SQL/PGQ](docs/graph/README.md), [protocol captures](docs/protocol/capture-format.md), [benchmarks](benchmarks/README.md), and [Contributing](CONTRIBUTING.md).

## Status

The current `0.3.0-preview.1` implementation provides:

- the complete repository/package layout;
- shared build, formatting, analyzer, and CI configuration;
- TCP endpoint and transport abstractions;
- PostgreSQL backend-frame parsing and startup/query message writing;
- an explicit protocol connection state machine;
- catalogue-friendly type descriptors and unknown-value preservation;
- text and binary codecs for core scalar boolean, integer, floating-point, numeric, character, binary, UUID, temporal, JSON, and XML values;
- advanced temporal, bit-string, transaction, object-identifier, network, geometric, money, full-text, JSONPath, and system-catalogue values;
- per-data-source catalogue discovery with explicit reload and unknown-value preservation;
- catalogue-composed arrays, enums, domains, named and anonymous records, ranges, and multiranges;
- convention- and attribute-based CLR enum and composite mappings plus public runtime codec registration;
- arbitrary-precision PostgreSQL `numeric`, including NaN and infinities, plus temporal infinity and 24:00 handling;
- security redaction and observability primitives;
- a fake backend message stream for conformance testing;
- a Docker-based PostgreSQL version matrix;
- a versioned, bounded protocol-capture format and redaction-aware inspector;
- executable BenchmarkDotNet protocol/type workloads with checked-in reference baselines;
- TLS negotiation with safe platform certificate validation by default;
- SCRAM-SHA-256 and SCRAM-SHA-256-PLUS authentication;
- startup metadata, structured errors/notices, and backend key data;
- buffered simple-query execution with multiple results;
- extended-query execution through Parse, Bind, Describe, Execute, and Sync;
- typed binary and text parameter encoding without SQL interpolation;
- binary result negotiation for extended queries and registry-driven field decoding;
- buffer-backed stream and text-reader accessors for `bytea`, text, and JSON values;
- ADO.NET transactions with PostgreSQL isolation levels, commit, rollback, and rollback-on-disposal;
- [PostgreSQL pipeline mode](docs/pipeline-mode.md) with explicit synchronization groups, ordered results, cancellation draining, and safe session reuse;
- cancellation tokens, command timeouts, and explicit sync/async cancellation over PostgreSQL's dedicated channel;
- bounded per-data-source connection pooling with cancellable waiters;
- transaction rollback, `DISCARD ALL` session reset, health validation, and connection lifetime enforcement;
- pool warm-up, clear/drain controls, statistics, metrics, live concurrency tests, and a checkout benchmark;
- streaming raw text, CSV, and binary COPY plus typed binary import and export;
- asynchronous `LISTEN`/`NOTIFY` delivery with quoted subscriptions and bounded backpressure;
- transactional large-object creation, deletion, streaming, 64-bit seek, and truncation;
- physical and logical `COPY BOTH` replication sessions with WAL and keepalive framing;
- replication-slot and publication discovery plus standby and hot-standby feedback;
- monotonic feedback, exact pgoutput transaction checkpoints, and guarded persistent-slot resume validation;
- protocol-version-aware `pgoutput` decoding for DML, streamed transactions, and two-phase metadata;
- raw logical decoding output for custom plugins;
- initial `BlueTuskConnection`, `BlueTuskCommand`, `BlueTuskDataReader`, and `BlueTuskDataSource` APIs.
- explicit and automatic prepared statements, `DbBatch`, named parameters, and multi-host pools;
- EF Core CRUD, transactions, generated values, core LINQ, typed PostgreSQL
  operator predicates including `ANY`/`ALL` and row-value comparisons, scalar functions, initial native aggregates, lateral array expansion, typed `generate_series`, scalar/key-value/model-derived JSONB roots, typed integer/text two-array `unnest`, model-registered table-valued functions, initial migrations, and reverse engineering;
- PostgreSQL-native EF scalar, array, range, multirange, enum, domain, composite, and record mappings.
- an immutable data-source feature registry plus independently packaged, live-tested `citext` ADO.NET and EF previews.
- a packaged extension-authoring template and framework-neutral live compatibility harness.
- catalogue-probed PostgreSQL 19 SQL/PGQ capability detection, live raw-SQL property-graph coverage, typed information-schema discovery, text/JSON schema tooling, capability-guarded EF migrations/reverse engineering, and typed composable EF linear-path queries.
- a benchmark-backed decision to retain the genuine sync/async ArrayPool/Span/Memory transport.

Applications should build one long-lived data source per distinct configuration. It owns pooling, runtime codecs, and the PostgreSQL type catalogue:

```csharp
await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();
await using var command = dataSource.CreateCommand("SELECT $1::int4 + $2::int4");
command.Parameters.Add(new BlueTuskParameter<int>(20));
command.Parameters.Add(new BlueTuskParameter<int>(22));

var answer = await command.ExecuteScalarAsync<int>();
```

Directly constructing `BlueTuskConnection` is supported for compatibility and dedicated ownership scenarios, but those connections are unpooled. Replication uses separate, dedicated unpooled sessions; derive their connection options from the long-lived data source so authentication and transport settings stay aligned without borrowing from its pool.

## License

BlueTusk is licensed under the [MIT License](LICENSE).
