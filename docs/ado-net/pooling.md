# Connection pooling

This page helps you size, warm up and monitor the connection pool, and
understand what happens to a connection between uses.

`BlueTuskDataSource` owns its own pool of physical connections, limited by
`Maximum Pool Size`. Connections created directly with `new BlueTuskConnection(...)` remain unpooled; applications that want pooling should keep one data source for each distinct connection string and open logical connections from it.

```csharp
var settings = new BlueTuskConnectionStringBuilder(connectionString)
{
    Pooling = true,
    MinimumPoolSize = 2,
    MaximumPoolSize = 50,
    ConnectionIdleLifetime = TimeSpan.FromMinutes(5),
    ConnectionLifetime = TimeSpan.FromHours(1),
};

await using var dataSource = BlueTuskDataSource.Create(settings.ConnectionString);
await dataSource.WarmUpAsync();
await using var connection = await dataSource.OpenConnectionAsync();
```

For high-concurrency, session-neutral commands, enable the statement
multiplexer and create commands directly from the data source:

```csharp
await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .EnableMultiplexing(options =>
    {
        options.WorkerCount = 4;
        options.QueueCapacity = 1_024;
        options.MaxPipelineCommands = 64;
    })
    .Build();

await using var command = dataSource.CreateCommand("SELECT $1::int4");
command.Parameters.Add(new BlueTuskParameter<int>(42));
var value = await command.ExecuteScalarAsync<int>();
```

## Settings

