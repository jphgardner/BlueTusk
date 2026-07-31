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

## Settings

| Setting | Default | Meaning |
|---|---:|---|
| `Pooling` | `true` | Enables the data source's physical connection pool. |
| `Minimum Pool Size` | `0` | Physical sessions opened by warm-up and before the first checkout. |
| `Maximum Pool Size` | `100` | Hard limit for physical sessions in each host endpoint pool. |
| `Connection Idle Lifetime` | 5 minutes | Maximum idle age checked before reuse; zero disables idle expiry. |
| `Connection Lifetime` | 1 hour | Maximum physical-session age checked at checkout and return; zero disables maximum-age expiry. |

When the pool is at its maximum, asynchronous opens wait in order for returned capacity. The caller's cancellation token cancels that wait without consuming a slot.

Multi-host data sources own one pool per configured endpoint. Checkout tries available capacity across the selected host order, and role-targeted checkouts revalidate primary/standby and read-only state. `Minimum Pool Size` and `Maximum Pool Size` apply to each endpoint pool. `GetHostPoolStatistics()` exposes each partition; `GetPoolStatistics()` reports their aggregate.

## Reset and validation

Closing a logical connection makes its physical session available but does not block on network I/O. Before reuse, BlueTusk:

1. sends `ROLLBACK` if PostgreSQL reports an open or failed transaction;
2. sends `DISCARD ALL`, which clears temporary objects, prepared statements, listeners, advisory locks, plans, sequence state, and changed session settings;
3. verifies that the session remains open and PostgreSQL reports the idle transaction state.

The reset round trip is also the health check. A closed session, failed reset, expired session, or session from an earlier pool generation is discarded and replaced within the configured maximum.

## Operations and diagnostics

- `WarmUpAsync()` opens the configured minimum number of physical sessions.
- `GetPoolStatistics()` returns total, idle, busy, waiting, opened, reused, and discarded counts.
- `GetHostPoolStatistics()` returns the same counters for each configured endpoint.
- `ClearPool()` and `ClearPoolAsync()` close idle sessions and mark active sessions for disposal when they return.
- Disposing the data source cancels queued opens, closes idle sessions, and drains active sessions as their logical connections close.

The `BlueTusk.Diagnostics` meter publishes connection, lease, waiter, reuse, reset, discard, and checkout-duration instruments. Statistics are scoped to one data source; meter instruments are process-wide aggregates.
