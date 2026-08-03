# Connection pooling

`BlueTuskDataSource` owns an independent bounded physical connection pool. Connections created directly with `new BlueTuskConnection(...)` remain unpooled; applications that want pooling should keep one data source for each distinct connection string and open logical connections from it.

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

For high-concurrency, session-neutral commands, enable the bounded statement
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

| Setting | Default | Meaning |
|---|---:|---|
| `Pooling` | `true` | Enables the data source's physical connection pool. |
| `Minimum Pool Size` | `0` | Physical sessions opened by warm-up and before the first checkout. |
| `Maximum Pool Size` | `100` | Hard limit for physical sessions in each host endpoint pool. |
| `Connection Idle Lifetime` | 5 minutes | Maximum idle age checked before reuse; zero disables idle expiry. |
| `Connection Lifetime` | 1 hour | Maximum physical-session age checked at checkout and return; zero disables maximum-age expiry. |
| `Multiplexing` | `false` | Enables bounded statement multiplexing; pooling must also be enabled. |

When the pool is at its maximum, asynchronous opens wait in order for returned capacity. The caller's cancellation token cancels that wait without consuming a slot.

Multi-host data sources own one pool per configured endpoint. Checkout tries available capacity across the selected host order, and role-targeted checkouts revalidate primary/standby and read-only state. `Minimum Pool Size` and `Maximum Pool Size` apply to each endpoint pool. `GetHostPoolStatistics()` exposes each partition; `GetPoolStatistics()` reports their aggregate.

## Reset and validation

Closing a logical connection makes its physical session available but does not block on network I/O. An open/close cycle that never accesses the physical session is marked clean and can be leased again without a server exchange. After any command, transaction, COPY, large-object, type-reload, or other session operation, BlueTusk resets the session before reuse:

1. sends `ROLLBACK` if PostgreSQL reports an open or failed transaction;
2. sends `DISCARD ALL`, which clears temporary objects, prepared statements, listeners, advisory locks, plans, sequence state, and changed session settings;
3. verifies that the session remains open and PostgreSQL reports the idle transaction state.

The reset round trip is also the health check for a touched lease. An untouched lease still verifies the locally observed open and idle state. A closed session, failed reset, expired session, or session from an earlier pool generation is discarded and replaced within the configured maximum. Logical connections reuse the data source's validated immutable connection settings, and optional notification/large-object coordination state is allocated only when those features are used.

## Operations and diagnostics

- `WarmUpAsync()` opens the configured minimum number of physical sessions.
- `GetPoolStatistics()` returns total, idle, busy, waiting, opened, reused, and discarded counts.
- `GetHostPoolStatistics()` returns the same counters for each configured endpoint.
- `ClearPool()` and `ClearPoolAsync()` close idle sessions and mark active sessions for disposal when they return.
- Disposing the data source cancels queued opens, closes idle sessions, and drains active sessions as their logical connections close.
- `GetMultiplexingStatistics()` reports queue, worker, completion, cancellation,
  failure, and PostgreSQL pipeline counters for the data source.

## Statement multiplexing

Multiplexing uses persistent, bounded worker lanes rather than assigning one
physical session to every logical command. The automatic worker count reserves
at most half of the configured pool and never selects more than four workers.
The default queue holds 1,024 commands, a pipeline flush contains at most 64
independently synchronized commands, and a lane is recycled after 65,536
commands. Disposal drains for 30 seconds before physically aborting a stuck
lane.

Only commands created directly from `BlueTuskDataSource` are eligible. Commands
on explicit connections, enlisted transactions, explicitly prepared commands,
and SQL that can depend on session state use a normal affine lease. This
includes transaction control, `SET`/`RESET`, temporary objects, LISTEN, COPY,
cursors, explicit PREPARE/EXECUTE, session advisory locks, and `set_config`.
Set `MultiplexingMode` to `Require` to reject fallback or `Disable` for a trusted
user-defined routine whose statefulness cannot be inferred from SQL text.

Every command ends at its own PostgreSQL `Sync` boundary. Server errors,
per-command timeouts, and caller cancellation are isolated from neighbouring
commands. Queue, pipeline, lease, and shutdown bounds are enforced independently.
See [ADR 0013](../architecture/decisions/0013-bounded-statement-multiplexing.md).

The `BlueTusk.Diagnostics` meter publishes connection, lease, waiter, reuse, reset, discard, and checkout-duration instruments. Multi-host retries and non-first-host selections have separate counters. Statistics are scoped to one data source; meter instruments are process-wide aggregates. See [Diagnostics and observability](../observability.md) for names, dimensions, and redaction rules.

The pooling production gate, its failure invariants, live version matrix, stress
coverage, and multiplexing boundary are recorded in
[Runtime release readiness](../release-readiness.md).
