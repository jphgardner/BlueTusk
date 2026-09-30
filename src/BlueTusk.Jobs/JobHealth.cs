namespace BlueTusk.Jobs;

public sealed record JobQueueHealth(
    int PendingObserved, bool PendingCountCapped,
    int RunningObserved, bool RunningCountCapped,
    int ExpiredLeasesObserved, bool ExpiredLeaseCountCapped,
    TimeSpan? OldestReadyAge);

public sealed partial class PostgreSqlJobStore
{
    /// <summary>Bounded queue inspection; counts explicitly report saturation instead of scanning unbounded backlog.</summary>
    public async ValueTask<JobQueueHealth> InspectAsync(JobScope scope, int maximumObserved = 10000, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumObserved, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumObserved, 100000);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, transaction: null, $"""
            WITH db_now AS MATERIALIZED (SELECT clock_timestamp() AS now)
            SELECT
                (SELECT count(*)::integer FROM (SELECT 1 FROM {_jobs} WHERE tenant = @tenant AND queue = @queue AND status = 0 LIMIT @limit) p),
                (SELECT count(*)::integer FROM (SELECT 1 FROM {_jobs} WHERE tenant = @tenant AND queue = @queue AND status = 1 LIMIT @limit) r),
                (SELECT count(*)::integer FROM (SELECT 1 FROM {_jobs}, db_now n WHERE tenant = @tenant AND queue = @queue AND status = 1 AND lease_expires <= n.now LIMIT @limit) e),
                (SELECT least(greatest(extract(epoch FROM (n.now - available_at)), 0), 3153600000)::double precision
                    FROM {_jobs}, db_now n WHERE tenant = @tenant AND queue = @queue AND status = 0 AND available_at <= n.now
                    ORDER BY available_at LIMIT 1)
            """);
        AddScope(command, scope);
        Add(command, "limit", maximumObserved + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        int pending = reader.GetInt32(0);
        int running = reader.GetInt32(1);
        int expired = reader.GetInt32(2);
        return new(Math.Min(pending, maximumObserved), pending > maximumObserved,
            Math.Min(running, maximumObserved), running > maximumObserved,
            Math.Min(expired, maximumObserved), expired > maximumObserved,
            reader.IsDBNull(3) ? null : TimeSpan.FromSeconds(reader.GetDouble(3)));
    }
}
