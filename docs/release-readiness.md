# Runtime release readiness

BlueTusk records production-readiness decisions as executable subsystem gates,
not as synonyms for package version. The 2026-08-02 whole-product review closes
the product-spec engineering gates for the ADO.NET provider, EF Core and design
tooling, PostgreSQL-specific schema/query support, native data paths,
replication, extensions, security, performance, stress, compatibility, and
documentation. The repository and packages remain `0.3.0-preview.1`: completing
an engineering gate does not substitute for external production experience or
the maintainer's explicit decision to publish a stable release.

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
disposal rejects queued opens. Multiplexing remains deliberately out of scope;
one logical checkout exclusively owns one physical session.

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
PostgreSQL 19: 1,987 pass and 124 retain upstream EF skip declarations. The
portable complex-type/JSON query slice additionally runs across PostgreSQL
15–19. BlueTusk's native provider project runs 301 cases per supported server;
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
extension image requires the same test to pass. Cloud identity adapters have
deterministic SDK contract tests; their real-account acceptance tests remain
opt-in because CI does not hold customer cloud credentials.

The complete solution gate on PostgreSQL 15 currently reports 2,948 passes,
137 intentional skips, and zero failures. The total includes the native and
official EF projects, all core and extension projects, compatibility,
conformance, security, stress, and replication; version-specific official
migration methods are excluded at discovery when the server cannot implement
their generated-column SQL.

## Release artifacts and documentation

The reviewed Release build produces 30 `0.3.0-preview.1` NuGet/tool/template
packages without warnings. Compiler-enforced public API/nullability baselines
cover the stable core, replication, and extension-authoring seams. All 18
checked-in allocation budgets pass, including command, typed reader, protocol
writer, structured-codec, large-value streaming, and replication paths.

Documentation covers every public subsystem and is led by long-lived,
data-source-first usage. A cross-platform CI script validates every local link
in all tracked Markdown files; the 2026-08-02 review checked 55 local links
across 73 files and separately resolved all 40 external Markdown references.
The support matrix identifies .NET 10, EF Core 10.0.10,
PostgreSQL 15–18, and the pinned PostgreSQL 19 Beta 2 preview, including the
remaining beta-syntax risk.

## Automated gates

The normal CI build runs formatting, documentation-link validation,
warnings-as-errors compilation, all offline tests, allocation budgets,
packaging, and public API analysis on Windows and Linux. A live matrix runs the
solution against PostgreSQL 15, 16, 17, 18, and 19. Scheduled/manual jobs add
elevated provider concurrency and a separate replication endurance run. See the
checked-in workflow and
[testing guide](contributing/testing.md) for the exact commands and environment
contract.

This evidence closes the repository's current product-spec engineering gates.
It does not rename the packages to `1.0.0`, guarantee suitability for a
particular production deployment, validate optional cloud credentials that were
not supplied, or expand the documented raw-SQL and ownership/grant boundaries.
