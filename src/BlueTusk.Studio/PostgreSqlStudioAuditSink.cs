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
                INSERT INTO {_schema}.studio_audit_version VALUES(true, 1) ON CONFLICT DO NOTHING;
                CREATE TABLE IF NOT EXISTS {_schema}.studio_audit (
                    operation_id uuid NOT NULL CHECK(operation_id <> '00000000-0000-0000-0000-000000000000'::uuid),
                    outcome text NOT NULL CHECK(length(outcome) BETWEEN 1 AND 64),
                    actor_id text NOT NULL CHECK(length(actor_id) BETWEEN 1 AND 512),
                    query_fingerprint text NOT NULL CHECK(length(query_fingerprint) = 64),
                    returned_rows integer NOT NULL CHECK(returned_rows >= 0),
                    occurred_at timestamptz NOT NULL DEFAULT pg_catalog.clock_timestamp(),
                    PRIMARY KEY(operation_id, outcome));
                CREATE INDEX IF NOT EXISTS studio_audit_time ON {_schema}.studio_audit(occurred_at, operation_id, outcome)
                """;
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            command.CommandText = $"SELECT version FROM {_schema}.studio_audit_version WHERE singleton";
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidOperationException("Unsupported durable Studio audit version.");
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
            string.IsNullOrWhiteSpace(record.Outcome) || record.Outcome.Length > 64 ||
            record.Outcome.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')) || record.ReturnedRows < 0)
        {
            throw new ArgumentException("Invalid immutable Studio audit record.", nameof(record));
        }
        await using var command = _dataSource.CreateCommand($"""
            INSERT INTO {_schema}.studio_audit AS current_audit(operation_id, outcome, actor_id, query_fingerprint, returned_rows)
            SELECT @operation, @outcome, @actor, @fingerprint, @rows
            FROM {_schema}.studio_audit_version WHERE singleton AND version = 1
            ON CONFLICT(operation_id, outcome) DO UPDATE SET operation_id = current_audit.operation_id
            WHERE current_audit.actor_id = excluded.actor_id AND current_audit.query_fingerprint = excluded.query_fingerprint
                AND current_audit.returned_rows = excluded.returned_rows
            RETURNING operation_id
            """);
        command.CommandTimeout = _timeout;
        Add(command, "operation", record.OperationId);
        Add(command, "outcome", record.Outcome);
        Add(command, "actor", record.ActorId);
        Add(command, "fingerprint", record.QueryFingerprint);
        Add(command, "rows", record.ReturnedRows);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not Guid)
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
