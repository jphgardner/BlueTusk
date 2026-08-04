# Runtime release readiness

BlueTusk records production-readiness decisions as executable subsystem gates,
not as synonyms for package version. The 2026-08-02 whole-product review closes
the product-spec engineering gates for the ADO.NET provider, EF Core and design
tooling, PostgreSQL-specific schema/query support, native data paths,
replication, extensions, security, performance, stress, compatibility, and
documentation. The repository and packages remain `0.3.0-preview.1`: completing
an engineering gate does not substitute for external production experience or
the maintainer's explicit decision to publish a stable release.

## Publication gate

All six product-family publication policies are currently disabled. Manual
release-workflow dispatches create candidate artifacts only; they cannot
publish. Provider, Streams, Sync, Live, and Control Plane are locked to stable
1.0.0-or-newer versions, while Continuous Graph is locked to a prerelease
version.

The final candidate commit must enable only the family whose dependencies are
already enabled, then pass an explicit full `build.yml` dispatch at that exact
SHA. Streams additionally requires its exact 72-hour workflow and Sync its
exact 24-hour workflow. The release workflow verifies those successful GitHub
Actions runs by `head_sha`, rejects a mismatched version tag or checkout, and
publishes only through the protected production environment. See the
[release process](release-process.md).

## Connection pooling

`BlueTuskDataSource` owns bounded per-endpoint pools. The completed gate covers:

- minimum and maximum sizes, warm-up, cancellable waiters, idle and maximum
  physical-connection lifetimes, health validation, clear/drain, and disposal;
- rollback plus `DISCARD ALL` before reuse, preventing transaction, temporary
  object, prepared statement, advisory-lock, and session-setting leakage;
- independent multi-host capacity/statistics, primary/standby selection, and
  failover without pooling dedicated replication or notification sessions;
- password and access-token callbacks on every newly created physical session,
  so clearing/expiry rotates credentials without changing a data source; and
- per-data-source statistics, OpenTelemetry metrics, a checkout benchmark,
  PostgreSQL 15–19 live acceptance, and scheduled elevated-concurrency churn.

Deterministic failure tests also prove that failed creation and validation
release capacity, clearing retires leased sessions on return, and data-source
disposal rejects queued opens.

Opt-in statement multiplexing is now part of the V1 gate. It provides bounded
queues, persistent worker lanes, conservative session-state fallback, one
PostgreSQL synchronization group per command, per-group cancellation and error
isolation, forced-shutdown transport abort, and scheduler statistics. Explicit
connections, transactions, prepared commands, replication, notifications, and
stateful SQL remain session-affine. Live PostgreSQL tests cover concurrent
fan-in, cancellation, timeouts, adjacent errors, reset isolation, and stuck-lane
shutdown. The Npgsql comparison and checked-in regression budget must pass
before the V1 baseline is frozen.

## Cancellation

Commands use PostgreSQL's one-shot cancellation connection and the backend key
from the active physical session. Cancellation does not interrupt the normal
socket directly. BlueTusk then drains the original operation through
`ReadyForQuery` before it can be reused; inside a transaction, PostgreSQL's
failed-transaction state is preserved until caller rollback.

The gate covers caller tokens, command and batch timeouts, explicit synchronous
and asynchronous cancellation, sequential readers, portal cleanup, pipeline
synchronization groups, COPY abort/recovery, notification waits, and replication
stream disposal. Protocol encoding and the separate cancellation channel have
unit coverage. PostgreSQL 15–19 acceptance verifies server-side cancellation
and subsequent reuse, while the scheduled stress gate runs cancellation storms
against a bounded pool.

## PostgreSQL-native data paths

