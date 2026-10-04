# Multiplexing compatibility

This page helps you decide whether multiplexing suits your workload, and shows
which commands it shares across connections and which it keeps on a dedicated
session.

Multiplexing is opt-in. It runs independent commands created directly from a
`BlueTuskDataSource` over a small number of shared physical connections. It
never moves session state, such as a transaction, between connections.

```csharp
await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .EnableMultiplexing()
    .Build();

// Shared: an independent statement from the data source.
await using var count = dataSource.CreateCommand("SELECT count(*) FROM app.orders");
var orders = await count.ExecuteScalarAsync<long>();

// Fail instead of falling back to a dedicated session.
await using var strict = dataSource.CreateCommand("SELECT now()");
strict.MultiplexingMode = BlueTuskMultiplexingMode.Require;
var now = await strict.ExecuteScalarAsync<DateTimeOffset>();
```

You can also turn it on with `Multiplexing=true` in the connection string.
Options are listed in [Configuration](configuration.md#multiplexing-options).

## Routing matrix

"Affine" means the command runs on a dedicated session, as it would without
multiplexing.

| Command | Route | Why |
| --- | --- | --- |
| Independent text and parameterised commands from the data source | Shared | A first-in, first-out queue writes up to `MaxPipelineCommands` commands per flush, each with its own PostgreSQL `Sync`. |
| Commands on an explicit `BlueTuskConnection` | Dedicated | You own the session. `Require` throws before execution instead of falling back. |
| Transactions and savepoints | Affine | Transaction state, failures, and savepoints belong to one backend. |
| Explicitly prepared commands and SQL `PREPARE`/`EXECUTE`/`DEALLOCATE` | Affine | Prepared statement identity belongs to a backend session. |
| Sequential readers and cursors | Affine | A portal remains live until the reader completes or is disposed. `Require` throws. |
| COPY import/export | Affine | COPY changes the protocol state until completion, cancellation, or abort recovery. |
| Large objects and `lo_*`/legacy large-object routines | Affine | Descriptors and their owning transaction belong to one connection. |
| `LISTEN`/`UNLISTEN` and notification APIs | Affine | Listener registration and the notification pump own dedicated session state. `NOTIFY` is conservatively routed affine as well. |
| Temporary objects and `pg_temp` | Affine | Temporary schemas and objects belong to one backend. |
| Session advisory locks | Affine | Lock ownership is the backend process. Transaction-scoped advisory-lock routines are conservatively affine too. |
| `SET`, `RESET`, `SHOW`, `set_config`, `current_setting`, `currval`, and `lastval` | Affine | These mutate or observe session-local settings/sequence state. |
| `CALL`, `DO`, and unknown stateful user routines | Affine | `CALL` and `DO` always use a dedicated session. SQL text cannot prove a function has no side effects on the session; set `MultiplexingMode.Disable` for a stateful routine. |
| Replication | Dedicated | Replication owns an unpooled physical/logical `COPY BOTH` session and never enters the statement scheduler. |

`MultiplexingMode.Auto` uses this routing table,
`MultiplexingMode.Require` rejects every fallback, and
`MultiplexingMode.Disable` deliberately obtains an affine lease. The classifier
skips quoted strings, quoted identifiers, dollar-quoted bodies, line comments,
and nested block comments before inspecting tokens. It remains conservative;
it is not a SQL authorisation boundary.

## How the scheduler behaves

- The queue size, pipeline size, worker count, commands per lease and shutdown
  time each have their own limit (see
  [Configuration](configuration.md#multiplexing-options)).
- Accepted commands are serviced FIFO per lane. Multiple lanes increase
  concurrency without allowing one lane to retain a pool lease beyond
  `MaxCommandsPerLease`.
- Cancellation while waiting for channel admission does not enter the queue.
  Cancellation after admission completes that request and preserves the lane.
- Pool exhaustion remains cancellable. For pools of two or more sessions, the
  automatic worker count consumes at most half the configured pool; a
  one-session pool cannot reserve simultaneous affine capacity.
- Each command has its own protocol synchronization group. A server error,
  caller cancellation, or command timeout is drained through `ReadyForQuery`
  before the next group completes.
- Disposal stops admission, drains accepted work up to `ShutdownTimeout`, then
  aborts stuck physical transports and completes every remaining request.
- A touched lease is rolled back if needed and runs `DISCARD ALL` before reuse,
  clearing settings, temporary objects, listeners, advisory locks, and
  prepared statements.

The process-wide `BlueTusk.Diagnostics` meter exposes pending/executing
up/down counters, admission and outcome counters, queue-wait duration, pipeline
size, and forced-shutdown count. `GetMultiplexingStatistics()` remains the
per-data-source point-in-time view.

## PgBouncer

Multiplexing is tested through PgBouncer 1.24 in both session and transaction
modes. In transaction mode, explicit preparation needs PgBouncer's
protocol-level prepared-statement support, and BlueTusk does not emulate
session state on top of transaction pooling. See
[Troubleshooting](troubleshooting.md#pgbouncer-and-prepared-statements).
