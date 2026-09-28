using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueTusk.Data;
using BlueTusk.Jobs;

namespace BlueTusk.Workflows;

/// <summary>Colocated durable workflow state and Jobs dispatch. The caller owns the data source.</summary>
public sealed partial class PostgreSqlWorkflowStore
{
    internal const string TimerJobType = "bluetusk.workflow.timer.v1";
    internal static string ActivityJobType(string activity) => "bluetusk.workflow.activity." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(activity)));
    private readonly BlueTuskDataSource _source;
    private readonly WorkflowOptions _options;
    private readonly string _schema;

    public PostgreSqlWorkflowStore(BlueTuskDataSource source, WorkflowOptions? options = null, JobStoreOptions? jobOptions = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _options = options ?? new WorkflowOptions();
        _options.Validate();
        _schema = "\"" + _options.Schema + "\"";
        _source = source;
        Jobs = new PostgreSqlJobStore(source, jobOptions);
    }

    public PostgreSqlJobStore Jobs { get; }
    internal int MaximumBatchSize => _options.MaximumBatchSize;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await Jobs.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = Command(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended(@schema, 0))"))
        {
            Add(command, "schema", _options.Schema);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(connection, transaction, $"CREATE SCHEMA IF NOT EXISTS {_schema}", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"CREATE TABLE IF NOT EXISTS {_schema}.settings (singleton boolean PRIMARY KEY CHECK(singleton), format integer NOT NULL, fingerprint bytea NOT NULL)", cancellationToken).ConfigureAwait(false);
        int[] bounds = [_options.MaximumNodes, _options.MaximumDependenciesPerNode, _options.MaximumDefinitionBytes,
            _options.MaximumInputBytes, _options.MaximumResultBytes, _options.MaximumActivityInputBytes,
            _options.MaximumSignalBytes, _options.MaximumSignalsPerWorkflow, _options.MaximumHistoryEntries];
        byte[] fingerprint = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(':', bounds.Select(value => value.ToString(CultureInfo.InvariantCulture)))));
        await using (var command = Command(connection, transaction, $"INSERT INTO {_schema}.settings VALUES (true, 1, @fingerprint) ON CONFLICT DO NOTHING"))
        {
            Add(command, "fingerprint", fingerprint);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = Command(connection, transaction, $"SELECT fingerprint FROM {_schema}.settings WHERE singleton AND format = 1"))
        {
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not byte[] existing || !existing.AsSpan().SequenceEqual(fingerprint))
            {
                throw new InvalidOperationException("Workflow schema format or durable admission limits do not match this store.");
            }
        }

        await ExecuteAsync(connection, transaction, $"""
            CREATE TABLE IF NOT EXISTS {_schema}.definitions (
                tenant varchar(200) NOT NULL, queue varchar(200) NOT NULL, name varchar(200) NOT NULL,
                version integer NOT NULL CHECK(version > 0), definition bytea NOT NULL CHECK(octet_length(definition) <= {_options.MaximumDefinitionBytes}),
                fingerprint bytea NOT NULL, PRIMARY KEY(tenant, queue, name, version)
            )
            """, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"""
            CREATE TABLE IF NOT EXISTS {_schema}.instances (
                tenant varchar(200) NOT NULL, queue varchar(200) NOT NULL, id uuid NOT NULL,
                definition varchar(200) NOT NULL, version integer NOT NULL, started_version integer NOT NULL,
                input bytea NOT NULL CHECK(octet_length(input) <= {_options.MaximumInputBytes}), dedup_key varchar(200) NULL,
                status smallint NOT NULL CHECK(status BETWEEN 0 AND 4), terminal_status smallint NOT NULL DEFAULT 3 CHECK(terminal_status IN (3,4)),
                revision bigint NOT NULL DEFAULT 0 CHECK(revision >= 0), history_sequence bigint NOT NULL DEFAULT 0 CHECK(history_sequence <= {_options.MaximumHistoryEntries}),
                signal_count integer NOT NULL DEFAULT 0 CHECK(signal_count <= {_options.MaximumSignalsPerWorkflow}),
                created_at timestamptz NOT NULL DEFAULT clock_timestamp(), completed_at timestamptz NULL,
                failure_code varchar(64) NULL, PRIMARY KEY(tenant, queue, id), UNIQUE(tenant, queue, dedup_key),
                FOREIGN KEY(tenant, queue, definition, version) REFERENCES {_schema}.definitions(tenant, queue, name, version),
                CHECK((status IN (2,3,4)) = (completed_at IS NOT NULL))
            )
            """, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"""
            CREATE TABLE IF NOT EXISTS {_schema}.nodes (
                tenant varchar(200) NOT NULL, queue varchar(200) NOT NULL, workflow_id uuid NOT NULL, id varchar(100) NOT NULL,
                status smallint NOT NULL DEFAULT 0 CHECK(status BETWEEN 0 AND 7), fencing_token bigint NOT NULL DEFAULT 0,
                job_id uuid NULL, compensation_job_id uuid NULL, result bytea NOT NULL DEFAULT ''::bytea CHECK(octet_length(result) <= {_options.MaximumResultBytes}),
                completion_sequence bigint NULL, failure_code varchar(64) NULL,
                PRIMARY KEY(tenant, queue, workflow_id, id),
                FOREIGN KEY(tenant, queue, workflow_id) REFERENCES {_schema}.instances(tenant, queue, id) ON DELETE CASCADE
            )
            """, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"""
            CREATE TABLE IF NOT EXISTS {_schema}.history (
                tenant varchar(200) NOT NULL, queue varchar(200) NOT NULL, workflow_id uuid NOT NULL, sequence bigint NOT NULL,
                occurred_at timestamptz NOT NULL DEFAULT clock_timestamp(), event varchar(32) NOT NULL, node_id varchar(100) NULL, code varchar(64) NULL,
                PRIMARY KEY(tenant, queue, workflow_id, sequence),
                FOREIGN KEY(tenant, queue, workflow_id) REFERENCES {_schema}.instances(tenant, queue, id) ON DELETE CASCADE
            )
            """, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"""
            CREATE TABLE IF NOT EXISTS {_schema}.signals (
                tenant varchar(200) NOT NULL, queue varchar(200) NOT NULL, workflow_id uuid NOT NULL,
                name varchar(100) NOT NULL, identity varchar(200) NOT NULL,
                payload bytea NOT NULL CHECK(octet_length(payload) <= {_options.MaximumSignalBytes}),
                received_at timestamptz NOT NULL DEFAULT clock_timestamp(), consumed_by varchar(100) NULL,
                PRIMARY KEY(tenant, queue, workflow_id, name, identity),
                FOREIGN KEY(tenant, queue, workflow_id) REFERENCES {_schema}.instances(tenant, queue, id) ON DELETE CASCADE
            )
            """, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"CREATE INDEX IF NOT EXISTS signals_pending ON {_schema}.signals (tenant, queue, workflow_id, name, received_at, identity) WHERE consumed_by IS NULL", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"CREATE INDEX IF NOT EXISTS nodes_dispatch ON {_schema}.nodes (tenant, queue, workflow_id, id) WHERE status IN (1,2,6)", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"CREATE INDEX IF NOT EXISTS instances_retention ON {_schema}.instances (tenant, queue, completed_at, id) WHERE status IN (2,3,4)", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"CREATE INDEX IF NOT EXISTS instances_active ON {_schema}.instances (tenant, queue, created_at, id) WHERE status IN (0,1)", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"CREATE INDEX IF NOT EXISTS instances_running_count ON {_schema}.instances (tenant, queue) WHERE status = 0", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"CREATE INDEX IF NOT EXISTS instances_compensating_count ON {_schema}.instances (tenant, queue) WHERE status = 1", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Versions are immutable; an identity reused with different node contracts is rejected.</summary>
    public async ValueTask RegisterDefinitionAsync(JobScope scope, WorkflowDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var canonical = WorkflowDefinitionValidation.Canonicalize(definition, _options);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, transaction: null, $"""
            INSERT INTO {_schema}.definitions AS d (tenant, queue, name, version, definition, fingerprint)
            VALUES (@tenant, @queue, @name, @version, @definition, @fingerprint)
            ON CONFLICT (tenant, queue, name, version) DO UPDATE SET fingerprint = EXCLUDED.fingerprint
            WHERE d.definition = EXCLUDED.definition RETURNING version
            """);
        Scope(command, scope);
        Add(command, "name", definition.Name);
        Add(command, "version", definition.Version);
        Add(command, "definition", canonical.Bytes);
        Add(command, "fingerprint", canonical.Fingerprint);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not int)
        {
            throw new InvalidOperationException("Workflow definition version conflicts with a different persisted contract.");
        }
    }

    public ValueTask<WorkflowKey> StartAsync(WorkflowStartRequest request) => StartAsync(request, CancellationToken.None);

    public async ValueTask<WorkflowKey> StartAsync(WorkflowStartRequest request, CancellationToken cancellationToken)
    {
        ValidateStart(request);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var key = await StartAsync(request, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return key;
    }

    /// <summary>Instance, nodes, history and initial Jobs dispatch participate in the caller's transaction.</summary>
    public async ValueTask<WorkflowKey> StartAsync(WorkflowStartRequest request, BlueTuskTransaction transaction, CancellationToken cancellationToken)
    {
        ValidateStart(request);
        ArgumentNullException.ThrowIfNull(transaction);
        var connection = transaction.Connection ?? throw new InvalidOperationException("A live transaction is required.");
        var definition = await DefinitionAsync(connection, transaction, request.Scope, request.Definition, request.Version, cancellationToken).ConfigureAwait(false);
        var key = new WorkflowKey(request.Scope, Guid.NewGuid());
        await using (var command = Command(connection, transaction, $"""
            INSERT INTO {_schema}.instances (tenant, queue, id, definition, version, started_version, input, dedup_key, status)
            VALUES (@tenant, @queue, @id, @definition, @version, @version, @input, @dedup, 0)
            ON CONFLICT (tenant, queue, dedup_key) DO NOTHING RETURNING id
            """))
        {
            Key(command, key);
            Add(command, "definition", request.Definition);
            Add(command, "version", request.Version);
            Add(command, "input", request.Input.ToArray());
            Add(command, "dedup", request.DeduplicationKey);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not Guid)
            {
                await using var existing = Command(connection, transaction, $"""
                    SELECT id, definition, started_version, input FROM {_schema}.instances
                    WHERE tenant = @tenant AND queue = @queue AND dedup_key = @dedup
                    """);
                Scope(existing, request.Scope);
                Add(existing, "dedup", request.DeduplicationKey);
                await using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetString(1) != request.Definition ||
                    reader.GetInt32(2) != request.Version || !reader.GetFieldValue<byte[]>(3).AsSpan().SequenceEqual(request.Input.Span))
                {
                    throw new InvalidOperationException("Workflow deduplication identity conflicts with a different contract or input.");
                }

                return new WorkflowKey(request.Scope, reader.GetGuid(0));
            }
        }

        foreach (var node in definition.Nodes)
        {
            await InsertNodeAsync(connection, transaction, key, node.Id, cancellationToken).ConfigureAwait(false);
        }

        _ = await AppendAsync(connection, transaction, key, "started", node: null, "v" + request.Version.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await AdvanceAsync(connection, transaction, key, definition, cancellationToken).ConfigureAwait(false);
        return key;
    }

    public async ValueTask<WorkflowSnapshot?> ReadAsync(WorkflowKey key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await SnapshotAsync(connection, transaction: null, key, locked: false, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<WorkflowNodeSnapshot>> ReadNodesAsync(WorkflowKey key, CancellationToken cancellationToken = default)
    {
        return await ReadNodesPageAsync(key, afterNodeId: null, Math.Min(_options.MaximumNodes, _options.MaximumBatchSize), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Keyset page bounded by both node count and aggregate result bytes.</summary>
    public async ValueTask<IReadOnlyList<WorkflowNodeSnapshot>> ReadNodesPageAsync(WorkflowKey key, string? afterNodeId,
        int maximumCount, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        Batch(maximumCount);
        if (afterNodeId is not null)
        {
            WorkflowOptions.Name(afterNodeId, nameof(afterNodeId), 100);
        }

        await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, transaction: null, $"""
            WITH selected AS (
                SELECT id, octet_length(result) AS bytes FROM {_schema}.nodes
                WHERE tenant = @tenant AND queue = @queue AND workflow_id = @id AND id > @after ORDER BY id LIMIT @count
            ), bounded AS (SELECT id, sum(bytes) OVER (ORDER BY id) AS total_bytes FROM selected)
            SELECT n.id, n.status, n.fencing_token, n.job_id, n.compensation_job_id, n.result, n.failure_code
            FROM {_schema}.nodes n JOIN bounded b ON b.id = n.id
            WHERE n.tenant = @tenant AND n.queue = @queue AND n.workflow_id = @id AND b.total_bytes <= @bytes ORDER BY n.id
            """);
        Key(command, key);
        Add(command, "after", afterNodeId ?? string.Empty);
        Add(command, "count", maximumCount);
        Add(command, "bytes", _options.MaximumNodeReadBytes);
        var nodes = new List<WorkflowNodeSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            nodes.Add(new(reader.GetString(0), (WorkflowNodeStatus)reader.GetInt16(1), reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.GetFieldValue<byte[]>(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return nodes;
    }

    public async ValueTask<IReadOnlyList<WorkflowHistoryEntry>> ReadHistoryAsync(WorkflowKey key, long afterSequence, int maximumCount, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        Batch(maximumCount);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, transaction: null, $"""
            SELECT sequence, occurred_at, event, node_id, code FROM {_schema}.history
            WHERE tenant = @tenant AND queue = @queue AND workflow_id = @id AND sequence > @after
            ORDER BY sequence LIMIT @count
            """);
        Key(command, key);
        Add(command, "after", afterSequence);
        Add(command, "count", maximumCount);
        var history = new List<WorkflowHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            history.Add(new(reader.GetInt64(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return history;
    }

    public async ValueTask<int> PruneAsync(JobScope scope, TimeSpan retention, int maximumCount, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Batch(maximumCount);
        if (retention < TimeSpan.Zero || retention > TimeSpan.FromDays(36500))
        {
            throw new ArgumentOutOfRangeException(nameof(retention));
        }

        await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, transaction: null, $"""
            WITH expired AS (
                SELECT id FROM {_schema}.instances WHERE tenant = @tenant AND queue = @queue AND status IN (2,3,4)
                    AND completed_at <= clock_timestamp() - @retention * interval '1 millisecond'
                ORDER BY completed_at, id FOR UPDATE SKIP LOCKED LIMIT @count
            ) DELETE FROM {_schema}.instances w USING expired e WHERE w.tenant = @tenant AND w.queue = @queue AND w.id = e.id
            """);
        Scope(command, scope);
        Add(command, "retention", retention.TotalMilliseconds);
        Add(command, "count", maximumCount);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ValidateStart(WorkflowStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Scope);
        WorkflowOptions.Name(request.Definition, nameof(request.Definition));
        WorkflowOptions.Range(request.Version, 1, int.MaxValue, nameof(request.Version));
        if (request.DeduplicationKey is not null)
        {
            WorkflowOptions.Name(request.DeduplicationKey, nameof(request.DeduplicationKey));
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Input.Length, _options.MaximumInputBytes);
    }

    private static void ValidateKey(WorkflowKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(key.Scope);
    }

    private void Batch(int count) => WorkflowOptions.Range(count, 1, _options.MaximumBatchSize, nameof(count));
    private BlueTuskCommand Command(BlueTuskConnection connection, BlueTuskTransaction? transaction, string sql) =>
        new(sql, connection) { Transaction = transaction, CommandTimeout = _options.CommandTimeoutSeconds };

    private async ValueTask ExecuteAsync(BlueTuskConnection connection, BlueTuskTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, sql);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Scope(DbCommand command, JobScope scope)
    {
        Add(command, "tenant", scope.Tenant);
        Add(command, "queue", scope.Queue);
    }

    private static void Key(DbCommand command, WorkflowKey key)
    {
        Scope(command, key.Scope);
        Add(command, "id", key.Id);
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
