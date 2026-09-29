using System.Data.Common;
using System.Globalization;

namespace BlueTusk.Studio;

/// <summary>Append-only durable Studio audits. The data source is borrowed and contains no SQL or result payloads.</summary>
public sealed class PostgreSqlStudioAuditSink : IStudioAuditSink
{
    private readonly DbDataSource _dataSource;
    private readonly string _schema;
    private readonly int _timeout;

    public PostgreSqlStudioAuditSink(DbDataSource dataSource, string schema = "bluetusk_studio", int commandTimeoutSeconds = 10)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        if (schema.Length > 63 || (!char.IsAsciiLetter(schema[0]) && schema[0] != '_') || schema.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
        {
            throw new ArgumentException("Audit schema requires one unquoted PostgreSQL identifier.", nameof(schema));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(commandTimeoutSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(commandTimeoutSeconds, 30);
        _dataSource = dataSource;
        _schema = '"' + schema + '"';
        _timeout = commandTimeoutSeconds;
    }

    /// <summary>Provision once during deployment. Unknown durable versions fail explicitly.</summary>
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandTimeout = _timeout;
            command.CommandText = $"""
                SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended('{_schema}', 0));
                CREATE SCHEMA IF NOT EXISTS {_schema};
                CREATE TABLE IF NOT EXISTS {_schema}.studio_audit_version (
                    singleton boolean PRIMARY KEY CHECK(singleton), version integer NOT NULL CHECK(version > 0));
                INSERT INTO {_schema}.studio_audit_version VALUES(true, 2) ON CONFLICT DO NOTHING;
                CREATE TABLE IF NOT EXISTS {_schema}.studio_audit (
                    operation_id uuid NOT NULL CHECK(operation_id <> '00000000-0000-0000-0000-000000000000'::uuid),
                    outcome text NOT NULL CHECK(length(outcome) BETWEEN 1 AND 64),
                    actor_id text NOT NULL CHECK(length(actor_id) BETWEEN 1 AND 512),
                    scope_id text NOT NULL CONSTRAINT studio_audit_scope_length CHECK(length(scope_id) BETWEEN 1 AND 128),
                    query_fingerprint text NOT NULL CHECK(length(query_fingerprint) = 64),
                    returned_rows integer NOT NULL CHECK(returned_rows >= 0),
                    occurred_at timestamptz NOT NULL DEFAULT pg_catalog.clock_timestamp(),
                    PRIMARY KEY(operation_id, outcome));
                CREATE INDEX IF NOT EXISTS studio_audit_time ON {_schema}.studio_audit(occurred_at, operation_id, outcome)
                """;
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            command.CommandText = $"SELECT version FROM {_schema}.studio_audit_version WHERE singleton";
            var version = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (version == 1)
            {
                // Existing audits cannot be attributed retroactively. The explicit marker
                // keeps that limitation visible while new writes require a real scope.
                command.CommandText = $"ALTER TABLE {_schema}.studio_audit ADD COLUMN scope_id text NOT NULL DEFAULT 'legacy-unknown'";
                _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                command.CommandText = $"ALTER TABLE {_schema}.studio_audit ADD CONSTRAINT studio_audit_scope_length CHECK(length(scope_id) BETWEEN 1 AND 128) NOT VALID";
                _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                command.CommandText = $"ALTER TABLE {_schema}.studio_audit ALTER COLUMN scope_id DROP DEFAULT";
                _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                command.CommandText = $"UPDATE {_schema}.studio_audit_version SET version=2 WHERE singleton AND version=1";
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException("The durable Studio audit version changed during its upgrade.");
                }
            }
            else if (version != 2)
            {
                throw new InvalidOperationException("Unsupported durable Studio audit version.");
            }
            command.CommandText = """
                SELECT EXISTS(
                    SELECT 1 FROM pg_catalog.pg_attribute a
                    WHERE a.attrelid=pg_catalog.to_regclass(@table)
                      AND a.attname='scope_id' AND a.atttypid=pg_catalog.to_regtype('pg_catalog.text')
                      AND a.attnum>0 AND NOT a.attisdropped AND a.attnotnull
                      AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_attrdef d
                          WHERE d.adrelid=a.attrelid AND d.adnum=a.attnum))
                """;
            Add(command, "table", _schema + ".studio_audit");
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                throw new InvalidOperationException("The durable Studio audit scope column differs from storage version 2.");
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A retry with the same operation/outcome must contain identical immutable audit fields.</summary>
    public async ValueTask RecordAsync(StudioAuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(record.ActorId) || record.ActorId.Length > 512 ||
            record.ActorId.Contains('\0', StringComparison.Ordinal) || record.QueryFingerprint is not { Length: 64 } ||
            record.QueryFingerprint.Any(character => character is not (>= 'a' and <= 'f') and not (>= '0' and <= '9')) ||
            record.ScopeId is not { Length: > 0 and <= 128 } || record.ScopeId == "legacy-unknown" ||
            record.ScopeId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not (':' or '.' or '-' or '_')) ||
            string.IsNullOrWhiteSpace(record.Outcome) || record.Outcome.Length > 64 ||
            record.Outcome.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')) || record.ReturnedRows < 0)
        {
            throw new ArgumentException("Invalid immutable Studio audit record.", nameof(record));
        }
        await using var command = _dataSource.CreateCommand($"""
            INSERT INTO {_schema}.studio_audit(operation_id, outcome, actor_id, scope_id, query_fingerprint, returned_rows)
            SELECT @operation, @outcome, @actor, @scope, @fingerprint, @rows
            FROM {_schema}.studio_audit_version WHERE singleton AND version = 2
            ON CONFLICT(operation_id, outcome) DO NOTHING
            RETURNING operation_id
            """);
        command.CommandTimeout = _timeout;
        Add(command, "operation", record.OperationId);
        Add(command, "outcome", record.Outcome);
        Add(command, "actor", record.ActorId);
        Add(command, "scope", record.ScopeId);
        Add(command, "fingerprint", record.QueryFingerprint);
        Add(command, "rows", record.ReturnedRows);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid) { return; }

        // A separate statement sees a concurrently committed winner of ON CONFLICT,
        // whereas a same-statement read can still use the earlier snapshot.
        await using var existing = _dataSource.CreateCommand($"""
            SELECT a.actor_id, a.scope_id, a.query_fingerprint, a.returned_rows
            FROM {_schema}.studio_audit_version v
            JOIN {_schema}.studio_audit a ON a.operation_id = @operation AND a.outcome = @outcome
            WHERE v.singleton AND v.version = 2
            """);
        existing.CommandTimeout = _timeout;
        Add(existing, "operation", record.OperationId);
        Add(existing, "outcome", record.Outcome);
        await using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetString(0) != record.ActorId || reader.GetString(1) != record.ScopeId ||
            reader.GetString(2) != record.QueryFingerprint || reader.GetInt32(3) != record.ReturnedRows)
        {
            throw new InvalidOperationException("A Studio audit operation/outcome identity was reused with different immutable fields.");
        }
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
