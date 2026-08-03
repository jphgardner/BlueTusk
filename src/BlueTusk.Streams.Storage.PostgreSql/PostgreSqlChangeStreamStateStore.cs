using System.Data;
using System.Data.Common;
using BlueTusk.TypeSystem;

namespace BlueTusk.Streams.Storage.PostgreSql;

public sealed class PostgreSqlChangeStreamStateStore : IChangeStreamStateStore
{
    private readonly DbDataSource _dataSource;
    private readonly string _table;

    public PostgreSqlChangeStreamStateStore(PostgreSqlStreamsStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _dataSource = options.ControlDataSource;
        _table = options.QuotedControlSchema + ".stream_state";
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var schema = _table[.._table.LastIndexOf('.')];
        await ExecuteAsync(
            connection,
            transaction: null,
            $"CREATE SCHEMA IF NOT EXISTS {schema}",
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction: null,
            $"""
            CREATE TABLE IF NOT EXISTS {_table} (
                source_fingerprint text NOT NULL,
                consumer_group text NOT NULL,
                checkpoint_format integer NULL,
                system_identifier text NULL,
                database_name text NULL,
                slot_name text NULL,
                publication_fingerprint text NULL,
                database_identity text NULL,
                output_plugin text NULL,
                mapping_fingerprint text NULL,
                acknowledged_position numeric(20, 0) NULL,
                store_generation bigint NULL,
                lease_owner text NULL,
                lease_token bigint NULL,
                lease_expires timestamptz NULL,
                last_fencing_token bigint NOT NULL DEFAULT 0,
                PRIMARY KEY (source_fingerprint, consumer_group),
                CHECK (checkpoint_format IS NULL OR checkpoint_format > 0),
                CHECK (store_generation IS NULL OR store_generation >= 0),
                CHECK (lease_token IS NULL OR lease_token > 0),
                CHECK (last_fencing_token >= 0)
            )
            """,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ChangeStreamCheckpoint?> ReadAsync(
        ChangeStreamStateKey key,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = CreateCommand(
            connection,
            transaction: null,
            $"""
            SELECT checkpoint_format, system_identifier, database_name, slot_name,
                   publication_fingerprint, database_identity, output_plugin,
                   mapping_fingerprint, acknowledged_position, store_generation
            FROM {_table}
            WHERE source_fingerprint = @source AND consumer_group = @consumer
            """);
        AddKeyParameters(command, key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadCheckpoint(reader, 0)
            : null;
    }

    public async ValueTask<ChangeCheckpointWriteResult> CompareExchangeAsync(
        ChangeStreamStateKey key,
        long expectedGeneration,
        ChangeStreamCheckpoint replacement,
        ChangeStreamLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(lease);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        var state = await ReadLockedStateAsync(connection, transaction, key, cancellationToken)
            .ConfigureAwait(false);
        var current = state?.Checkpoint;
        if (state is null || !state.IsLeaseActive || !LeaseMatches(state.Lease, lease, key))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ChangeCheckpointWriteResult(ChangeCheckpointWriteStatus.Fenced, current);
        }

        if (!string.Equals(key.SourceFingerprint, replacement.Source.Fingerprint, StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ChangeCheckpointWriteResult(ChangeCheckpointWriteStatus.Incompatible, current);
        }

        if ((current?.StoreGeneration ?? -1) != expectedGeneration ||
            replacement.StoreGeneration != checked(expectedGeneration + 1))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ChangeCheckpointWriteResult(ChangeCheckpointWriteStatus.Conflict, current);
        }

        if (current is not null)
        {
            try
            {
                replacement.EnsureCompatibleWith(current);
            }
            catch (ChangeStreamCheckpointMismatchException)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ChangeCheckpointWriteResult(ChangeCheckpointWriteStatus.Incompatible, current);
            }

            if (replacement.AcknowledgedCommitPosition < current.AcknowledgedCommitPosition)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ChangeCheckpointWriteResult(ChangeCheckpointWriteStatus.BackwardMovement, current);
            }
        }

        await using (var command = CreateCommand(
                         connection,
                         transaction,
                         $"""
                         UPDATE {_table}
                         SET checkpoint_format = @format,
                             system_identifier = @system_identifier,
                             database_name = @database_name,
                             slot_name = @slot_name,
                             publication_fingerprint = @publication,
                             database_identity = @database_identity,
                             output_plugin = @output_plugin,
                             mapping_fingerprint = @mapping,
                             acknowledged_position = @position,
                             store_generation = @generation
                         WHERE source_fingerprint = @source AND consumer_group = @consumer
                         """))
        {
            AddKeyParameters(command, key);
            AddParameter(command, "format", replacement.FormatVersion);
            AddParameter(command, "system_identifier", replacement.Source.SystemIdentifier);
            AddParameter(command, "database_name", replacement.Source.DatabaseName);
            AddParameter(command, "slot_name", replacement.Source.SlotName);
            AddParameter(command, "publication", replacement.Source.PublicationFingerprint);
            AddParameter(command, "database_identity", replacement.DatabaseIdentity);
            AddParameter(command, "output_plugin", replacement.OutputPlugin);
            AddParameter(command, "mapping", replacement.MappingFingerprint);
            AddParameter(command, "position", (decimal)replacement.AcknowledgedCommitPosition.Value);
            AddParameter(command, "generation", replacement.StoreGeneration);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ChangeCheckpointWriteResult(ChangeCheckpointWriteStatus.Stored, replacement);
    }

    public async ValueTask<ChangeLeaseAcquireResult> AcquireAsync(
        ChangeStreamStateKey key,
        string ownerId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ValidateLeaseArguments(ownerId, duration);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        await using (var insert = CreateCommand(
                         connection,
                         transaction,
                         $"""
                         INSERT INTO {_table} (source_fingerprint, consumer_group)
                         VALUES (@source, @consumer)
                         ON CONFLICT (source_fingerprint, consumer_group) DO NOTHING
                         """))
        {
            AddKeyParameters(insert, key);
            _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var state = await ReadLockedStateAsync(connection, transaction, key, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("The state row could not be created.");
        if (state.IsLeaseActive &&
            state.Lease is not null &&
            !string.Equals(state.Lease.OwnerId, ownerId, StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ChangeLeaseAcquireResult(
                ChangeLeaseAcquireStatus.HeldByAnotherOwner,
                state.Lease);
        }

        var token = state.IsLeaseActive &&
                    state.Lease is not null &&
                    string.Equals(state.Lease.OwnerId, ownerId, StringComparison.Ordinal)
            ? state.Lease.FencingToken
            : checked(state.LastFencingToken + 1);
        var acquired = await WriteLeaseAsync(
            connection,
            transaction,
            key,
            ownerId,
            token,
            duration,
            updateLastToken: token > state.LastFencingToken,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ChangeLeaseAcquireResult(ChangeLeaseAcquireStatus.Acquired, acquired);
    }

    public async ValueTask<ChangeStreamLease?> RenewAsync(
        ChangeStreamLease lease,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateLeaseArguments(lease.OwnerId, duration);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = CreateCommand(
            connection,
            transaction: null,
            $"""
            UPDATE {_table}
            SET lease_expires = clock_timestamp() + (@duration_ms * interval '1 millisecond')
            WHERE source_fingerprint = @source AND consumer_group = @consumer
              AND lease_owner = @owner AND lease_token = @token
              AND lease_expires > clock_timestamp()
            RETURNING lease_expires
            """);
        AddKeyParameters(command, lease.Key);
        AddParameter(command, "owner", lease.OwnerId);
        AddParameter(command, "token", lease.FencingToken);
        AddParameter(command, "duration_ms", duration.TotalMilliseconds);
        var expires = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return expires is null || expires is DBNull
            ? null
            : lease with { ExpiresAt = ReadTimestamp(expires) };
    }

    public async ValueTask<bool> ReleaseAsync(
        ChangeStreamLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = CreateCommand(
            connection,
            transaction: null,
            $"""
            UPDATE {_table}
            SET lease_owner = NULL, lease_token = NULL, lease_expires = NULL
            WHERE source_fingerprint = @source AND consumer_group = @consumer
              AND lease_owner = @owner AND lease_token = @token
            """);
        AddKeyParameters(command, lease.Key);
        AddParameter(command, "owner", lease.OwnerId);
        AddParameter(command, "token", lease.FencingToken);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private async ValueTask<LockedState?> ReadLockedStateAsync(
        DbConnection connection,
        DbTransaction transaction,
        ChangeStreamStateKey key,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            $"""
            SELECT checkpoint_format, system_identifier, database_name, slot_name,
                   publication_fingerprint, database_identity, output_plugin,
                   mapping_fingerprint, acknowledged_position, store_generation,
                   lease_owner, lease_token, lease_expires,
                   COALESCE(lease_expires > clock_timestamp(), false),
                   last_fencing_token
            FROM {_table}
            WHERE source_fingerprint = @source AND consumer_group = @consumer
            FOR UPDATE
            """);
        AddKeyParameters(command, key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var checkpoint = ReadCheckpoint(reader, 0);
        ChangeStreamLease? lease = null;
        if (!reader.IsDBNull(10))
        {
            lease = new ChangeStreamLease(
                key,
                reader.GetString(10),
                reader.GetInt64(11),
                reader.GetFieldValue<DateTimeOffset>(12));
        }

        return new LockedState(
            checkpoint,
            lease,
            reader.GetBoolean(13),
            reader.GetInt64(14));
    }

    private async ValueTask<ChangeStreamLease> WriteLeaseAsync(
        DbConnection connection,
        DbTransaction transaction,
        ChangeStreamStateKey key,
        string ownerId,
        long token,
        TimeSpan duration,
        bool updateLastToken,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            $"""
            UPDATE {_table}
            SET lease_owner = @owner,
                lease_token = @token,
                lease_expires = clock_timestamp() + (@duration_ms * interval '1 millisecond'),
                last_fencing_token = CASE WHEN @update_last THEN @token ELSE last_fencing_token END
            WHERE source_fingerprint = @source AND consumer_group = @consumer
            RETURNING lease_expires
            """);
        AddKeyParameters(command, key);
        AddParameter(command, "owner", ownerId);
        AddParameter(command, "token", token);
        AddParameter(command, "duration_ms", duration.TotalMilliseconds);
        AddParameter(command, "update_last", updateLastToken);
        var expires = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ??
            throw new InvalidOperationException("The lease row disappeared while locked.");
        return new ChangeStreamLease(key, ownerId, token, ReadTimestamp(expires));
    }

    private static ChangeStreamCheckpoint? ReadCheckpoint(DbDataReader reader, int offset)
    {
        if (reader.IsDBNull(offset))
        {
            return null;
        }

        return new ChangeStreamCheckpoint(
            reader.GetInt32(offset),
            new ChangeSourceIdentity(
                reader.GetString(offset + 1),
                reader.GetString(offset + 2),
                reader.GetString(offset + 3),
                reader.GetString(offset + 4)),
            reader.GetString(offset + 5),
            reader.GetString(offset + 6),
            reader.GetString(offset + 7),
            new BlueTuskLogSequenceNumber(checked((ulong)reader.GetDecimal(offset + 8))),
            reader.GetInt64(offset + 9));
    }

    private static bool LeaseMatches(
        ChangeStreamLease? current,
        ChangeStreamLease candidate,
        ChangeStreamStateKey key) =>
        candidate.Key == key &&
        current is not null &&
        current.FencingToken == candidate.FencingToken &&
        string.Equals(current.OwnerId, candidate.OwnerId, StringComparison.Ordinal);

    private static DateTimeOffset ReadTimestamp(object value) => value switch
    {
        DateTimeOffset timestamp => timestamp,
        DateTime timestamp => new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
        _ => throw new InvalidOperationException(
            $"The PostgreSQL provider returned unsupported timestamp type '{value.GetType()}'."),
    };

    private static DbCommand CreateCommand(
        DbConnection connection,
        DbTransaction? transaction,
        string commandText)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Transaction = transaction;
        return command;
    }

    private static void AddKeyParameters(DbCommand command, ChangeStreamStateKey key)
    {
        AddParameter(command, "source", key.SourceFingerprint);
        AddParameter(command, "consumer", key.ConsumerGroup);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        _ = command.Parameters.Add(parameter);
    }

    private static async ValueTask ExecuteAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, commandText);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateLeaseArguments(string ownerId, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
    }

    private sealed record LockedState(
        ChangeStreamCheckpoint? Checkpoint,
        ChangeStreamLease? Lease,
        bool IsLeaseActive,
        long LastFencingToken);
}
