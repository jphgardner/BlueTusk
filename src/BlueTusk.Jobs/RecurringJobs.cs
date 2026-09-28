namespace BlueTusk.Jobs;

public sealed partial class PostgreSqlJobStore
{
    /// <summary>Creates a fixed-interval schedule. Existing identities are never silently reconfigured.</summary>
    public async ValueTask<bool> CreateScheduleAsync(RecurringJobSchedule schedule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        JobValidation.Name(schedule.Name, nameof(schedule.Name), 100);
        ValidateRequest(schedule.Job);
        JobValidation.Duration(schedule.Interval, nameof(schedule.Interval), TimeSpan.FromDays(365));
        if (schedule.Interval < TimeSpan.FromMilliseconds(100) || schedule.Interval.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schedule), "Recurring intervals must be whole milliseconds and at least 100 milliseconds.");
        }

        if (!Enum.IsDefined(schedule.MisfirePolicy) || schedule.Job.DeduplicationKey is not null || schedule.Job.Delay != TimeSpan.Zero)
        {
            throw new ArgumentException("Schedules require a valid misfire policy and a job without a delay or deduplication key.", nameof(schedule));
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, transaction: null, $"""
            INSERT INTO {_schedules} (tenant, queue, name, id, job_type, payload, maximum_attempts, interval_ms, next_at, misfire)
            VALUES (@tenant, @queue, @name, @id, @type, @payload, @maximum, @interval, @next, @misfire)
            ON CONFLICT (tenant, queue, name) DO NOTHING
            """);
        AddScope(command, schedule.Job.Scope);
        Add(command, "name", schedule.Name);
        Add(command, "id", Guid.NewGuid());
        Add(command, "type", schedule.Job.JobType);
        Add(command, "payload", schedule.Job.Payload.ToArray());
        Add(command, "maximum", schedule.Job.MaximumAttempts);
        Add(command, "interval", (long)schedule.Interval.TotalMilliseconds);
        Add(command, "next", schedule.FirstOccurrence.ToUniversalTime());
        Add(command, "misfire", (short)schedule.MisfirePolicy);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>Advances schedules and inserts occurrences in one statement. Dispatchers may race safely.</summary>
    public async ValueTask<int> DispatchSchedulesAsync(JobScope scope, int maximumCount, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ValidateBatch(maximumCount);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, transaction: null, $"""
            WITH db_now AS MATERIALIZED (SELECT clock_timestamp() AS now),
            due AS MATERIALIZED (
                SELECT s.*, n.now AS db_clock FROM {_schedules} s, db_now n
                WHERE s.tenant = @tenant AND s.queue = @queue AND s.next_at <= n.now
                ORDER BY s.next_at, s.name FOR UPDATE OF s SKIP LOCKED LIMIT @count
            ), changed AS (
                UPDATE {_schedules} s SET next_at = d.next_at +
                    (floor(extract(epoch FROM (d.db_clock - d.next_at)) * 1000 / d.interval_ms) + 1)
                    * d.interval_ms * interval '1 millisecond'
                FROM due d WHERE s.tenant = d.tenant AND s.queue = d.queue AND s.name = d.name
                RETURNING d.*
            ), inserted AS (
                INSERT INTO {_jobs} (tenant, queue, id, job_type, payload, dedup_key, available_at, maximum_attempts)
                SELECT tenant, queue, gen_random_uuid(), job_type, payload,
                    'schedule:' || id::text || ':' || extract(epoch FROM next_at)::text, db_clock, maximum_attempts
                FROM changed WHERE misfire = 0 OR db_clock - next_at < interval_ms * interval '1 millisecond'
                ON CONFLICT (tenant, queue, dedup_key) DO NOTHING RETURNING id
            ) SELECT count(*)::integer FROM inserted
            """);
        AddScope(command, scope);
        Add(command, "count", maximumCount);
        return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>Stops future occurrences; previously enqueued jobs retain their normal lifecycle.</summary>
    public async ValueTask<bool> DeleteScheduleAsync(JobScope scope, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        JobValidation.Name(name, nameof(name), 100);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, transaction: null,
            $"DELETE FROM {_schedules} WHERE tenant = @tenant AND queue = @queue AND name = @name");
        AddScope(command, scope);
        Add(command, "name", name);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }
}