| Surface | Production invariant | Executable evidence |
| --- | --- | --- |
| COPY | Raw text/CSV/binary and typed binary transfers stream incrementally with bounded backpressure; failure, cancellation, early disposal, and malformed input abort and recover the session. | Binary framing and fragmented UTF-8 unit tests; sync/async PostgreSQL 15–19 round trips and recovery tests. |
| Notifications | Identifier-safe `LISTEN`/`UNLISTEN`, bounded delivery, explicit lifetime completion, and no listener state in an ADO.NET pool. | Live concurrent command/notification, unsubscribe, close, and invalid-lifetime tests across the server matrix. |
| Large objects | Transaction-owned descriptors, sync/async streaming, access checks, 64-bit seek/truncate, chunked writes, commit/rollback, and deterministic exclusivity. | Stream unit tests plus implicit/explicit transaction lifecycle tests across PostgreSQL 15–19. |
| Replication | Dedicated unpooled physical/logical `COPY BOTH`, slot/publication discovery, monotonic feedback, exact transaction checkpoints, reconnect safety, streamed transactions, two-phase metadata, pgoutput, and custom plug-ins. | Wire/decoder tests, full PostgreSQL 15–19 acceptance, allocation/backpressure benchmarks, cancellation/disposal stress, and the scheduled PostgreSQL 19 1,000-epoch persistent-slot endurance gate. |

The replication packages add 822 compiler-enforced shipped API/nullability
signatures. Their pre-freeze review replaced ambiguous optional-parameter
overload families with explicit no-token and required-token overloads.

## EF Core, design tooling, and extensions

BlueTusk directly consumes Microsoft's EF Core 10.0.10 relational
specification package. Its official assembly gate discovers 2,111 cases on
PostgreSQL 18 and 19: 1,987 pass and 124 retain upstream EF skip declarations.
PostgreSQL 15–17 run the same adopted suite with only unsupported
generated-column rows excluded by explicit server-version conditions. The
portable complex-type/JSON query slice runs unchanged across PostgreSQL 15–19.
BlueTusk's native provider project runs 301 cases per supported server;
PostgreSQL 15 passes 299 with two inapplicable cases and PostgreSQL 16–19 each
pass 300 with only the filesystem-dependent tablespace case skipped when no
server-owned directory is configured.

Together those gates cover CRUD, tracking, relational model building, queries,
updates, physical database lifecycle, migrations, reverse engineering,
generated code, PostgreSQL-specific types/operators/functions/schema objects,
and capability-guarded PostgreSQL 19 SQL/PGQ. The exact official-suite boundary
and upstream skip ownership are documented in the
[EF specification-test record](ef-core/specification-tests.md).

Optional PostGIS, pgvector, citext, hstore, ltree, pg_trgm, and TimescaleDB
packages remain independently installable and do not add their types or SQL to
the core packages. Their live extension-image gates, the immutable feature
registry, extension template, and compatibility harness close the extension
architecture gate. A live extension test first checks
`pg_available_extensions`: a plain PostgreSQL image reports an intentional
dynamic skip when the optional extension is absent, while each dedicated
extension image requires the same test to pass. The dedicated-image gate reports
23 pgvector, 10 PostGIS, and 9 TimescaleDB passes with no skips or failures.
The checked-in workflow enforces these as three independent CI matrix entries,
each running the extension's ADO.NET and EF Core projects against its dedicated
image and retaining container logs on failure.
Cloud identity adapters have deterministic SDK contract tests; their
real-account acceptance tests remain opt-in because CI does not hold customer
cloud credentials.

The compatibility environment matrix also builds pinned, repository-owned
PgBouncer session/transaction configurations and PostgreSQL 18 images for
`en_GB.UTF-8`/`Europe/London` and `de_DE.UTF-8`/`America/New_York`. Live gates
cover session-affine temporary/prepared state, transaction-pool-safe explicit
transactions and prepared commands, locale-aware money text, and time-zone-safe
timestamp decoding. PgBouncer's cleartext test authentication is confined to
the isolated Docker network and requires the provider's explicit insecure-test
opt-in.

