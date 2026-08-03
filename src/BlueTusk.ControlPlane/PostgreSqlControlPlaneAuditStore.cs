using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;

namespace BlueTusk.ControlPlane;

public sealed class PostgreSqlControlPlaneAuditStore : IControlPlaneAuditStore
{
    public const int CurrentSchemaVersion = 2;

    private const int CurrentRecordFormat = 1;

    private static readonly Regex SchemaPattern = new(
        "^[A-Za-z_][A-Za-z0-9_$]*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly DbDataSource _dataSource;
    private readonly string _controlSchema;
    private readonly string _schema;

    public PostgreSqlControlPlaneAuditStore(
        DbDataSource dataSource,
        string controlSchema = "bluetusk_control")
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlSchema);
        if (!SchemaPattern.IsMatch(controlSchema))
        {
            throw new ArgumentException(
                "The control schema must be one unquoted PostgreSQL identifier.",
                nameof(controlSchema));
        }

        _dataSource = dataSource;
        _controlSchema = controlSchema;
        _schema = $"\"{controlSchema}\"";
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        await using (var migrationLock = connection.CreateCommand())
        {
            migrationLock.Transaction = transaction;
            migrationLock.CommandText =
                "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(@schema, 0))";
            AddParameter(migrationLock, "schema", _controlSchema);
            _ = await migrationLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        await ExecuteNonQueryAsync(
            connection,
            transaction,
            $"CREATE SCHEMA IF NOT EXISTS {_schema}",
            cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            $"""
             CREATE TABLE IF NOT EXISTS {_schema}.storage_metadata (
                 singleton boolean PRIMARY KEY DEFAULT TRUE CHECK (singleton),
                 schema_version integer NOT NULL CHECK (schema_version >= 0)
             )
             """,
            cancellationToken).ConfigureAwait(false);

        var schemaVersion = await ReadOrCreateSchemaVersionAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (schemaVersion > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Control-plane audit schema version {schemaVersion} is newer than " +
                $"the supported version {CurrentSchemaVersion}.");
        }

        if (schemaVersion < 0)
        {
            throw new InvalidOperationException(
                $"Control-plane audit schema version {schemaVersion} is invalid.");
        }

        if (schemaVersion <= 1)
        {
            await ApplyVersionOneAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            await SetSchemaVersionAsync(
                connection,
                transaction,
                1,
                cancellationToken).ConfigureAwait(false);
            schemaVersion = 1;
        }

