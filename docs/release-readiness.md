# Runtime release readiness

BlueTusk records production-readiness decisions as executable subsystem gates,
not as synonyms for package version. The 2026-08-02 review closes the pooling,
cancellation, COPY, notification, large-object, and replication gates. The
repository and NuGet packages remain `0.3.0-preview.1` until the remaining EF,
schema, documentation, and whole-product release gates are complete.

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

## Automated gates

The normal CI build runs formatting, warnings-as-errors compilation, all offline
tests, allocation budgets, packaging, and public API analysis on Windows and
Linux. A live matrix runs the solution against PostgreSQL 15, 16, 17, 18, and
19. Scheduled/manual jobs add elevated provider concurrency and a separate
replication endurance run. See the checked-in workflow and
[testing guide](contributing/testing.md) for the exact commands and environment
contract.

This evidence closes the listed runtime subsystem gates. It does not promote
the entire provider to 1.0 or claim production readiness for EF, design tooling,
identity adapters, or individual extension-specific APIs.