Every keyword is described in [Configuration](configuration.md#pooling).

| Setting | Default | Meaning |
|---|---:|---|
| `Pooling` | `true` | Enables the data source's physical connection pool. |
| `Minimum Pool Size` | `0` | Physical sessions opened by warm-up and before the first checkout. |
| `Maximum Pool Size` | `100` | Hard limit for physical sessions in each host endpoint pool. |
| `Connection Idle Lifetime` | 5 minutes | Maximum idle age checked before reuse; zero disables idle expiry. |
| `Connection Lifetime` | 1 hour | Maximum physical-session age checked at checkout and return; zero disables maximum-age expiry. |
| `Multiplexing` | `false` | Enables statement multiplexing; pooling must also be enabled. |
| `Timeout` | 15 seconds | Limits connecting to the server and waiting for a free connection when the pool is full. |

Watch the pool from your own health or metrics code:

```csharp
var stats = dataSource.GetPoolStatistics();
Console.WriteLine(
    $"total={stats.Total} idle={stats.Idle} busy={stats.Busy} waiting={stats.Waiting}");
```

Multi-host data sources own one pool per configured endpoint. Checkout tries available capacity across the selected host order, and role-targeted checkouts revalidate primary/standby and read-only state. `Minimum Pool Size` and `Maximum Pool Size` apply to each endpoint pool. `GetHostPoolStatistics()` exposes each partition; `GetPoolStatistics()` reports their aggregate.

## What happens when the pool is full?

When every connection is in use, `Open`, `OpenAsync` and data-source commands
wait in order for a connection to be returned. The connection-string `Timeout`
(default 15 seconds, also reported as `DbConnection.ConnectionTimeout`) limits
this wait. When it expires, the open throws a `TimeoutException` that names
the endpoint, `Maximum Pool Size` and `Timeout`:

```text
The connection pool for db.example.com:5432 is exhausted: no connection became available within the 15-second Timeout. Close connections sooner, or raise 'Maximum Pool Size' (currently 100) or 'Timeout' in the connection string.
```

The caller leaves the queue without using a slot. A cancellation token ends
the wait sooner, with `OperationCanceledException`:

```csharp
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await using var connection = await dataSource.OpenConnectionAsync(timeout.Token);
```

In a [multi-host](multi-host.md) data source each endpoint pool applies the
limit, and a full endpoint pool is not marked as unavailable.

> **Note:** **Behaviour change in 1.1.0.** In 1.0.0 and 1.1.0-rc.1 an open
> waited for a free pooled connection with no time limit. If your application
> relied on that, raise `Timeout`.

## Reset and validation

Closing a logical connection makes its physical session available but does not block on network I/O. An open/close cycle that never accesses the physical session is marked clean and can be leased again without a server exchange. After any command, transaction, COPY, large-object, type-reload, or other session operation, BlueTusk resets the session before reuse:

1. sends `ROLLBACK` if PostgreSQL reports an open or failed transaction;
2. sends `DISCARD ALL`, which clears temporary objects, prepared statements, listeners, advisory locks, plans, sequence state, and changed session settings;
3. verifies that the session remains open and PostgreSQL reports the idle transaction state.

The reset round trip is also the health check for a touched lease. An untouched lease still verifies the locally observed open and idle state. A closed session, failed reset, expired session, or session from an earlier pool generation is discarded and replaced within the configured maximum. Logical connections reuse the data source's validated immutable connection settings, and optional notification/large-object coordination state is allocated only when those features are used.

For an eligible parameterized scalar command created directly from a single-host
data source, BlueTusk defers an idle session's `DISCARD ALL` until command
execution. The reset and extended-query messages are written in one transport
flush, while PostgreSQL still completes the reset before it processes the user
query. This removes a separate checkout round trip without weakening isolation.
Sessions in a transaction are reset before checkout, and explicit connections,
prepared commands, simple-query execution, multi-host routing, and initial type
metadata loading retain the conservative reset-before-lease path.

## Operations and diagnostics

- `WarmUpAsync()` opens the configured minimum number of physical sessions.
- `GetPoolStatistics()` returns total, idle, busy, waiting, opened, reused, and discarded counts.
- `GetHostPoolStatistics()` returns the same counters for each configured endpoint.
- `ClearPool()` and `ClearPoolAsync()` close idle sessions and mark active sessions for disposal when they return.
- Disposing the data source cancels queued opens, closes idle sessions, and drains active sessions as their logical connections close.
- `GetMultiplexingStatistics()` reports queue, worker, completion, cancellation,
  failure, and PostgreSQL pipeline counters for the data source.

## Statement multiplexing

Multiplexing uses a fixed number of long-lived workers rather than assigning
one physical session to every logical command. The automatic worker count reserves
at most half of the configured pool and never selects more than four workers.
The default queue holds 1,024 commands, a pipeline flush contains at most 64
independently synchronized commands, and a lane is recycled after 65,536
commands. Disposal drains for 30 seconds before physically aborting a stuck
lane.

Only commands created directly from `BlueTuskDataSource` are eligible. Commands
on explicit connections, enlisted transactions, explicitly prepared commands,
and SQL that can depend on session state use a normal affine lease. This
includes transaction control, `SET`/`RESET`, temporary objects, LISTEN, COPY,
cursors and sequential readers, explicit PREPARE/EXECUTE, large-object
routines, session advisory locks, `SHOW`/`current_setting`, and `set_config`.
`CALL`, `DO`, and notification statements are conservatively affine too. Set
`MultiplexingMode` to `Require` to reject every fallback (including explicit
connections and sequential readers), or `Disable` for a trusted user-defined
routine whose statefulness cannot be inferred from SQL text.

Every command ends at its own PostgreSQL `Sync` boundary. Server errors,
per-command timeouts, and caller cancellation are isolated from neighbouring
commands. Queue, pipeline, lease and shutdown limits are enforced separately.
The design is recorded in
[ADR 0013](../architecture/decisions/0013-bounded-statement-multiplexing.md).
The complete routing, PgBouncer, failure, and verification matrix is in
[Multiplexing compatibility](multiplexing-compatibility.md).

The `BlueTusk.Diagnostics` meter publishes connection, lease, waiter, reuse, reset, discard, and checkout-duration instruments. Multi-host retries and non-first-host selections have separate counters. Statistics are scoped to one data source; meter instruments are process-wide aggregates. See [Diagnostics and observability](../observability.md) for names, dimensions, and redaction rules.