        if (schemaVersion == 1)
        {
            await ExecuteNonQueryAsync(
                connection,
                transaction,
                $"""
                 ALTER TABLE {_schema}.audit_log
                 ADD COLUMN IF NOT EXISTS record_format integer NOT NULL DEFAULT 1
                 """,
                cancellationToken).ConfigureAwait(false);
            await SetSchemaVersionAsync(
                connection,
                transaction,
                CurrentSchemaVersion,
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int> GetSchemaVersionAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT schema_version FROM {_schema}.storage_metadata WHERE singleton";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull
            ? throw new InvalidOperationException(
                "The control-plane audit schema is not initialized.")
            : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async ValueTask<int> ReadOrCreateSchemaVersionAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText =
            $"SELECT schema_version FROM {_schema}.storage_metadata WHERE singleton";
        var value = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is not null and not DBNull)
        {
            return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        await using var detectLegacy = connection.CreateCommand();
        detectLegacy.Transaction = transaction;
        detectLegacy.CommandText = "SELECT pg_catalog.to_regclass(@audit_table)";
        AddParameter(detectLegacy, "audit_table", $"{_controlSchema}.audit_log");
        var legacyTable = await detectLegacy.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        var inferredVersion = legacyTable is null or DBNull ? 0 : 1;

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            $"""
             INSERT INTO {_schema}.storage_metadata (singleton, schema_version)
             VALUES (TRUE, @schema_version)
             ON CONFLICT (singleton) DO NOTHING
             """;
        AddParameter(insert, "schema_version", inferredVersion);
        _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return inferredVersion;
    }

    private async ValueTask ApplyVersionOneAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        string[] statements =
        [
            $"""
             CREATE TABLE IF NOT EXISTS {_schema}.audit_log (
                 audit_sequence bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                 operation_id uuid NOT NULL,
                 occurred_at timestamptz NOT NULL,
                 actor_id text NOT NULL,
                 operation_kind text NOT NULL,
                 target text NOT NULL,
                 status text NOT NULL,
                 reason text NOT NULL,
                 detail_code text NULL
             )
             """,
            $"""
             CREATE OR REPLACE FUNCTION {_schema}.reject_audit_mutation()
             RETURNS trigger
             LANGUAGE plpgsql
             AS $body$
             BEGIN
                 RAISE EXCEPTION 'BlueTusk control-plane audit rows are immutable';
             END;
             $body$
             """,
            $"""
             DO $body$
             BEGIN
                 IF NOT EXISTS (
                     SELECT 1
                     FROM pg_catalog.pg_trigger
                     WHERE tgname = 'audit_log_immutable'
                       AND tgrelid = '{_schema}.audit_log'::regclass
                       AND NOT tgisinternal)
                 THEN
                     EXECUTE $command$
                         CREATE TRIGGER audit_log_immutable
                         BEFORE UPDATE OR DELETE ON {_schema}.audit_log
                         FOR EACH ROW EXECUTE FUNCTION {_schema}.reject_audit_mutation()
                     $command$;
                 END IF;
             END;
             $body$
             """,
            $"""
             CREATE INDEX IF NOT EXISTS audit_log_operation_idx
             ON {_schema}.audit_log (operation_id, audit_sequence)
             """,
        ];
        foreach (var statement in statements)
        {
            await ExecuteNonQueryAsync(
                connection,
                transaction,
                statement,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask AppendAsync(
        ControlPlaneAuditRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        Validate(record);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             INSERT INTO {_schema}.audit_log (
                 operation_id, occurred_at, actor_id, operation_kind, target,
                 status, reason, detail_code, record_format)
             SELECT
                 @operation_id, @occurred_at, @actor_id, @operation_kind,
                 @target, @status, @reason, @detail_code, @record_format
             FROM {_schema}.storage_metadata
             WHERE singleton AND schema_version = @schema_version
             """;
        AddParameter(command, "operation_id", record.OperationId);
        AddParameter(command, "occurred_at", record.OccurredAt);
        AddParameter(command, "actor_id", record.ActorId);
        AddParameter(command, "operation_kind", record.Kind.ToString());
        AddParameter(command, "target", record.Target);
        AddParameter(command, "status", record.Status.ToString());
        AddParameter(command, "reason", record.Reason);
        AddParameter(command, "detail_code", record.DetailCode);
        AddParameter(command, "record_format", CurrentRecordFormat);
        AddParameter(command, "schema_version", CurrentSchemaVersion);
        var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows != 1)
        {
            throw new InvalidOperationException(
                $"Control-plane audit writes require schema version {CurrentSchemaVersion}.");
        }
    }

    private async ValueTask SetSchemaVersionAsync(
        DbConnection connection,
        DbTransaction transaction,
        int schemaVersion,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             UPDATE {_schema}.storage_metadata
             SET schema_version = @schema_version
             WHERE singleton
             """;
        AddParameter(command, "schema_version", schemaVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                "The control-plane audit schema metadata row is missing.");
        }
    }

    private static async ValueTask ExecuteNonQueryAsync(
        DbConnection connection,
        DbTransaction transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(ControlPlaneAuditRecord record)
    {
        if (record.OperationId == Guid.Empty)
        {
            throw new ArgumentException("The operation ID cannot be empty.", nameof(record));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(record.ActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Target);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Reason);
        if (!Enum.IsDefined(record.Kind) || !Enum.IsDefined(record.Status))
        {
            throw new ArgumentOutOfRangeException(nameof(record));
        }

        ThrowIfTooLong(record.ActorId, 512, nameof(record));
        ThrowIfTooLong(record.Target, 1024, nameof(record));
        ThrowIfTooLong(record.Reason, 2048, nameof(record));
        if (record.DetailCode is not null)
        {
            ThrowIfTooLong(record.DetailCode, 512, nameof(record));
        }
    }

    private static void ThrowIfTooLong(string value, int maximum, string parameterName)
    {
        if (value.Length > maximum)
        {
            throw new ArgumentException(
                $"The value cannot exceed {maximum} characters.",
                parameterName);
        }
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        if (value is null)
        {
            parameter.DbType = DbType.String;
        }

        parameter.Value = value ?? DBNull.Value;
        _ = command.Parameters.Add(parameter);
    }
}