A separate PostgreSQL 18 streaming-replication topology takes a fresh physical
base backup for every CI run. Its live tests prove strict and preferred
primary/read-write/standby/read-only selection after unavailable and
role-incompatible endpoints, WAL replay visibility, and standby write
rejection. This is distinct from the logical-replication decoder and endurance
gates.

For change detection beyond the pinned PostgreSQL 19 Beta 2 image, a
scheduled/manual job verifies the checksum of the official nightly PostgreSQL
19 branch snapshot, compiles it in a repository-owned multi-stage image, and
runs the full solution against it. The 2026-08-02 scheduled snapshot run
identified itself as PostgreSQL 19beta2 and passed 2,967 cases with 146
intentional environment or
upstream skips and no failures across 28 test assemblies. No unofficial
PostgreSQL binary image enters the release gate.

The complete serial solution matrix currently reports:

| PostgreSQL | Passed | Intentional skips | Failed |
| --- | ---: | ---: | ---: |
| 15 | 2,963 | 147 | 0 |
| 16 | 2,964 | 146 | 0 |
| 17 | 2,966 | 146 | 0 |
| 18 | 2,978 | 146 | 0 |
| 19 | 2,978 | 146 | 0 |

Each total includes the native and official EF projects, all core and extension
projects, source generation, compatibility, conformance, security, stress, and
replication. The server-free run reports 1,538 passes, 318 intentional skips,
and no failures across the same 28 test assemblies.
Version-specific official migration methods are excluded at discovery when the
server cannot implement their generated-column SQL.

## Release artifacts and documentation

The reviewed Release build produces 30 `0.3.0-preview.1` NuGet/tool/template
packages without warnings. Compiler-enforced public API/nullability baselines
cover all 27 Provider-family library surfaces and are locked by the
[V1 candidate hash manifest](../eng/provider-api-freeze.json). Package
conformance also prevents embedded extension-template content projects from
entering the release train. The final
direct-and-transitive NuGet vulnerability audit covers the complete solution
and reports zero vulnerable package entries. All 30 checked-in allocation
budgets pass, including command, typed reader, protocol writer,
structured-codec, large-value streaming, replication, EF Core application,
Live diff/replay/fan-out, SQL/PGQ traversal, and Continuous Graph registration,
authoritative requery, and affected-invalidation paths.

The live application benchmark gate adds fresh parameterized EF query
compilation plus first execution, 100-entity materialization, normalized tracked
inserts and load/update paths, plus traversal of a 1,000-vertex/999-edge
PostgreSQL 19 property graph through both a prepared raw command and the typed
EF graph API. The checked-in ShortRun reports and allocation budgets are
regression evidence, not universal latency or throughput claims.

The provider core also publishes and executes full-trim and NativeAOT offline
smokes on Windows and Linux. The first Windows x64 observation records a
21,993,850-byte trimmed deployment at 248.994 ms cold wall-clock and 327,144 B
second-pass managed allocation, plus a 5,783,552-byte NativeAOT executable at
18.327 ms and 343,392 B. This covers the provider construction/type-system path
without a database; it is regression evidence rather than production latency
or Npgsql-comparison evidence. The documented AOT boundary keeps common
built-in ranges and one-dimensional arrays static and fails explicitly for
runtime-selected unsupported shapes.

The in-memory Live application gate records a 76.4 µs/221,872 B keyed diff for
one update in a bounded 1,000-row result, 881 ns/832 B versioned replay
serialization, and a 92.3 µs/175,060 B lifecycle that coalesces 100 relevant
invalidations and fans one update to 64 bounded subscribers. A deterministic
race suite separately proves exactly-once observation through either replay or
the live channel across 64 concurrent reconnect/publication boundaries. These
figures exclude database and network time and are used only as checked-in
regression budgets.

The live PostgreSQL 19 Continuous Graph gate records 988 µs/103,446 B for
capability-guarded registration, 2.827 ms/666,055 B for authoritative
materialisation of 999 graph paths, and 4.225 ms/888,159 B for an affected
invalidation through authoritative requery plus keyed diff. The invalidation
source is constant-time and in-memory, but the `GRAPH_TABLE` query and provider
work are included. These ShortRun values are checked-in regression evidence,
not production latency objectives.

