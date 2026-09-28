using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BlueTusk.Client;
using BlueTusk.Data;

namespace BlueTusk.Schema;

/// <summary>Durable, fenced PostgreSQL step journal. The caller owns the data source and external effects.</summary>
public sealed class PostgreSqlSchemaDeploymentCoordinator
{
    private readonly BlueTuskDataSource _source;
    internal BlueTuskDataSource Source => _source;
    private readonly string _schema;
    private readonly string _schemaName;
    private readonly string _settings;
    private readonly string _deployments;
    private readonly string _attempts;
    private readonly int _timeout;
    private static readonly string LayoutFingerprint = Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes("BlueTusk.Schema.PostgreSqlDeploymentJournal:1;settings;deployments;step_attempts;primary-key;foreign-key;completed-index")));

    public PostgreSqlSchemaDeploymentCoordinator(BlueTuskDataSource dataSource, string schema = "bluetusk_schema",
        int commandTimeoutSeconds = 30)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        Name(schema, nameof(schema));
        ArgumentOutOfRangeException.ThrowIfLessThan(commandTimeoutSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(commandTimeoutSeconds, 300);
        _source = dataSource; _schemaName = schema; _schema = '"' + schema + '"';
        if (Encoding.UTF8.GetByteCount(schema) > 63)
        { throw new ArgumentException("PostgreSQL schema names are bounded to 63 bytes.", nameof(schema)); }
        _settings = _schema + ".settings";
        _deployments = _schema + ".deployments"; _attempts = _schema + ".step_attempts";
        _timeout = commandTimeoutSeconds;
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = Deadline(cancellationToken);
        var token = deadline.Token;
        await using var connection = await _source.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await using (var command = Command(connection, transaction,
            "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(@schema, 0))"))
        { Add(command, "schema", _schema); _ = await command.ExecuteScalarAsync(token).ConfigureAwait(false); }
        await ExecuteAsync(connection, transaction, $"CREATE SCHEMA IF NOT EXISTS {_schema}", token).ConfigureAwait(false);
        await CheckExistingStorageSetAsync(connection, transaction, token).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"""
            CREATE TABLE IF NOT EXISTS {_settings} (
                singleton boolean PRIMARY KEY CHECK (singleton),
                format_version integer NOT NULL,
                layout_fingerprint char(64) NOT NULL
            )
            """, token).ConfigureAwait(false);
        await using (var command = Command(connection, transaction, $"""
            INSERT INTO {_settings} (singleton, format_version, layout_fingerprint)
            VALUES (true, 1, @layout) ON CONFLICT (singleton) DO NOTHING
            """))
        { Add(command, "layout", LayoutFingerprint); _ = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
        await using (var command = Command(connection, transaction,
            $"SELECT format_version, layout_fingerprint FROM {_settings} WHERE singleton"))
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetInt32(0) != 1 ||
                reader.GetString(1) != LayoutFingerprint || await reader.ReadAsync(token).ConfigureAwait(false))
            { throw new InvalidOperationException("Schema deployment journal format or layout fingerprint is incompatible."); }
        }
        await ExecuteAsync(connection, transaction, $"""
            CREATE TABLE IF NOT EXISTS {_deployments} (
                deployment_id varchar(128) PRIMARY KEY,
                format_version integer NOT NULL CHECK (format_version = 1),
                plan_fingerprint char(64) NOT NULL,
                definition_fingerprint char(64) NOT NULL,
                before_fingerprint char(64) NOT NULL,
                after_fingerprint char(64) NOT NULL,
                lease_owner varchar(128),
                lease_expires timestamptz,
                fencing_token bigint NOT NULL DEFAULT 0 CHECK (fencing_token >= 0),
                created_at timestamptz NOT NULL DEFAULT pg_catalog.clock_timestamp(),
                CHECK ((lease_owner IS NULL) = (lease_expires IS NULL))
            )
            """, token).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"""
            CREATE TABLE IF NOT EXISTS {_attempts} (
                deployment_id varchar(128) NOT NULL REFERENCES {_deployments}(deployment_id),
                step_id varchar(256) NOT NULL,
                attempt integer NOT NULL CHECK (attempt BETWEEN 1 AND 1000),
                action_fingerprint char(64) NOT NULL,
                kind smallint NOT NULL CHECK (kind BETWEEN 0 AND 1),
                state smallint NOT NULL CHECK (state BETWEEN 0 AND 2),
                fencing_token bigint NOT NULL CHECK (fencing_token > 0),
                observed_fingerprint char(64),
                evidence_sha256 char(64),
                started_at timestamptz NOT NULL DEFAULT pg_catalog.clock_timestamp(),
                finished_at timestamptz,
                PRIMARY KEY (deployment_id, step_id, attempt),
                CHECK ((state = 0) = (finished_at IS NULL))
            )
            """, token).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            $"CREATE INDEX IF NOT EXISTS schema_step_completed ON {_attempts} (deployment_id, step_id) WHERE state = 1", token)
            .ConfigureAwait(false);
        await VerifyStorageShapeAsync(connection, transaction, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    public async ValueTask RegisterAsync(string deploymentId, SchemaDeploymentDefinition definition,
        CancellationToken cancellationToken = default)
    {
        Validate(deploymentId, definition);
        using var deadline = Deadline(cancellationToken);
        var token = deadline.Token;
        await using var connection = await _source.OpenConnectionAsync(token).ConfigureAwait(false);
        await using (var command = Command(connection, null, $"""
            INSERT INTO {_deployments} (deployment_id, format_version, plan_fingerprint, definition_fingerprint,
                before_fingerprint, after_fingerprint)
            VALUES (@id, 1, @plan, @definition, @before, @after)
            ON CONFLICT (deployment_id) DO NOTHING
            """))
        {
            Add(command, "id", deploymentId); Add(command, "plan", definition.Plan.Fingerprint);
            Add(command, "definition", definition.Fingerprint); Add(command, "before", definition.Plan.BeforeFingerprint);
            Add(command, "after", definition.Plan.AfterFingerprint);
            _ = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await CheckDefinitionAsync(connection, null, deploymentId, definition, token).ConfigureAwait(false);
    }

    /// <summary>Acquires an expired or unowned lease. A null result means another owner still holds it.</summary>
    public async ValueTask<SchemaDeploymentLease?> AcquireAsync(string deploymentId, SchemaDeploymentDefinition definition,
        string owner, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        Validate(deploymentId, definition); Name(owner, nameof(owner)); Duration(leaseDuration);
        using var deadline = Deadline(cancellationToken);
        var token = deadline.Token;
        await using var connection = await _source.OpenConnectionAsync(token).ConfigureAwait(false);
        await CheckDefinitionAsync(connection, null, deploymentId, definition, token).ConfigureAwait(false);
        await using var command = Command(connection, null, $"""
            UPDATE {_deployments} SET lease_owner = @owner,
                lease_expires = pg_catalog.clock_timestamp() + @milliseconds * interval '1 millisecond',
                fencing_token = fencing_token + 1
            WHERE deployment_id = @id AND definition_fingerprint = @definition
                AND (lease_expires IS NULL OR lease_expires <= pg_catalog.clock_timestamp())
            RETURNING fencing_token
            """);
        Add(command, "owner", owner); Add(command, "milliseconds", (long)leaseDuration.TotalMilliseconds);
        Add(command, "id", deploymentId); Add(command, "definition", definition.Fingerprint);
        var value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        return value is null or DBNull ? null : new(deploymentId, owner, Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }

    public async ValueTask<bool> RenewAsync(SchemaDeploymentLease lease, SchemaDeploymentDefinition definition,
        TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        ValidateLease(lease, definition); Duration(leaseDuration);
        using var deadline = Deadline(cancellationToken);
        var token = deadline.Token;
        await using var connection = await _source.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = Command(connection, null, $"""
            UPDATE {_deployments} SET lease_expires = pg_catalog.clock_timestamp() + @milliseconds * interval '1 millisecond'
            WHERE deployment_id = @id AND definition_fingerprint = @definition AND lease_owner = @owner
                AND fencing_token = @fence AND lease_expires > pg_catalog.clock_timestamp()
            """);
        BindLease(command, lease, definition); Add(command, "milliseconds", (long)leaseDuration.TotalMilliseconds);
        return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
    }

    public async ValueTask<bool> ReleaseAsync(SchemaDeploymentLease lease, SchemaDeploymentDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ValidateLease(lease, definition);
        using var deadline = Deadline(cancellationToken);
        var token = deadline.Token;
        await using var connection = await _source.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = Command(connection, null, $"""
            UPDATE {_deployments} SET lease_owner = NULL, lease_expires = NULL
            WHERE deployment_id = @id AND definition_fingerprint = @definition AND lease_owner = @owner
                AND fencing_token = @fence AND lease_expires > pg_catalog.clock_timestamp()
            """);
        BindLease(command, lease, definition);
        return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
    }

    /// <summary>Creates a pending journal attempt before the caller invokes an external effect.</summary>
    public async ValueTask<SchemaDeploymentStepAttempt> BeginExternalStepAsync(SchemaDeploymentLease lease,
        SchemaDeploymentDefinition definition, string stepId, CancellationToken cancellationToken = default)
    {
        var action = ValidateStep(lease, definition, stepId, SchemaDeploymentActionKind.External);
        using var deadline = Deadline(cancellationToken);
        var token = deadline.Token;
        await using var connection = await _source.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await LockLeaseAsync(connection, transaction, lease, definition, token).ConfigureAwait(false);
        var previous = await LatestAsync(connection, transaction, lease.DeploymentId, stepId, token).ConfigureAwait(false);
        if (previous is { State: SchemaDeploymentAttemptState.Pending or SchemaDeploymentAttemptState.Completed })
        { await transaction.CommitAsync(token).ConfigureAwait(false); return previous; }
        await CheckPrerequisitesAsync(connection, transaction, lease.DeploymentId, definition.Plan.StepIndex[stepId], token)
            .ConfigureAwait(false);
        var next = checked((previous?.Attempt ?? 0) + 1);
        if (next > 1000) { throw new SchemaCaptureLimitException(); }
        await InsertAttemptAsync(connection, transaction, lease, action, next,
            SchemaDeploymentAttemptState.Pending, null, null, token).ConfigureAwait(false);
        await AssertLeaseAsync(connection, transaction, lease, definition, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return new(stepId, next, SchemaDeploymentAttemptState.Pending, true, action.Fingerprint, null, null);
    }

    /// <summary>Records external evidence, including after an unknown outcome under a new fenced lease.</summary>
    public ValueTask CompleteExternalStepAsync(SchemaDeploymentLease lease, SchemaDeploymentDefinition definition,
        string stepId, int attempt, string evidenceSha256, string? observedFingerprint = null,
        CancellationToken cancellationToken = default) =>
        FinishExternalAsync(lease, definition, stepId, attempt, SchemaDeploymentAttemptState.Completed,
            evidenceSha256, observedFingerprint, cancellationToken);

    /// <summary>Explicitly attests that a pending external action had no effect before retrying it.</summary>
    public ValueTask ReconcileNotAppliedAsync(SchemaDeploymentLease lease, SchemaDeploymentDefinition definition,
        string stepId, int attempt, string evidenceSha256, CancellationToken cancellationToken = default) =>
        FinishExternalAsync(lease, definition, stepId, attempt, SchemaDeploymentAttemptState.ReconciledNotApplied,
            evidenceSha256, null, cancellationToken);

    /// <summary>Executes one prepared DDL statement and journals completion in its owning transaction.</summary>
    public async ValueTask<SchemaDeploymentStepAttempt> ExecuteTransactionalSqlStepAsync(SchemaDeploymentLease lease,
        SchemaDeploymentDefinition definition, string stepId, CancellationToken cancellationToken = default)
    {
        var action = ValidateStep(lease, definition, stepId, SchemaDeploymentActionKind.TransactionalSql);
        using var deadline = Deadline(cancellationToken);
        var token = deadline.Token;
        await using var connection = await _source.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await LockLeaseAsync(connection, transaction, lease, definition, token).ConfigureAwait(false);
        var previous = await LatestAsync(connection, transaction, lease.DeploymentId, stepId, token).ConfigureAwait(false);
        if (previous is { State: SchemaDeploymentAttemptState.Completed })
        { await transaction.CommitAsync(token).ConfigureAwait(false); return previous; }
        if (previous is { State: SchemaDeploymentAttemptState.Pending })
        { throw new InvalidOperationException("A pending external action must be reconciled before SQL execution."); }
        await CheckPrerequisitesAsync(connection, transaction, lease.DeploymentId, definition.Plan.StepIndex[stepId], token)
            .ConfigureAwait(false);
        var next = checked((previous?.Attempt ?? 0) + 1);
        if (next > 1000) { throw new SchemaCaptureLimitException(); }
        await ExecuteAsync(connection, transaction, "SET LOCAL search_path = pg_catalog", token).ConfigureAwait(false);
        await using (var sql = Command(connection, transaction, action.Content))
        {
            // PostgreSQL's extended Parse rejects multiple statements. BlueTusk preparation is required here.
            sql.ExecutionMode = BlueTuskCommandExecutionMode.Extended;
            await sql.PrepareAsync(token).ConfigureAwait(false);
            _ = await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        // This row remains locked through commit, so another owner cannot acquire while DDL is in flight.
        await AssertLeaseAsync(connection, transaction, lease, definition, token).ConfigureAwait(false);
        await InsertAttemptAsync(connection, transaction, lease, action, next,
            SchemaDeploymentAttemptState.Completed, null, action.Fingerprint, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return new(stepId, next, SchemaDeploymentAttemptState.Completed, true, action.Fingerprint, null, action.Fingerprint);
    }

    public async ValueTask<SchemaDeploymentStepAttempt?> ReadStepAsync(string deploymentId,
        SchemaDeploymentDefinition definition, string stepId, CancellationToken cancellationToken = default)
    {
        Validate(deploymentId, definition);
        if (!definition.ActionIndex.ContainsKey(stepId)) { throw new ArgumentException("Unknown deployment step.", nameof(stepId)); }
        using var deadline = Deadline(cancellationToken);
        var token = deadline.Token;
        await using var connection = await _source.OpenConnectionAsync(token).ConfigureAwait(false);
        await CheckDefinitionAsync(connection, null, deploymentId, definition, token).ConfigureAwait(false);
        return await LatestAsync(connection, null, deploymentId, stepId, token).ConfigureAwait(false);
    }

    private async ValueTask FinishExternalAsync(SchemaDeploymentLease lease, SchemaDeploymentDefinition definition,
        string stepId, int attempt, SchemaDeploymentAttemptState outcome, string evidenceSha256,
        string? observedFingerprint, CancellationToken cancellationToken)
    {
        var action = ValidateStep(lease, definition, stepId, SchemaDeploymentActionKind.External);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        Hash(evidenceSha256, nameof(evidenceSha256));
        var step = definition.Plan.StepIndex[stepId];
        var required = outcome == SchemaDeploymentAttemptState.Completed ? step.Phase switch
        {
            SchemaDeploymentPhase.VerifyBaseline => definition.Plan.BeforeFingerprint,
            SchemaDeploymentPhase.VerifyTarget => definition.Plan.AfterFingerprint,
            SchemaDeploymentPhase.ApproveReview => definition.Plan.Fingerprint,
            _ => null,
        } : null;
        if (required != observedFingerprint)
        { throw new InvalidOperationException("Deployment attestation fingerprint does not match this step."); }
        using var deadline = Deadline(cancellationToken);
        var token = deadline.Token;
        await using var connection = await _source.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await LockLeaseAsync(connection, transaction, lease, definition, token).ConfigureAwait(false);
        var previous = await LatestAsync(connection, transaction, lease.DeploymentId, stepId, token).ConfigureAwait(false);
        if (previous is null || previous.Attempt != attempt || previous.ActionFingerprint != action.Fingerprint)
        { throw new InvalidOperationException("The pending step attempt or action binding changed."); }
        if (previous.State == outcome && previous.EvidenceSha256 == evidenceSha256 &&
            previous.ObservedFingerprint == observedFingerprint)
        { await transaction.CommitAsync(token).ConfigureAwait(false); return; }
        if (previous.State != SchemaDeploymentAttemptState.Pending)
        { throw new InvalidOperationException("The external step was already resolved differently."); }
        await using var command = Command(connection, transaction, $"""
            UPDATE {_attempts} SET state = @state, observed_fingerprint = @observed,
                evidence_sha256 = @evidence, finished_at = pg_catalog.clock_timestamp()
            WHERE deployment_id = @id AND step_id = @step AND attempt = @attempt AND state = 0
                AND action_fingerprint = @action
            """);
        Add(command, "state", (int)outcome); Add(command, "observed", observedFingerprint);
        Add(command, "evidence", evidenceSha256); Add(command, "id", lease.DeploymentId);
        Add(command, "step", stepId); Add(command, "attempt", attempt); Add(command, "action", action.Fingerprint);
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
        { throw new InvalidOperationException("The pending step could not be resolved."); }
        await AssertLeaseAsync(connection, transaction, lease, definition, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    private async ValueTask VerifyStorageShapeAsync(DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        var expected = new Dictionary<string, (int TypeOid, int Modifier, bool NotNull)>(StringComparer.Ordinal)
        {
            ["settings.singleton"] = (16, -1, true),
            ["settings.format_version"] = (23, -1, true),
            ["settings.layout_fingerprint"] = (1042, 68, true),
            ["deployments.deployment_id"] = (1043, 132, true),
            ["deployments.format_version"] = (23, -1, true),
            ["deployments.plan_fingerprint"] = (1042, 68, true),
            ["deployments.definition_fingerprint"] = (1042, 68, true),
            ["deployments.before_fingerprint"] = (1042, 68, true),
            ["deployments.after_fingerprint"] = (1042, 68, true),
            ["deployments.lease_owner"] = (1043, 132, false),
            ["deployments.lease_expires"] = (1184, -1, false),
            ["deployments.fencing_token"] = (20, -1, true),
            ["deployments.created_at"] = (1184, -1, true),
            ["step_attempts.deployment_id"] = (1043, 132, true),
            ["step_attempts.step_id"] = (1043, 260, true),
            ["step_attempts.attempt"] = (23, -1, true),
            ["step_attempts.action_fingerprint"] = (1042, 68, true),
            ["step_attempts.kind"] = (21, -1, true),
            ["step_attempts.state"] = (21, -1, true),
            ["step_attempts.fencing_token"] = (20, -1, true),
            ["step_attempts.observed_fingerprint"] = (1042, 68, false),
            ["step_attempts.evidence_sha256"] = (1042, 68, false),
            ["step_attempts.started_at"] = (1184, -1, true),
            ["step_attempts.finished_at"] = (1184, -1, false),
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = Command(connection, transaction, """
            SELECT c.relname, a.attname, a.atttypid::integer, a.atttypmod, a.attnotnull
            FROM pg_catalog.pg_class AS c
            JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
            JOIN pg_catalog.pg_attribute AS a ON a.attrelid = c.oid
            WHERE n.nspname = @schema AND c.relname IN ('settings', 'deployments', 'step_attempts')
                AND c.relkind = 'r' AND a.attnum > 0 AND NOT a.attisdropped
            """))
        {
            Add(command, "schema", _schemaName);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var key = reader.GetString(0) + "." + reader.GetString(1);
                if (!seen.Add(key) || !expected.TryGetValue(key, out var shape) ||
                    reader.GetInt32(2) != shape.TypeOid || reader.GetInt32(3) != shape.Modifier ||
                    reader.GetBoolean(4) != shape.NotNull)
                { throw new InvalidOperationException("Schema deployment journal storage shape is incompatible."); }
            }
        }
        if (seen.Count != expected.Count)
        { throw new InvalidOperationException("Schema deployment journal storage columns are missing."); }
        var primaryKeys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["settings"] = "PRIMARY KEY (singleton)",
            ["deployments"] = "PRIMARY KEY (deployment_id)",
            ["step_attempts"] = "PRIMARY KEY (deployment_id, step_id, attempt)",
        };
        await using (var command = Command(connection, transaction, """
            SELECT c.relname, pg_catalog.pg_get_constraintdef(k.oid)
            FROM pg_catalog.pg_constraint AS k
            JOIN pg_catalog.pg_class AS c ON c.oid = k.conrelid
            JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname IN ('settings', 'deployments', 'step_attempts')
                AND k.contype = 'p'
            """))
        {
            Add(command, "schema", _schemaName);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var table = reader.GetString(0);
                if (!primaryKeys.Remove(table, out var expectedDefinition) ||
                    reader.GetString(1) != expectedDefinition)
                { throw new InvalidOperationException("Schema deployment journal primary keys are incompatible."); }
            }
        }
        if (primaryKeys.Count != 0)
        { throw new InvalidOperationException("Schema deployment journal primary keys are missing."); }
        await using (var command = Command(connection, transaction, """
            SELECT count(*)::integer
            FROM pg_catalog.pg_constraint AS k
            JOIN pg_catalog.pg_class AS child ON child.oid = k.conrelid
            JOIN pg_catalog.pg_class AS parent ON parent.oid = k.confrelid
            JOIN pg_catalog.pg_namespace AS n ON n.oid = child.relnamespace AND n.oid = parent.relnamespace
            WHERE n.nspname = @schema AND child.relname = 'step_attempts'
                AND parent.relname = 'deployments' AND k.contype = 'f'
                AND k.conkey = ARRAY[1]::smallint[] AND k.confkey = ARRAY[1]::smallint[]
            """))
        {
            Add(command, "schema", _schemaName);
            if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not int count || count != 1)
            { throw new InvalidOperationException("Schema deployment journal foreign key is incompatible."); }
        }
    }

    private async ValueTask CheckExistingStorageSetAsync(DbConnection connection, DbTransaction transaction,
        CancellationToken token)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var command = Command(connection, transaction, """
            SELECT c.relname FROM pg_catalog.pg_class AS c
            JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname IN ('settings', 'deployments', 'step_attempts')
            """);
        Add(command, "schema", _schemaName);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false)) { names.Add(reader.GetString(0)); }
        if (names.Count is not (0 or 3))
        { throw new InvalidOperationException("Schema deployment journal storage is incomplete or unrecognized."); }
    }

    private async ValueTask CheckDefinitionAsync(DbConnection connection, DbTransaction? transaction,
        string deploymentId, SchemaDeploymentDefinition definition, CancellationToken token)
    {
        await using var command = Command(connection, transaction, $"""
            SELECT format_version, plan_fingerprint, definition_fingerprint, before_fingerprint, after_fingerprint
            FROM {_deployments} WHERE deployment_id = @id
            """);
        Add(command, "id", deploymentId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetInt32(0) != 1 ||
            reader.GetString(1) != definition.Plan.Fingerprint || reader.GetString(2) != definition.Fingerprint ||
            reader.GetString(3) != definition.Plan.BeforeFingerprint || reader.GetString(4) != definition.Plan.AfterFingerprint)
        { throw new InvalidOperationException("Deployment identity, format or immutable definition does not match."); }
    }

    private async ValueTask LockLeaseAsync(DbConnection connection, DbTransaction transaction,
        SchemaDeploymentLease lease, SchemaDeploymentDefinition definition, CancellationToken token)
    {
        await using var command = Command(connection, transaction, $"""
            SELECT 1 FROM {_deployments}
            WHERE deployment_id = @id AND definition_fingerprint = @definition AND lease_owner = @owner
                AND fencing_token = @fence AND lease_expires > pg_catalog.clock_timestamp()
            FOR UPDATE
            """);
        BindLease(command, lease, definition);
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is null)
        { throw new InvalidOperationException("The database-clock deployment lease is absent, expired or fenced."); }
    }

    private async ValueTask AssertLeaseAsync(DbConnection connection, DbTransaction transaction,
        SchemaDeploymentLease lease, SchemaDeploymentDefinition definition, CancellationToken token)
    {
        await using var command = Command(connection, transaction, $"""
            SELECT 1 FROM {_deployments}
            WHERE deployment_id = @id AND definition_fingerprint = @definition AND lease_owner = @owner
                AND fencing_token = @fence AND lease_expires > pg_catalog.clock_timestamp()
            """);
        BindLease(command, lease, definition);
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is null)
        { throw new InvalidOperationException("The SQL step exceeded its database-clock lease; DDL is rolled back."); }
    }

    private async ValueTask CheckPrerequisitesAsync(DbConnection connection, DbTransaction transaction,
        string deploymentId, SchemaDeploymentStep step, CancellationToken token)
    {
        if (step.DependsOn.Count == 0) { return; }
        await using var command = Command(connection, transaction, $"""
            SELECT NOT EXISTS (
                SELECT 1 FROM pg_catalog.unnest(@dependencies) AS required(step_id)
                WHERE NOT EXISTS (
                    SELECT 1 FROM {_attempts} AS completed
                    WHERE completed.deployment_id = @id AND completed.step_id = required.step_id
                        AND completed.state = 1))
            """);
        AddTextArray(command, "dependencies", step.DependsOn.ToArray()); Add(command, "id", deploymentId);
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not true)
        { throw new InvalidOperationException("Deployment prerequisites are incomplete."); }
    }

    private async ValueTask<SchemaDeploymentStepAttempt?> LatestAsync(DbConnection connection, DbTransaction? transaction,
        string deploymentId, string stepId, CancellationToken token)
    {
        await using var command = Command(connection, transaction, $"""
            SELECT attempt, state, action_fingerprint, observed_fingerprint, evidence_sha256
            FROM {_attempts} WHERE deployment_id = @id AND step_id = @step ORDER BY attempt DESC LIMIT 1
            """);
        Add(command, "id", deploymentId); Add(command, "step", stepId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) { return null; }
        return new(stepId, reader.GetInt32(0), (SchemaDeploymentAttemptState)reader.GetInt16(1), false,
            reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private async ValueTask InsertAttemptAsync(DbConnection connection, DbTransaction transaction,
        SchemaDeploymentLease lease, SchemaDeploymentAction action, int attempt, SchemaDeploymentAttemptState state,
        string? observedFingerprint, string? evidenceSha256, CancellationToken token)
    {
        await using var command = Command(connection, transaction, $"""
            INSERT INTO {_attempts} (deployment_id, step_id, attempt, action_fingerprint, kind, state,
                fencing_token, observed_fingerprint, evidence_sha256, finished_at)
            VALUES (@id, @step, @attempt, @action, @kind, @state, @fence, @observed, @evidence,
                CASE WHEN @state = 0 THEN NULL ELSE pg_catalog.clock_timestamp() END)
            """);
        Add(command, "id", lease.DeploymentId); Add(command, "step", action.StepId);
        Add(command, "attempt", attempt); Add(command, "action", action.Fingerprint);
        Add(command, "kind", (int)action.Kind); Add(command, "state", (int)state);
        Add(command, "fence", lease.FencingToken); Add(command, "observed", observedFingerprint);
        Add(command, "evidence", evidenceSha256);
        _ = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private async ValueTask ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql, CancellationToken token)
    {
        await using var command = Command(connection, transaction, sql);
        _ = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private BlueTuskCommand Command(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = (BlueTuskCommand)connection.CreateCommand();
        command.Transaction = (BlueTuskTransaction?)transaction;
        command.CommandText = sql; command.CommandTimeout = _timeout;
        return command;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name;
        if (value is null) { ((BlueTuskParameter)parameter).PostgreSqlTypeOid = 25; }
        parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter);
    }

    private static void AddTextArray(DbCommand command, string name, string[] value)
    {
        var parameter = new BlueTuskParameter(value) { ParameterName = name, PostgreSqlTypeOid = 1009 };
        command.Parameters.Add(parameter);
    }

    private static void BindLease(DbCommand command, SchemaDeploymentLease lease, SchemaDeploymentDefinition definition)
    {
        Add(command, "id", lease.DeploymentId); Add(command, "owner", lease.Owner);
        Add(command, "fence", lease.FencingToken); Add(command, "definition", definition.Fingerprint);
    }

    private static SchemaDeploymentAction ValidateStep(SchemaDeploymentLease lease,
        SchemaDeploymentDefinition definition, string stepId, SchemaDeploymentActionKind kind)
    {
        ValidateLease(lease, definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        if (!definition.ActionIndex.TryGetValue(stepId, out var action) || action.Kind != kind)
        { throw new ArgumentException("Unknown step or incorrect action kind.", nameof(stepId)); }
        return action;
    }

    private static void ValidateLease(SchemaDeploymentLease lease, SchemaDeploymentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(lease); Validate(lease.DeploymentId, definition);
        Name(lease.Owner, nameof(lease)); ArgumentOutOfRangeException.ThrowIfLessThan(lease.FencingToken, 1);
    }

    private static void Validate(string deploymentId, SchemaDeploymentDefinition definition)
    { Name(deploymentId, nameof(deploymentId)); ArgumentNullException.ThrowIfNull(definition); }

    private static void Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromMinutes(5) ||
            duration.Ticks % TimeSpan.TicksPerMillisecond != 0)
        { throw new ArgumentOutOfRangeException(nameof(duration), "Leases must be whole milliseconds from 1 second to 5 minutes."); }
    }

    private static void Name(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (Encoding.UTF8.GetByteCount(value) > 128 || value[0] is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z') ||
            value.Any(character => character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')))
        { throw new ArgumentException("Deployment names must be bounded ASCII identifiers.", parameter); }
    }

    private static void Hash(string value, string parameter)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        { throw new ArgumentException("Expected a lowercase SHA-256 hex digest.", parameter); }
    }

    private CancellationTokenSource Deadline(CancellationToken token)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(token);
        source.CancelAfter(TimeSpan.FromSeconds(_timeout));
        return source;
    }
}
