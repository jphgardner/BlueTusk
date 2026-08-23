# BlueTusk

**PostgreSQL, fully exposed to .NET.**

BlueTusk is a ground-up PostgreSQL platform for .NET. Its scope includes a
native wire-protocol engine, ADO.NET, replication, Entity Framework Core,
extension packages, PostgreSQL SQL/PGQ support, and an independently versioned
[real-time application platform](docs/realtime-platform/README.md)—without a
runtime dependency on Npgsql.

> [!IMPORTANT]
> BlueTusk `1.0.0` was published on 2026-08-23 as six product families: 62
> NuGet packages and three npm packages. The repository owner authorised an
> exception for the incomplete independent approval, exact-candidate fuzz,
> reference-performance, endurance, and PostgreSQL 19 GA evidence; those gates
> are not represented as passed. The strict release workflow was restored
> immediately after publication. Start with the
> [V1 publication record](docs/releases/1.0.0-publication-record.md),
> [documentation handbook](docs/README.md), [V1 readiness record](docs/v1-release-readiness.md),
> [roadmap](docs/roadmap.md), and [support matrix](VERSIONING.md).

## Build

Prerequisites:

- .NET SDK 10.0.111 or a compatible later feature band
- Docker, only for PostgreSQL integration tests

```powershell
dotnet restore BlueTusk.slnx
dotnet build BlueTusk.slnx --no-restore
dotnet test BlueTusk.slnx --no-build
```

The root solution is grouped by product and role rather than physical folder.
See [repository and solution layout](docs/contributing/repository-layout.md) for
navigation, project-registration rules, and safe generated-output cleanup.

The solution includes BlueTusk's native xUnit v3 tests and a separate xUnit v2
assembly that consumes Microsoft's official EF Core relational specification
package. See the [EF specification-test coverage](docs/ef-core/specification-tests.md)
for the exact adopted suites, counts, upstream skips, and scope boundary.

Integration tests are opt-in. Start one of the test databases and set `BLUETUSK_TEST_CONNECTION_STRING` before running the integration suite.

```powershell
docker compose -f eng/compose/postgres.yml up -d postgres18
$env:BLUETUSK_TEST_CONNECTION_STRING = "Host=localhost;Port=5418;Username=postgres;Password=postgres;Database=bluetusk_tests"
dotnet test tests/BlueTusk.IntegrationTests
```

The extension profile has opt-in dedicated services and live gates. pgvector
and the bundled-contrib packages use PostgreSQL 18:

```powershell
docker compose -f eng/compose/postgres.yml --profile extension-tests up -d pgvector18
$env:BLUETUSK_TEST_CONNECTION_STRING = "Host=localhost;Port=5518;Username=postgres;Password=postgres;Database=bluetusk_tests"
dotnet test tests/BlueTusk.Extensions.PgVector.Tests
dotnet test tests/BlueTusk.Extensions.PgVector.EntityFrameworkCore.Tests
dotnet test tests/BlueTusk.Extensions.HStore.Tests
dotnet test tests/BlueTusk.Extensions.LTree.Tests
dotnet test tests/BlueTusk.Extensions.PgTrgm.Tests
```

PostGIS uses its official PostgreSQL 18/PostGIS 3.6 image:

```powershell
docker compose -f eng/compose/postgres.yml --profile extension-tests up -d postgis18
$env:BLUETUSK_TEST_CONNECTION_STRING = "Host=localhost;Port=5519;Username=postgres;Password=postgres;Database=bluetusk_tests"
dotnet test tests/BlueTusk.Extensions.PostGIS.Tests
dotnet test tests/BlueTusk.Extensions.PostGIS.EntityFrameworkCore.Tests
```

TimescaleDB uses its PostgreSQL 17 image and a separate live gate:

```powershell
docker compose -f eng/compose/postgres.yml --profile extension-tests up -d timescaledb17
$env:BLUETUSK_TEST_CONNECTION_STRING = "Host=localhost;Port=5520;Username=postgres;Password=postgres;Database=bluetusk_tests"
dotnet test tests/BlueTusk.Extensions.TimescaleDB.Tests
dotnet test tests/BlueTusk.Extensions.TimescaleDB.EntityFrameworkCore.Tests
```