The independently versioned `BlueTusk.ContinuousGraph 0.1.0-preview.1` runtime
and `BlueTusk.ContinuousGraph.ControlPlane` adapter
passes a zero-warning repository Release build, the complete offline solution
suite, all eight graph tests including PostgreSQL 19 acceptance, public API and
dependency-direction conformance, documentation-link and allocation-budget
gates, and inspected NuGet packs. The Graph family remains non-publishable
until Provider, Live, and Control Plane are publishable. The Live gate covers signed
disconnect/resume replay from the PostgreSQL production store through SSE,
SignalR/WebSockets, and HTTP/2 gRPC on PostgreSQL 15–19. The release script
rejects any publishable family with a gated dependency. This does not mark the
still-open Streams 72-hour, Sync 24-hour, Live 1.0, or Control Plane release
gates complete.

The independently versioned Control Plane candidate now exposes discoverable
v1 agent routes with versioned envelopes while retaining the original preview
routes as compatibility aliases. Its audit store transactionally upgrades the
legacy pre-metadata table to schema version 2, preserves and format-marks
existing rows, rejects future schemas, and fences appends to the exact running
schema version. Eleven unit tests, a hash-locked compiler API baseline, an
executable format registry, and the live PostgreSQL 15–19 matrix cover route
authorization, version negotiation, immutable audit, fresh initialization, and
legacy upgrade. The `BlueTusk.ControlPlane` and `BlueTusk.Dashboard
0.1.0-preview.1` candidates remain non-publishable because the declared Sync
release dependency has not yet archived its required 24-hour endurance
evidence.

The paired PostgreSQL 19 provider gate records lower BlueTusk mean latency and
managed allocation on parameterized and explicitly prepared scalar execution,
untouched warm checkout, sequential 1,000-row reads, and isolated 1 MiB
streaming. The MediumRun confidence intervals establish clear latency wins for
parameterized execution, checkout, and row streaming; prepared and large-stream
latency remain statistical parity despite lower BlueTusk means. Release
readiness does not reinterpret these environment-specific results as blanket
provider superiority.

Documentation covers every public subsystem and is led by long-lived,
data-source-first usage. A cross-platform CI script validates every local link
in all tracked Markdown files; the current review checks 254 local links across
124 tracked Markdown files. The 2026-08-02 provider review separately resolved
all 40 external Markdown references.
The support matrix identifies .NET 10, EF Core 10.0.10,
PostgreSQL 15–18, and the pinned PostgreSQL 19 Beta 2 preview, including the
remaining beta-syntax risk.

## Automated gates

The normal CI build runs formatting, documentation-link validation,
warnings-as-errors compilation, all offline tests, allocation budgets,
packaging, and public API analysis on Windows and Linux. Live matrices run the
solution against PostgreSQL 15, 16, 17, 18, and 19 and run pgvector, PostGIS,
and TimescaleDB ADO.NET/EF acceptance on their dedicated images. Additional
live jobs enforce PgBouncer session/transaction pooling and two locale/time-zone
combinations. A physical primary/standby job adds target-session selection and
WAL-replay acceptance. Scheduled/manual CI also builds the official PostgreSQL
19 branch snapshot and runs the complete solution against that moving target.
Scheduled/manual jobs add elevated provider concurrency and a separate
replication endurance run. See the
checked-in workflow and
[testing guide](contributing/testing.md) for the exact commands and environment
contract.

The [specification completion audit](completion-audit.md) maps every original
product area and architecture-gap priority to its primary executable evidence.
This evidence closes the repository's current product-spec engineering gates.
It does not rename the packages to `1.0.0`, guarantee suitability for a
particular production deployment, validate optional cloud credentials that were
not supplied, or expand the documented raw-SQL and ownership/grant boundaries.
