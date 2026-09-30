# PostgreSQL 19 native REPACK

PostgreSQL 19 adds the native `REPACK` statement for rewriting a table and
returning space occupied by dead rows to the operating system. This is the
PostgreSQL command, not the separate `pg_repack` extension.

BlueTusk 1.2 supports every documented PostgreSQL 19 form through
`BlueTuskRepackRequest`, prevents unsupported combinations before sending SQL,
and exposes live server progress from `pg_stat_progress_repack`.

> PostgreSQL 19 is currently Beta 3. Use this API for development and release
> qualification now, but wait for the digest-pinned PostgreSQL 19 GA gate
> before describing the combination as production-certified.

## Repack one table

Open a dedicated connection. A repack owns that physical session until the
server completes or the operation is cancelled.

```csharp
using BlueTusk.Data;
using BlueTusk.Data.Maintenance;

await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();
await using var connection = await dataSource.OpenConnectionAsync();

if (connection.SupportsRepack is not true)
{
    throw new InvalidOperationException("PostgreSQL 19 or later is required.");
}

await connection.RepackAsync(
    BlueTuskRepackRequest.ForTable("events", "app") with
    {
        Concurrently = true,
        Analyze = true,
        CommandTimeoutSeconds = 0,
    },
    stoppingToken);
```

`CommandTimeoutSeconds = 0` disables the client timeout, which is usually the
right choice for scheduled maintenance. Cancellation still sends PostgreSQL's
normal cancellation request and restores the connection to a usable protocol
state.

## Choose the operation

| Goal | Request |
| --- | --- |
| Rewrite one table | `ForTable("events", "app")` |
| Keep a supported table available | Set `Concurrently = true` |
| Refresh all table statistics | Set `Analyze = true` |
| Refresh selected column statistics | Set `Analyze = true` and `AnalyzeColumns = ["tenant_id"]` |
| Physically order by the configured clustering index | Set `UseIndex = true` |
| Choose and remember a clustering index | Set `IndexName = "events_created_at_idx"` |
| Emit PostgreSQL information messages | Set `Verbose = true` |
| Process all eligible relations in the database | `ForDatabase()` |
| Process all relations with a configured clustering index | `ForDatabase() with { UseIndex = true }` |

Table, schema, index, and column names are quoted as PostgreSQL identifiers;
they are never concatenated as untrusted SQL fragments. BlueTusk sends
`REPACK` over the simple protocol on a non-multiplexed connection because it is
connection-affine maintenance work.

For a newly introduced PostgreSQL feature without a BlueTusk convenience API,
use a parameterized `BlueTuskCommand`. BlueTusk does not maintain an allow-list
of SQL statements, so native PostgreSQL grammar remains available immediately.

## Monitor progress

Poll from a second connection while the maintenance connection is busy:

```csharp
await using var observer = await dataSource.OpenConnectionAsync();

foreach (var operation in await observer.GetRepackProgressAsync(stoppingToken))
{
    logger.LogInformation(
        "REPACK pid {Pid}: {Phase}, heap scan {Percent:P0}, {Indexes} indexes rebuilt",
        operation.ProcessId,
        operation.Phase,
        operation.HeapScanPercent is { } percent ? percent / 100 : null,
        operation.IndexRebuildCount);
}
```

The returned model includes the database and relation OIDs, command, phase,
index OID, tuple counters, heap-block counters, index rebuild count, and a
bounded `HeapScanPercent` when PostgreSQL reports a block total. A completed
operation disappears from the view, so store any history your operations team
needs outside PostgreSQL.

## Production checks

Before scheduling `REPACK`:

1. Grant the execution role `MAINTAIN` on each target table.
2. Budget free disk for a complete table and index copy. A sort can require up
   to roughly twice the table size plus its indexes.
3. Use `CONCURRENTLY` only for a logged, non-partitioned user table with a
   primary key or index-based replica identity.
4. Reserve a `max_repack_replication_slots` slot and enough WAL capacity for a
   concurrent run.
5. Choose a quiet period. Concurrent mode shortens the final exclusive lock,
   but it still needs one to swap relation files and catch-up work can extend it.
6. Alert on a stalled phase, rapidly growing WAL or temporary storage, lock
   waits, cancellation, and server errors.
7. Verify table row counts and application health after completion.

Database-wide and concurrent operations cannot run inside a transaction.
PostgreSQL also rejects a partitioned-table repack inside a transaction. The
BlueTusk request validator rejects transaction and option combinations it can
prove invalid; relation-specific eligibility remains authoritative on the
server.

The command's full behavior and restrictions are defined by the PostgreSQL 19
[`REPACK` documentation](https://www.postgresql.org/docs/19/sql-repack.html),
and the progress columns and phases are defined by
[`pg_stat_progress_repack`](https://www.postgresql.org/docs/19/progress-reporting.html#REPACK-PROGRESS-REPORTING).