The non-packable `pg_durable` preview adapter uses Microsoft's official
PostgreSQL 17 evaluation image and runs in the extension's required `postgres`
database. This is compatibility testing, not production deployment guidance:

```powershell
docker compose -f eng/compose/postgres.yml --profile extension-tests up -d pgdurable17
$env:BLUETUSK_TEST_CONNECTION_STRING = "Host=localhost;Port=5521;Username=postgres;Password=postgres;Database=postgres"
dotnet test tests/BlueTusk.Extensions.PgDurable.Tests
```

## Architecture

The dependency direction is deliberately one-way:

```text
EntityFrameworkCore → Data → Client → Protocol → Transport
                              ↓          ↓
                          TypeSystem   Security

Replication.PgOutput → Replication → Client
```

See [Architecture](docs/architecture/overview.md), [ADRs](docs/architecture/decisions), [API compatibility](docs/api-compatibility.md), [runtime release readiness](docs/release-readiness.md), [release process](docs/release-process.md), [type mappings](docs/types/README.md), [extension SDK](docs/extensions/README.md), [replication](docs/replication/README.md), [diagnostics and observability](docs/observability.md), [security review](docs/security.md), [PostgreSQL 19 SQL/PGQ](docs/graph/README.md), [protocol captures](docs/protocol/capture-format.md), [benchmarks](benchmarks/README.md), and [Contributing](CONTRIBUTING.md).

The real-time platform is delivered in independently gated Streams, Sync, Live,
Control Plane, and Continuous Graph release trains. Their V1 code, tests,
package manifests and evidence verifiers are implemented. The remaining release
work is deliberately operational: merge the final arming PR after PostgreSQL 19
GA, archive the exact 72-hour Streams, 24-hour Sync, and 24-hour
ContinuousGraph candidate runs, complete the content-addressed disturbance
recoveries and external acceptance window, and obtain independent sign-off.
Package names and successful local builds are not claims of public availability.

Three package-consumer applications now exercise the release train end to end:
Order Fulfilment Operations, Service Topology Centre, and Fraud Graph
Investigator. Their exact-RC architecture, application workflows, PostgreSQL
19 staging boundary, local verification, Kubernetes topology, and stable-pilot
promotion rules are recorded in the
[V1 application suite](docs/v1-applications.md). RC application evidence is
deliberately separate from immutable stable-candidate and pilot evidence.

Read the [platform contracts](docs/realtime-platform/contracts.md),
[real-time operations guide](docs/realtime-platform/operations.md), and
[release process](docs/release-process.md) before designing a production
topology.

## Status

The published `1.0.0` implementation provides:

- the complete repository/package layout;
- shared build, formatting, analyzer, and CI configuration;
- a hash-locked V1 API/nullability baseline for every publishable Provider library, including EF Core, extensions, and identity adapters;
- TCP and Unix-domain transports with deterministic DNS/address fallback, total connect
  deadlines, cancellation, TCP keepalive, bounded socket buffers, and classified connection
  failures;
- PostgreSQL backend-frame parsing and startup/query message writing;
- an explicit protocol connection state machine;
- catalogue-friendly type descriptors and unknown-value preservation;
- text and binary codecs for core scalar boolean, integer, floating-point, numeric, character, binary, UUID, temporal, JSON, and XML values;
- advanced temporal, bit-string, transaction, object-identifier (including PostgreSQL 19 `oid8` and `regdatabase`), network, geometric, money, full-text, JSONPath, and system-catalogue values;
- per-data-source catalogue discovery with explicit reload and unknown-value preservation;
- catalogue-composed arrays, enums, domains, named and anonymous records, ranges, and multiranges;
- convention- and attribute-based CLR enum and composite mappings, optional source-generated
  composite member access/construction, and public runtime codec registration;
