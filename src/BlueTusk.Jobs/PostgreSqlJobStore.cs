using System.Data;
using System.Data.Common;
using BlueTusk.Data;

namespace BlueTusk.Jobs;

/// <summary>PostgreSQL-backed, at-least-once durable work. The caller owns the data source.</summary>
public sealed partial class PostgreSqlJobStore
{
    private readonly BlueTuskDataSource _dataSource;
    private readonly JobStoreOptions _options;
    private readonly string _schema;
    private readonly string _jobs;
    private readonly string _history;
    private readonly string _schedules;

    internal int MaximumClaimBatch => _options.MaximumClaimBatch;
    internal int MaximumPayloadBytes => _options.MaximumPayloadBytes;
    internal int MaximumClaimPayloadBytes => _options.MaximumClaimPayloadBytes;

    public PostgreSqlJobStore(BlueTuskDataSource dataSource, JobStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _options = options ?? new JobStoreOptions();
        _options.Validate();
        _dataSource = dataSource;
        _schema = "\"" + _options.Schema + "\"";
        _jobs = _schema + ".jobs";
        _history = _schema + ".job_attempts";
        _schedules = _schema + ".schedules";
    }

    /// <summary>Deployment-time schema setup, serialized with a transaction advisory lock.</summary>
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = CreateCommand(connection, transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended(@schema, 0))"))
        {
            Add(command, "schema", _options.Schema);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(connection, transaction, $"CREATE SCHEMA IF NOT EXISTS {_schema}", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"""
            CREATE TABLE IF NOT EXISTS {_schema}.settings (
                singleton boolean PRIMARY KEY CHECK (singleton),
                format_version integer NOT NULL,
                maximum_payload_bytes integer NOT NULL,
                maximum_history_entries integer NOT NULL
            )
            """, cancellationToken).ConfigureAwait(false);
        await using (var command = CreateCommand(connection, transaction, $"""
            INSERT INTO {_schema}.settings VALUES (true, 1, @payload, @history)
            ON CONFLICT (singleton) DO NOTHING
            """))
        {
            Add(command, "payload", _options.MaximumPayloadBytes);
            Add(command, "history", _options.MaximumHistoryEntries);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = CreateCommand(connection, transaction,
            $"SELECT format_version, maximum_payload_bytes, maximum_history_entries FROM {_schema}.settings WHERE singleton"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetInt32(0) != 1 ||
                reader.GetInt32(1) != _options.MaximumPayloadBytes || reader.GetInt32(2) != _options.MaximumHistoryEntries)
            {
                throw new InvalidOperationException("Jobs schema format or admission limits do not match this store.");
            }
        }

        await ExecuteAsync(connection, transaction, $"""
            CREATE TABLE IF NOT EXISTS {_jobs} (
                tenant varchar(200) NOT NULL,
                queue varchar(200) NOT NULL,
                id uuid NOT NULL,
                job_type varchar(200) NOT NULL,
                payload bytea NOT NULL CHECK (octet_length(payload) <= {_options.MaximumPayloadBytes}),
                dedup_key varchar(200) NULL,
                status smallint NOT NULL DEFAULT 0 CHECK (status BETWEEN 0 AND 4),
                available_at timestamptz NOT NULL,
                created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                completed_at timestamptz NULL,
                attempts integer NOT NULL DEFAULT 0 CHECK (attempts >= 0),
                maximum_attempts integer NOT NULL CHECK (maximum_attempts BETWEEN 1 AND 10000),
                fencing_token bigint NOT NULL DEFAULT 0 CHECK (fencing_token >= 0),
                lease_owner varchar(200) NULL,
                lease_expires timestamptz NULL,
                last_failure_code varchar(64) NULL,
                PRIMARY KEY (tenant, queue, id),
                UNIQUE (tenant, queue, dedup_key),
                CHECK ((status = 1) = (lease_owner IS NOT NULL AND lease_expires IS NOT NULL)),
                CHECK ((status IN (2, 3, 4)) = (completed_at IS NOT NULL)),
                CHECK (attempts <= maximum_attempts)
            )
            """, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"""
            CREATE INDEX IF NOT EXISTS jobs_pending ON {_jobs} (tenant, queue, available_at, id) WHERE status = 0
            """, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"""
            CREATE INDEX IF NOT EXISTS jobs_expired ON {_jobs} (tenant, queue, lease_expires, id) WHERE status = 1
            """, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"""
            CREATE INDEX IF NOT EXISTS jobs_retention ON {_jobs} (tenant, queue, completed_at, id) WHERE status IN (2, 3, 4)
            """, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"""
            CREATE TABLE IF NOT EXISTS {_history} (
                tenant varchar(200) NOT NULL,
                queue varchar(200) NOT NULL,
                job_id uuid NOT NULL,
                attempt integer NOT NULL,
                fencing_token bigint NOT NULL,
                started_at timestamptz NOT NULL,
                finished_at timestamptz NULL,
                outcome varchar(16) NOT NULL,
                failure_code varchar(64) NULL,
                PRIMARY KEY (tenant, queue, job_id, attempt),
                FOREIGN KEY (tenant, queue, job_id) REFERENCES {_jobs} (tenant, queue, id) ON DELETE CASCADE
            )
            """, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"""
            CREATE TABLE IF NOT EXISTS {_schedules} (
                tenant varchar(200) NOT NULL,
                queue varchar(200) NOT NULL,
                name varchar(100) NOT NULL,
                id uuid NOT NULL,
                job_type varchar(200) NOT NULL,
                payload bytea NOT NULL CHECK (octet_length(payload) <= {_options.MaximumPayloadBytes}),
                maximum_attempts integer NOT NULL CHECK (maximum_attempts BETWEEN 1 AND 10000),
                interval_ms bigint NOT NULL CHECK (interval_ms BETWEEN 100 AND 31536000000),
                next_at timestamptz NOT NULL,
                misfire smallint NOT NULL CHECK (misfire IN (0, 1)),
                PRIMARY KEY (tenant, queue, name)
            )
            """, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            $"CREATE INDEX IF NOT EXISTS schedules_due ON {_schedules} (tenant, queue, next_at)", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<Guid> EnqueueAsync(JobRequest request) => EnqueueAsync(request, CancellationToken.None);

    public async ValueTask<Guid> EnqueueAsync(JobRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await EnqueueAsync(request, connection, transaction: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Enqueues in a caller transaction; rollback also rolls back the job and its deduplication key.</summary>
    public ValueTask<Guid> EnqueueAsync(
        JobRequest request, BlueTuskTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var connection = transaction.Connection ?? throw new InvalidOperationException("A live transaction is required.");
        return EnqueueAsync(request, connection, transaction, cancellationToken);
    }

    private async ValueTask<Guid> EnqueueAsync(
        JobRequest request, BlueTuskConnection connection, BlueTuskTransaction? transaction, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        using var activity = JobTelemetry.ActivitySource.StartActivity("jobs.enqueue");
        await using var command = CreateCommand(connection, transaction, $"""
            INSERT INTO {_jobs} AS j
                (tenant, queue, id, job_type, payload, dedup_key, available_at, maximum_attempts)
            VALUES (@tenant, @queue, @id, @type, @payload, @dedup, clock_timestamp() + @delay * interval '1 millisecond', @maximum)
            ON CONFLICT (tenant, queue, dedup_key) DO UPDATE SET dedup_key = EXCLUDED.dedup_key
            WHERE j.job_type = EXCLUDED.job_type AND j.payload = EXCLUDED.payload AND j.maximum_attempts = EXCLUDED.maximum_attempts
            RETURNING id
            """);
        AddScope(command, request.Scope);
        Add(command, "id", Guid.NewGuid());
        Add(command, "type", request.JobType);
        Add(command, "payload", request.Payload.ToArray());
        Add(command, "dedup", request.DeduplicationKey);
        Add(command, "delay", request.Delay.TotalMilliseconds);
        Add(command, "maximum", request.MaximumAttempts);
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is Guid id ? id : throw new InvalidOperationException("Job deduplication key conflicts with a different job contract.");
    }

    /// <summary>Claims only this tenant and queue. An optional type allowlist prevents rolling-deployment consumers stealing unknown types.</summary>
    public async ValueTask<IReadOnlyList<JobLease>> ClaimAsync(
        JobScope scope, string owner, int maximumCount, TimeSpan leaseDuration,
        IReadOnlyCollection<string>? jobTypes = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        JobValidation.Name(owner, nameof(owner));
        ValidateBatch(maximumCount);
        JobValidation.Duration(leaseDuration, nameof(leaseDuration), TimeSpan.FromDays(1));
        if (jobTypes is { Count: 0 })
        {
            return Array.Empty<JobLease>();
        }

        if (jobTypes is { Count: > 128 })
        {
            throw new ArgumentOutOfRangeException(nameof(jobTypes));
        }

        string[] types = jobTypes?.ToArray() ?? [];
        foreach (string type in types)
        {
            JobValidation.Name(type, nameof(jobTypes));
        }

        string typeFilter = types.Length == 0 ? string.Empty : "AND j.job_type IN (" + string.Join(',', types.Select((_, index) => "@type" + index)) + ")";
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, transaction: null, $"""
            WITH db_now AS MATERIALIZED (SELECT clock_timestamp() AS now),
            pending AS MATERIALIZED (
                SELECT j.id, j.available_at AS due_at, octet_length(j.payload) AS payload_bytes FROM {_jobs} j, db_now n
                WHERE j.tenant = @tenant AND j.queue = @queue {typeFilter}
                  AND j.status = 0 AND j.available_at <= n.now
                ORDER BY j.available_at, j.id
                FOR UPDATE OF j SKIP LOCKED LIMIT @count
            ), expired AS MATERIALIZED (
                SELECT j.id, j.lease_expires AS due_at, octet_length(j.payload) AS payload_bytes FROM {_jobs} j, db_now n
                WHERE j.tenant = @tenant AND j.queue = @queue {typeFilter}
                  AND j.status = 1 AND j.lease_expires <= n.now
                ORDER BY j.lease_expires, j.id
                FOR UPDATE OF j SKIP LOCKED LIMIT @count
            ), due AS MATERIALIZED (
                SELECT id FROM (
                    SELECT id, due_at, sum(payload_bytes) OVER (ORDER BY due_at, id) AS total_bytes
                    FROM (SELECT * FROM pending UNION ALL SELECT * FROM expired) candidates
                ) bounded WHERE total_bytes <= @bytes ORDER BY due_at, id LIMIT @count
            ), changed AS (
                UPDATE {_jobs} j SET
                    status = CASE WHEN j.attempts >= j.maximum_attempts THEN 3 ELSE 1 END,
                    attempts = CASE WHEN j.attempts >= j.maximum_attempts THEN j.attempts ELSE j.attempts + 1 END,
                    fencing_token = j.fencing_token + 1,
                    lease_owner = CASE WHEN j.attempts >= j.maximum_attempts THEN NULL ELSE @owner END,
                    lease_expires = CASE WHEN j.attempts >= j.maximum_attempts THEN NULL ELSE n.now + @lease * interval '1 millisecond' END,
                    completed_at = CASE WHEN j.attempts >= j.maximum_attempts THEN n.now ELSE NULL END,
                    last_failure_code = CASE WHEN j.status = 1 THEN 'lease_expired' ELSE j.last_failure_code END
                FROM due d, db_now n WHERE j.id = d.id AND j.tenant = @tenant AND j.queue = @queue
                RETURNING j.*
            ), expired_history AS (
                UPDATE {_history} h SET outcome = 'expired', finished_at = n.now, failure_code = 'lease_expired'
                FROM changed c, db_now n
                WHERE h.tenant = c.tenant AND h.queue = c.queue AND h.job_id = c.id AND h.finished_at IS NULL
                    AND h.attempt > c.attempts - @history
            ), history AS (
                INSERT INTO {_history} (tenant, queue, job_id, attempt, fencing_token, started_at, outcome)
                SELECT c.tenant, c.queue, c.id, c.attempts, c.fencing_token, n.now, 'running'
                FROM changed c, db_now n WHERE c.status = 1
                RETURNING job_id
            ), trimmed AS (
                DELETE FROM {_history} h USING changed c
                WHERE h.tenant = c.tenant AND h.queue = c.queue AND h.job_id = c.id AND h.attempt <= c.attempts - @history
            )
            SELECT id, job_type, payload, attempts, maximum_attempts, fencing_token, lease_expires
            FROM changed WHERE status = 1
            """);
        AddScope(command, scope);
        Add(command, "count", maximumCount);
        Add(command, "owner", owner);
        Add(command, "lease", leaseDuration.TotalMilliseconds);
        Add(command, "history", _options.MaximumHistoryEntries);
        Add(command, "bytes", _options.MaximumClaimPayloadBytes);
        for (int index = 0; index < types.Length; index++)
        {
            Add(command, "type" + index, types[index]);
        }

        var leases = new List<JobLease>(maximumCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            leases.Add(new JobLease(reader.GetGuid(0), scope, reader.GetString(1), reader.GetFieldValue<byte[]>(2),
                reader.GetInt32(3), reader.GetInt32(4), owner, reader.GetInt64(5), reader.GetFieldValue<DateTimeOffset>(6)));
        }

        JobTelemetry.Claimed.Add(leases.Count);
        return leases;
    }

    public async ValueTask<bool> HeartbeatAsync(JobLease lease, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ValidateLease(lease);
        JobValidation.Duration(duration, nameof(duration), TimeSpan.FromDays(1));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, transaction: null, $"""
            UPDATE {_jobs} SET lease_expires = clock_timestamp() + @duration * interval '1 millisecond'
            WHERE tenant = @tenant AND queue = @queue AND id = @id AND status = 1
              AND lease_owner = @owner AND fencing_token = @token AND lease_expires > clock_timestamp()
            """);
        AddLease(command, lease);
        Add(command, "duration", duration.TotalMilliseconds);
        return RecordFencing(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1);
    }

    public ValueTask<bool> CompleteAsync(JobLease lease, CancellationToken cancellationToken = default) =>
        FinishAttemptAsync(lease, succeeded: true, retryable: false, failureCode: null, TimeSpan.Zero, cancellationToken);

    /// <summary>Only a stable caller-supplied code is persisted; exception messages and stack traces are never stored.</summary>
    public ValueTask<bool> FailAsync(
        JobLease lease, string failureCode, TimeSpan retryDelay, bool retryable = true, CancellationToken cancellationToken = default)
    {
        JobValidation.FailureCode(failureCode);
        JobValidation.Duration(retryDelay, nameof(retryDelay), TimeSpan.FromDays(365), allowZero: true);
        return FinishAttemptAsync(lease, succeeded: false, retryable, failureCode, retryDelay, cancellationToken);
    }

    private async ValueTask<bool> FinishAttemptAsync(
        JobLease lease, bool succeeded, bool retryable, string? failureCode, TimeSpan retryDelay, CancellationToken cancellationToken)
    {
        ValidateLease(lease);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, transaction: null, $"""
            WITH changed AS (
                UPDATE {_jobs} SET
                    status = CASE WHEN @succeeded THEN 2 WHEN @retryable AND attempts < maximum_attempts THEN 0 ELSE 3 END,
                    available_at = CASE WHEN @succeeded THEN available_at ELSE clock_timestamp() + @delay * interval '1 millisecond' END,
                    completed_at = CASE WHEN NOT @succeeded AND @retryable AND attempts < maximum_attempts THEN NULL ELSE clock_timestamp() END,
                    lease_owner = NULL, lease_expires = NULL, last_failure_code = @failure
                WHERE tenant = @tenant AND queue = @queue AND id = @id AND status = 1
                  AND lease_owner = @owner AND fencing_token = @token AND lease_expires > clock_timestamp()
                RETURNING tenant, queue, id, attempts
            ), history AS (
                UPDATE {_history} h SET finished_at = clock_timestamp(),
                    outcome = CASE WHEN @succeeded THEN 'succeeded' ELSE 'failed' END, failure_code = @failure
                FROM changed c WHERE h.tenant = c.tenant AND h.queue = c.queue AND h.job_id = c.id AND h.attempt = c.attempts
            )
            SELECT count(*)::integer FROM changed
            """);
        AddLease(command, lease);
        Add(command, "succeeded", succeeded);
        Add(command, "retryable", retryable);
        Add(command, "failure", failureCode);
        Add(command, "delay", retryDelay.TotalMilliseconds);
        bool stored = RecordFencing((int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 1);
        if (stored)
        {
            (succeeded ? JobTelemetry.Succeeded : JobTelemetry.Failed).Add(1);
        }

        return stored;
    }

    /// <summary>Cancellation revokes the lease immediately. Workers observe revocation at their next heartbeat.</summary>
    public async ValueTask<bool> CancelAsync(JobScope scope, Guid id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, transaction: null, $"""
            WITH changed AS (
                UPDATE {_jobs} SET status = 4, completed_at = clock_timestamp(), lease_owner = NULL,
                    lease_expires = NULL, fencing_token = fencing_token + 1
                WHERE tenant = @tenant AND queue = @queue AND id = @id AND status IN (0, 1)
                RETURNING tenant, queue, id, attempts
            ), history AS (
                UPDATE {_history} h SET finished_at = clock_timestamp(), outcome = 'canceled'
                FROM changed c WHERE h.tenant = c.tenant AND h.queue = c.queue AND h.job_id = c.id
                    AND h.attempt = c.attempts AND h.finished_at IS NULL
            ) SELECT count(*)::integer FROM changed
            """);
        AddScope(command, scope);
        Add(command, "id", id);
        return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 1;
    }

    public async ValueTask<JobSnapshot?> ReadAsync(JobScope scope, Guid id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, transaction: null, $"""
            SELECT id, job_type, status, attempts, maximum_attempts, fencing_token, available_at, completed_at, last_failure_code
            FROM {_jobs} WHERE tenant = @tenant AND queue = @queue AND id = @id
            """);
        AddScope(command, scope);
        Add(command, "id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new JobSnapshot(reader.GetGuid(0), scope, reader.GetString(1), (JobStatus)reader.GetInt16(2),
                reader.GetInt32(3), reader.GetInt32(4), reader.GetInt64(5), reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7), reader.IsDBNull(8) ? null : reader.GetString(8))
            : null;
    }

    public async ValueTask<IReadOnlyList<JobAttempt>> ReadHistoryAsync(JobScope scope, Guid id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, transaction: null, $"""
            SELECT attempt, fencing_token, started_at, finished_at, outcome, failure_code FROM {_history}
            WHERE tenant = @tenant AND queue = @queue AND job_id = @id ORDER BY attempt DESC LIMIT @limit
            """);
        AddScope(command, scope);
        Add(command, "id", id);
        Add(command, "limit", _options.MaximumHistoryEntries);
        var history = new List<JobAttempt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            history.Add(new JobAttempt(reader.GetInt32(0), reader.GetInt64(1), reader.GetFieldValue<DateTimeOffset>(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return history;
    }

    /// <summary>Bounded physical deletion. This also releases deduplication identities and cascades attempt history.</summary>
    public async ValueTask<int> PruneAsync(JobScope scope, TimeSpan retention, int maximumCount, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        JobValidation.Duration(retention, nameof(retention), TimeSpan.FromDays(36500), allowZero: true);
        ValidateBatch(maximumCount);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, transaction: null, $"""
            WITH expired AS (
                SELECT id FROM {_jobs} WHERE tenant = @tenant AND queue = @queue AND status IN (2, 3, 4)
                    AND completed_at <= clock_timestamp() - @retention * interval '1 millisecond'
                ORDER BY completed_at, id FOR UPDATE SKIP LOCKED LIMIT @count
            ) DELETE FROM {_jobs} j USING expired e WHERE j.tenant = @tenant AND j.queue = @queue AND j.id = e.id
            """);
        AddScope(command, scope);
        Add(command, "retention", retention.TotalMilliseconds);
        Add(command, "count", maximumCount);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ValidateRequest(JobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Scope);
        JobValidation.Name(request.JobType, nameof(request.JobType));
        if (request.DeduplicationKey is not null)
        {
            JobValidation.Name(request.DeduplicationKey, nameof(request.DeduplicationKey));
            if (request.DeduplicationKey.StartsWith("schedule:", StringComparison.Ordinal))
            {
                throw new ArgumentException("The schedule deduplication namespace is reserved.", nameof(request));
            }
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Payload.Length, _options.MaximumPayloadBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaximumAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.MaximumAttempts, 10000);
        JobValidation.Duration(request.Delay, nameof(request.Delay), TimeSpan.FromDays(365), allowZero: true);
    }

    private void ValidateBatch(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _options.MaximumClaimBatch);
    }

    private static void ValidateLease(JobLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(lease.Scope);
        JobValidation.Name(lease.Owner, nameof(lease.Owner));
        ArgumentOutOfRangeException.ThrowIfLessThan(lease.FencingToken, 1);
    }

    private static bool RecordFencing(bool result)
    {
        if (!result)
        {
            JobTelemetry.Fenced.Add(1);
        }

        return result;
    }

    private BlueTuskCommand CreateCommand(BlueTuskConnection connection, BlueTuskTransaction? transaction, string sql) =>
        new(sql, connection) { Transaction = transaction, CommandTimeout = _options.CommandTimeoutSeconds };

    private async ValueTask ExecuteAsync(BlueTuskConnection connection, BlueTuskTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddScope(DbCommand command, JobScope scope)
    {
        Add(command, "tenant", scope.Tenant);
        Add(command, "queue", scope.Queue);
    }

    private static void AddLease(DbCommand command, JobLease lease)
    {
        AddScope(command, lease.Scope);
        Add(command, "id", lease.JobId);
        Add(command, "owner", lease.Owner);
        Add(command, "token", lease.FencingToken);
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        if (value is null)
        {
            parameter.DbType = DbType.String;
        }

        command.Parameters.Add(parameter);
    }
}
