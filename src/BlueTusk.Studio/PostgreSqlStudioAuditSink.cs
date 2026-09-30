using System.Data;
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
            else if (version is not 2 and not 3)
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
            command.Parameters.Clear();
            if (version == 2 || version == 1)
            {
                // An exclusive table lock drains old v2 INSERT statements before the
                // version changes. Later v2 writers see version 3 and fail closed.
                command.CommandText = $"LOCK TABLE {_schema}.studio_audit IN ACCESS EXCLUSIVE MODE";
                _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                command.CommandText = $"ALTER TABLE {_schema}.studio_audit ADD COLUMN operation_time_ms bigint";
                _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                command.CommandText = $"ALTER TABLE {_schema}.studio_audit_version ADD COLUMN sealed_through_ms bigint NOT NULL DEFAULT -1, ADD COLUMN archived_through_ms bigint NOT NULL DEFAULT -1, ADD COLUMN legacy_archived boolean NOT NULL DEFAULT false";
                _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                command.CommandText = $"CREATE INDEX studio_audit_operation_time ON {_schema}.studio_audit(operation_time_ms, operation_id, outcome) WHERE operation_time_ms IS NOT NULL";
                _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                command.CommandText = $"UPDATE {_schema}.studio_audit_version SET version=3 WHERE singleton AND version=2";
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException("The durable Studio audit version changed during its upgrade.");
                }
            }
            command.CommandText = $"SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_attribute WHERE attrelid=pg_catalog.to_regclass(@table) AND attname='operation_time_ms' AND atttypid=pg_catalog.to_regtype('pg_catalog.int8') AND attnum>0 AND NOT attisdropped)";
            command.Parameters.Clear();
            Add(command, "table", _schema + ".studio_audit");
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                throw new InvalidOperationException("The durable Studio audit operation time differs from storage version 3.");
            }
            command.CommandText = $"""
                CREATE TABLE IF NOT EXISTS {_schema}.studio_audit_archives (
                    archive_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    kind text NOT NULL CHECK(kind IN ('horizon', 'legacy')),
                    through_ms bigint,
                    archive_reference text NOT NULL CHECK(length(archive_reference) BETWEEN 1 AND 512),
                    confirmed_at timestamptz NOT NULL DEFAULT pg_catalog.clock_timestamp())
                """;
            command.Parameters.Clear();
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            // The database fence also covers a caller issuing INSERT directly with the
            // runtime role. Its row lock serializes every insert with a horizon seal.
            command.CommandText = $"""
                CREATE OR REPLACE FUNCTION {_schema}.studio_audit_insert_fence()
                RETURNS trigger LANGUAGE plpgsql AS $studio$
                DECLARE storage_version integer; sealed bigint; encoded_time bigint;
                BEGIN
                    SELECT version, sealed_through_ms INTO storage_version, sealed
                    FROM {_schema}.studio_audit_version WHERE singleton FOR SHARE;
                    IF storage_version IS DISTINCT FROM 3 THEN
                        RAISE EXCEPTION 'Unsupported durable Studio audit version';
                    END IF;
                    IF substring(NEW.operation_id::text FROM 15 FOR 1) <> '7'
                        OR substring(NEW.operation_id::text FROM 20 FOR 1) NOT IN ('8','9','a','b') THEN
                        RAISE EXCEPTION 'Studio audit operation ID must be UUIDv7';
                    END IF;
                    encoded_time := ('x' || left(replace(NEW.operation_id::text, '-', ''), 12))::bit(48)::bigint;
                    IF NEW.operation_time_ms IS DISTINCT FROM encoded_time
                        OR encoded_time <= sealed
                        OR encoded_time > (extract(epoch FROM pg_catalog.clock_timestamp()) * 1000)::bigint + 300000 THEN
                        RAISE EXCEPTION 'Studio audit operation ID is outside its writable horizon';
                    END IF;
                    RETURN NEW;
                END;
                $studio$;
                CREATE OR REPLACE TRIGGER studio_audit_insert_fence
                    BEFORE INSERT ON {_schema}.studio_audit FOR EACH ROW
                    EXECUTE FUNCTION {_schema}.studio_audit_insert_fence()
                """;
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
        if (!TryOperationTime(record.OperationId, out var operationTime))
        {
            throw new ArgumentException("Durable Studio audit operation IDs must be UUIDv7.", nameof(record));
        }
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var fence = connection.CreateCommand())
        {
            fence.Transaction = transaction;
            fence.CommandTimeout = _timeout;
            fence.CommandText = $"SELECT version, sealed_through_ms, (extract(epoch FROM pg_catalog.clock_timestamp()) * 1000)::bigint FROM {_schema}.studio_audit_version WHERE singleton FOR SHARE";
            await using var reader = await fence.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetInt32(0) != 3)
            {
                throw new InvalidOperationException("Unsupported durable Studio audit version.");
            }
            if (operationTime <= reader.GetInt64(1))
            {
                throw new StudioAuditHorizonException();
            }
            if (operationTime > reader.GetInt64(2) + 300_000)
            {
                throw new ArgumentException("The Studio audit operation ID is more than five minutes in the future.", nameof(record));
            }
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandTimeout = _timeout;
            command.CommandText = $"""
                INSERT INTO {_schema}.studio_audit(operation_id, outcome, actor_id, scope_id, query_fingerprint, returned_rows, operation_time_ms)
                VALUES(@operation, @outcome, @actor, @scope, @fingerprint, @rows, @operationTime)
                ON CONFLICT(operation_id, outcome) DO NOTHING
                RETURNING operation_id
                """;
            Add(command, "operation", record.OperationId);
            Add(command, "outcome", record.Outcome);
            Add(command, "actor", record.ActorId);
            Add(command, "scope", record.ScopeId);
            Add(command, "fingerprint", record.QueryFingerprint);
            Add(command, "rows", record.ReturnedRows);
            Add(command, "operationTime", operationTime);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        // A new statement sees a concurrent ON CONFLICT winner after it commits.
        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandTimeout = _timeout;
            existing.CommandText = $"SELECT actor_id, scope_id, query_fingerprint, returned_rows, operation_time_ms FROM {_schema}.studio_audit WHERE operation_id=@operation AND outcome=@outcome";
            Add(existing, "operation", record.OperationId);
            Add(existing, "outcome", record.Outcome);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.GetString(0) != record.ActorId || reader.GetString(1) != record.ScopeId ||
                reader.GetString(2) != record.QueryFingerprint || reader.GetInt32(3) != record.ReturnedRows ||
                reader.IsDBNull(4) || reader.GetInt64(4) != operationTime)
            {
                throw new InvalidOperationException("A Studio audit operation/outcome identity was reused with different immutable fields.");
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool TryOperationTime(Guid operationId, out long milliseconds)
    {
        var bytes = operationId.ToByteArray(bigEndian: true);
        if ((bytes[6] >> 4) != 7 || (bytes[8] >> 6) != 2)
        {
            milliseconds = 0;
            return false;
        }
        milliseconds = 0;
        for (var index = 0; index < 6; index++) { milliseconds = (milliseconds << 8) | bytes[index]; }
        return true;
    }

    /// <summary>Seal UUIDv7 identities through an operator-chosen UTC instant before exporting the stable audit set.</summary>
    public async ValueTask SealRetentionHorizonAsync(DateTimeOffset through, CancellationToken cancellationToken = default)
    {
        var milliseconds = through.ToUnixTimeMilliseconds();
        if (milliseconds < 0) { throw new ArgumentOutOfRangeException(nameof(through)); }
        await using var command = _dataSource.CreateCommand($"""
            UPDATE {_schema}.studio_audit_version
            SET sealed_through_ms=@through
            WHERE singleton AND version=3 AND @through > sealed_through_ms
              AND @through <= (extract(epoch FROM pg_catalog.clock_timestamp()) * 1000)::bigint
            """);
        command.CommandTimeout = _timeout;
        Add(command, "through", milliseconds);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The Studio audit horizon must advance within the current database time and storage version.");
        }
    }

    /// <summary>Attest that all sealed UUIDv7 audit rows through this horizon are in an independently verified archive.</summary>
    public ValueTask ConfirmArchivedHorizonAsync(DateTimeOffset through, string archiveReference, CancellationToken cancellationToken = default) =>
        ConfirmArchiveAsync("horizon", through.ToUnixTimeMilliseconds(), archiveReference, cancellationToken);

    /// <summary>Attest that all pre-v3 audit rows have been exported after the v3 cutover.</summary>
    public ValueTask ConfirmLegacyArchiveAsync(string archiveReference, CancellationToken cancellationToken = default) =>
        ConfirmArchiveAsync("legacy", null, archiveReference, cancellationToken);

    private async ValueTask ConfirmArchiveAsync(string kind, long? through, string archiveReference, CancellationToken cancellationToken)
    {
        if (through < 0 || string.IsNullOrWhiteSpace(archiveReference) || archiveReference.Length > 512 || archiveReference.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A valid archive horizon and external archive reference are required.", nameof(archiveReference));
        }
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandTimeout = _timeout;
            update.CommandText = kind == "legacy"
                ? $"UPDATE {_schema}.studio_audit_version SET legacy_archived=true WHERE singleton AND version=3 AND NOT legacy_archived"
                : $"UPDATE {_schema}.studio_audit_version SET archived_through_ms=@through WHERE singleton AND version=3 AND @through > archived_through_ms AND @through <= sealed_through_ms";
            if (through.HasValue) { Add(update, "through", through.Value); }
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("The Studio audit archive confirmation must advance within its sealed horizon.");
            }
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandTimeout = _timeout;
            insert.CommandText = $"INSERT INTO {_schema}.studio_audit_archives(kind, through_ms, archive_reference) VALUES(@kind, @through, @reference)";
            Add(insert, "kind", kind);
            Add(insert, "through", through is { } value ? value : DBNull.Value, DbType.Int64);
            Add(insert, "reference", archiveReference);
            _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Delete at most maximumRows confirmed archived records; repeat until zero is returned.</summary>
    public async ValueTask<int> PruneArchivedAsync(int maximumRows = 1000, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumRows, 10_000);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var worker = connection.CreateCommand())
        {
            worker.Transaction = transaction;
            worker.CommandTimeout = _timeout;
            worker.CommandText = "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(@name, 1))";
            Add(worker, "name", "BlueTusk.Studio:" + _schema + ":prune");
            _ = await worker.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        long horizon;
        bool legacy;
        await using (var fence = connection.CreateCommand())
        {
            fence.Transaction = transaction;
            fence.CommandTimeout = _timeout;
            fence.CommandText = $"SELECT version, archived_through_ms, legacy_archived FROM {_schema}.studio_audit_version WHERE singleton FOR SHARE";
            await using var reader = await fence.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetInt32(0) != 3)
            {
                throw new InvalidOperationException("Unsupported durable Studio audit version.");
            }
            horizon = reader.GetInt64(1);
            legacy = reader.GetBoolean(2);
        }
        var deleted = 0;
        if (legacy)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = _timeout;
            command.CommandText = $"""
                WITH selected AS (
                    SELECT ctid FROM {_schema}.studio_audit
                    WHERE operation_time_ms IS NULL ORDER BY occurred_at, operation_id, outcome
                    LIMIT @limit FOR UPDATE)
                DELETE FROM {_schema}.studio_audit a USING selected s WHERE a.ctid=s.ctid
                """;
            Add(command, "limit", maximumRows);
            deleted += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (deleted < maximumRows && horizon >= 0)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = _timeout;
            command.CommandText = $"""
                WITH selected AS (
                    SELECT ctid FROM {_schema}.studio_audit
                    WHERE operation_time_ms <= @horizon ORDER BY operation_time_ms, operation_id, outcome
                    LIMIT @limit FOR UPDATE)
                DELETE FROM {_schema}.studio_audit a USING selected s WHERE a.ctid=s.ctid
                """;
            Add(command, "horizon", horizon);
            Add(command, "limit", maximumRows - deleted);
            deleted += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    private static void Add(DbCommand command, string name, object value, DbType? type = null)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        if (type is { } concreteType) { parameter.DbType = concreteType; }
        command.Parameters.Add(parameter);
    }
}