- arbitrary-precision PostgreSQL `numeric`, including NaN and infinities, plus temporal infinity and 24:00 handling;
- security redaction and observability primitives;
- OpenTelemetry-compatible connection/command activities and metrics, redacted slow-command events, prepared-statement/retry/failover metrics, query tags, COPY throughput, and replication lag;
- a fake backend message stream for conformance testing;
- a Docker-based PostgreSQL version matrix;
- a versioned, bounded protocol-capture format and redaction-aware inspector;
- executable BenchmarkDotNet protocol/type workloads with checked-in reference baselines,
  including equivalent live PostgreSQL 19 BlueTusk/Npgsql hot-path comparisons;
- PostgreSQL 15–19 live stress coverage plus scheduled elevated-concurrency provider and replication-endurance gates;
- TLS negotiation with safe platform certificate validation by default;
- secure-by-default `Persist Security Info=false` connection/data-source properties and enforced direct/transitive NuGet vulnerability auditing;
- SCRAM-SHA-256 and SCRAM-SHA-256-PLUS authentication, PostgreSQL 18+ native OAUTHBEARER, GSSAPI/Kerberos and SSPI with mutual authentication and a live KDC gate, PostgreSQL password files, per-physical-connection password/access-token callbacks, TLS client certificates, and PostgreSQL 15–19-tested legacy MD5 and gated cleartext compatibility;
- optional AWS RDS/Aurora, Azure Database for PostgreSQL, and Google Cloud SQL identity packages with TLS-enforced per-physical-connection token acquisition;
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
- EF Core CRUD, transactions, generated values, core LINQ, physical database lifecycle, table CHECK and exclusion constraints, advanced column/expression PostgreSQL indexes, table/view/event-trigger, rewrite-rule, logical-publication/subscription, foreign-data-wrapper/server/user-mapping/foreign-table, tablespace, operator/operator-family/operator-class/cast/aggregate, declarative partition, row-level-security, direct table-inheritance, collation, installed-extension, enum/domain/composite/range/multirange-type, function/procedure, and ordinary/materialised-view migrations/scaffolding, typed PostgreSQL
  operator translations including `ANY`/`ALL`, row-value comparisons, array/range/multirange algebra, JSONB extraction/mutation, full-text composition, network arithmetic, bit strings, and complete built-in geometric forms, typed array/string/bytea/numeric/formatting/range/JSONB/regex/network/full-text/date-time/geometric scalar functions, complete built-in PostgreSQL aggregate families (including PostgreSQL 16 strict/unique variants), multidimensional array construction/subscripts/slices, lateral array element/subscript expansion, typed `generate_series`, scalar/key-value/model-derived JSONB roots, typed two- through four-array `unnest`, regex/delimiter table roots, runtime enum/domain predicates, catalogue-resolved nested composite/lossless-record field access, ordered `DISTINCT ON`, `TABLESAMPLE`, row locking, ranking/value window projections, recursive/materialized CTEs, `RETURNING`, `ON CONFLICT`, single-row `MERGE`, typed system columns with `xmin` concurrency, model-registered table-valued functions, initial migrations, and reverse engineering;
- PostgreSQL-native EF scalar, array, range, multirange, enum, domain, composite, and record mappings.
- readable fluent `CreatePropertyGraph` migrations with validated vertex,
  edge, label, property, key, source, and destination builders; generated
  migrations no longer embed serialized graph metadata strings.
- a packaged `bluetusk scaffold` database-first tool with schema/table filters,
  PostgreSQL-specific metadata retention, and secure-by-default connection handling.
- an immutable data-source feature registry plus independently packaged,
  live-tested PostGIS ADO.NET/NetTopologySuite EF, TimescaleDB ADO.NET/EF,
  `citext` ADO.NET/EF, `hstore`, `ltree`, `pg_trgm`, and pgvector ADO.NET/EF
  integrations, with a separate non-packable `pg_durable` preview adapter.
- a packaged extension-authoring template and framework-neutral live compatibility harness.
- catalogue-probed PostgreSQL 19 SQL/PGQ capability detection, live raw-SQL property-graph coverage, typed information-schema discovery, text/JSON schema tooling, capability-guarded EF migrations/reverse engineering, and typed composable EF linear-path queries.
- a benchmark-backed decision to retain the genuine sync/async ArrayPool/Span/Memory
  [transport](docs/architecture/transport.md).

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
