using System.Data;
using System.Data.Common;
using BlueTusk.Streams.Storage.PostgreSql;
using BlueTusk.TypeSystem;

namespace BlueTusk.ControlPlane;

public sealed class PostgreSqlControlPlaneQueryService : IControlPlaneQueryService
{
    private readonly ControlPlanePostgreSqlSource[] _instances;
    private readonly TimeProvider _timeProvider;

    public PostgreSqlControlPlaneQueryService(
        IEnumerable<ControlPlanePostgreSqlSource> instances,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(instances);
        _instances = instances.ToArray();
        if (_instances.Length == 0)
        {
            throw new ArgumentException(
                "At least one PostgreSQL control-plane source is required.",
                nameof(instances));
        }

        var duplicate = _instances
            .GroupBy(instance => instance.InstanceName, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Control-plane instance name '{duplicate.Key}' is duplicated.",
                nameof(instances));
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<ControlPlaneOverview> GetOverviewAsync(
        CancellationToken cancellationToken = default)
    {
        var sources = new List<ControlPlaneSourceSnapshot>();
        foreach (var instance in _instances)
        {
            await ReadInstanceAsync(instance, sources, cancellationToken).ConfigureAwait(false);
        }

        return new ControlPlaneOverview(
            _timeProvider.GetUtcNow(),
            sources.OrderBy(source => source.InstanceName, StringComparer.Ordinal)
                .ThenBy(source => source.DatabaseName, StringComparer.Ordinal)
                .ThenBy(source => source.SlotName, StringComparer.Ordinal)
                .ToArray());
    }

    private static async ValueTask ReadInstanceAsync(
        ControlPlanePostgreSqlSource instance,
        List<ControlPlaneSourceSnapshot> destination,
        CancellationToken cancellationToken)
    {
        await using var connection = await instance.ControlDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSupportedSchemaAsync(connection, transaction, instance, cancellationToken)
            .ConfigureAwait(false);
        var registeredSources = await ReadRegisteredSourcesAsync(
            connection,
            transaction,
            instance,
            cancellationToken).ConfigureAwait(false);
        foreach (var source in registeredSources)
        {
            var slot = await ReadSlotAsync(instance.SourceDataSource, source.SlotName, cancellationToken)
                .ConfigureAwait(false);
            var relay = await ReadRelayAsync(connection, transaction, instance, source, cancellationToken)
                .ConfigureAwait(false);
            var groups = await ReadGroupsAsync(connection, transaction, instance, source, cancellationToken)
                .ConfigureAwait(false);
            var snapshots = await ReadSnapshotsAsync(connection, transaction, instance, source, cancellationToken)
                .ConfigureAwait(false);
            var checkpoints = await ReadCheckpointsAsync(connection, transaction, instance, source, cancellationToken)
                .ConfigureAwait(false);
            destination.Add(new ControlPlaneSourceSnapshot(
                instance.InstanceName + ":" + source.SourceFingerprint,
                instance.InstanceName,
                source.SourceFingerprint,
                source.SystemIdentifier,
                source.DatabaseName,
                source.SlotName,
                source.PublicationFingerprint,
                source.SourceEpoch,
                source.LastSequence,
                FormatPosition(source.LastCommitPosition),
                slot,
                relay,
                groups,
                snapshots,
                checkpoints));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask EnsureSupportedSchemaAsync(
        DbConnection connection,
        DbTransaction transaction,
        ControlPlanePostgreSqlSource instance,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT schema_version FROM {instance.QuotedControlSchema}.storage_metadata WHERE singleton";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var version = value is null or DBNull
            ? 0
            : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        if (version != PostgreSqlDurableChangeRelay.CurrentSchemaVersion)
        {
            throw new ControlPlaneStorageVersionException(
                $"Control-plane inventory requires relay schema version " +
                $"{PostgreSqlDurableChangeRelay.CurrentSchemaVersion}; found {version}.");
        }
    }

    private static async ValueTask<IReadOnlyList<SourceRow>> ReadRegisteredSourcesAsync(
        DbConnection connection,
        DbTransaction transaction,
        ControlPlanePostgreSqlSource instance,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             SELECT source_fingerprint, system_identifier, database_name,
                    slot_name, publication_fingerprint, source_epoch,
                    last_sequence, last_commit_position
             FROM {instance.QuotedControlSchema}.relay_sources
             ORDER BY database_name, slot_name
             """;
        var sources = new List<SourceRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sources.Add(new SourceRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                checked((ulong)reader.GetDecimal(7))));
        }

        return sources;
    }

    private static async ValueTask<ControlPlaneSlotSnapshot> ReadSlotAsync(
        DbDataSource dataSource,
        string slotName,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT active, plugin, restart_lsn::text,
                       confirmed_flush_lsn::text, wal_status,
                       COALESCE(pg_wal_lsn_diff(
                           pg_current_wal_lsn(), confirmed_flush_lsn), 0)
                FROM pg_catalog.pg_replication_slots
                WHERE slot_name = @slot AND slot_type = 'logical'
                """;
            AddParameter(command, "slot", slotName);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new ControlPlaneSlotSnapshot(
                    SourceReachable: true,
                    Exists: false,
                    Active: false,
                    OutputPlugin: null,
                    RestartPosition: null,
                    ConfirmedFlushPosition: null,
                    WalStatus: null,
                    WalLagBytes: 0,
                    DiagnosticCode: "slot-missing");
            }

            return new ControlPlaneSlotSnapshot(
                SourceReachable: true,
                Exists: true,
                reader.GetBoolean(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                SaturatingInt64(reader.GetDecimal(5)),
                DiagnosticCode: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            return new ControlPlaneSlotSnapshot(
                SourceReachable: false,
                Exists: false,
                Active: false,
                OutputPlugin: null,
                RestartPosition: null,
                ConfirmedFlushPosition: null,
                WalStatus: null,
                WalLagBytes: 0,
                DiagnosticCode: "source-unavailable");
        }
    }

    private static async ValueTask<ControlPlaneRelaySnapshot> ReadRelayAsync(
        DbConnection connection,
        DbTransaction transaction,
        ControlPlanePostgreSqlSource instance,
        SourceRow source,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             SELECT COUNT(candidate.sequence),
                    COALESCE(SUM(octet_length(candidate.envelope)), 0),
                    COALESCE(MIN(candidate.sequence), 0),
                    COALESCE(MAX(candidate.sequence), 0),
                    COALESCE((
                        SELECT MIN(consumer.checkpoint_sequence)
                        FROM {instance.QuotedControlSchema}.relay_consumer_groups AS consumer
                        WHERE consumer.source_fingerprint = @source
                          AND consumer.source_epoch = @epoch
                          AND (consumer.active OR
                               consumer.retention_protected_until > clock_timestamp())), 0),
                    COALESCE(EXTRACT(EPOCH FROM (
                        clock_timestamp() - MIN(candidate.appended_at) FILTER (
                            WHERE EXISTS (
                                SELECT 1
                                FROM {instance.QuotedControlSchema}.relay_consumer_groups AS pending
                                WHERE pending.source_fingerprint = candidate.source_fingerprint
                                  AND pending.source_epoch = candidate.source_epoch
                                  AND (pending.active OR
                                       pending.retention_protected_until > clock_timestamp())
                                  AND pending.start_sequence < candidate.sequence
                                  AND pending.checkpoint_sequence < candidate.sequence))))::double precision, 0)
             FROM {instance.QuotedControlSchema}.relay_transactions AS candidate
             WHERE candidate.source_fingerprint = @source
               AND candidate.source_epoch = @epoch
             """;
        AddSourceParameters(command, source);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new ControlPlaneRelaySnapshot(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            TimeSpan.FromSeconds(reader.GetDouble(5)));
    }

    private static async ValueTask<IReadOnlyList<ControlPlaneConsumerGroupSnapshot>> ReadGroupsAsync(
        DbConnection connection,
        DbTransaction transaction,
        ControlPlanePostgreSqlSource instance,
        SourceRow source,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             SELECT consumer_group, start_sequence, checkpoint_sequence,
                    store_generation, active,
                    lease_expires IS NOT NULL AND lease_expires > clock_timestamp(),
                    lease_expires, last_fencing_token, removed_at,
                    retention_protected_until
             FROM {instance.QuotedControlSchema}.relay_consumer_groups
             WHERE source_fingerprint = @source AND source_epoch = @epoch
             ORDER BY consumer_group
             """;
        AddSourceParameters(command, source);
        var groups = new List<ControlPlaneConsumerGroupSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            groups.Add(new ControlPlaneConsumerGroupSnapshot(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetBoolean(4),
                reader.GetBoolean(5),
                ReadNullableTimestamp(reader, 6),
                reader.GetInt64(7),
                ReadNullableTimestamp(reader, 8),
                ReadNullableTimestamp(reader, 9)));
        }

        return groups;
    }

    private static async ValueTask<IReadOnlyList<ControlPlaneSnapshotRunSnapshot>> ReadSnapshotsAsync(
        DbConnection connection,
        DbTransaction transaction,
        ControlPlanePostgreSqlSource instance,
        SourceRow source,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             SELECT snapshot_epoch, state, COALESCE(octet_length(progress), 0),
                    updated_at
             FROM {instance.QuotedControlSchema}.snapshot_runs
             WHERE source_fingerprint = @source AND source_epoch = @epoch
             ORDER BY updated_at DESC, snapshot_epoch
             """;
        AddSourceParameters(command, source);
        var snapshots = new List<ControlPlaneSnapshotRunSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            snapshots.Add(new ControlPlaneSnapshotRunSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                ReadTimestamp(reader.GetValue(3))));
        }

        return snapshots;
    }

    private static async ValueTask<IReadOnlyList<ControlPlaneCheckpointSnapshot>> ReadCheckpointsAsync(
        DbConnection connection,
        DbTransaction transaction,
        ControlPlanePostgreSqlSource instance,
        SourceRow source,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             SELECT consumer_group, checkpoint_format, slot_name, output_plugin,
                    mapping_fingerprint, acknowledged_position, store_generation,
                    lease_expires IS NOT NULL AND lease_expires > clock_timestamp(),
                    lease_expires, last_fencing_token
             FROM {instance.QuotedControlSchema}.stream_state
             WHERE source_fingerprint = @source AND checkpoint_format IS NOT NULL
             ORDER BY consumer_group
             """;
        AddParameter(command, "source", source.SourceFingerprint);
        var checkpoints = new List<ControlPlaneCheckpointSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            checkpoints.Add(new ControlPlaneCheckpointSnapshot(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                FormatPosition(checked((ulong)reader.GetDecimal(5))),
                reader.GetInt64(6),
                reader.GetBoolean(7),
                ReadNullableTimestamp(reader, 8),
                reader.GetInt64(9)));
        }

        return checkpoints;
    }

    private static string FormatPosition(ulong value) =>
        new BlueTuskLogSequenceNumber(value).ToString();

    private static long SaturatingInt64(decimal value) => value >= long.MaxValue
        ? long.MaxValue
        : value <= 0
            ? 0
            : decimal.ToInt64(value);

    private static DateTimeOffset? ReadNullableTimestamp(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadTimestamp(reader.GetValue(ordinal));

    private static DateTimeOffset ReadTimestamp(object value) => value switch
    {
        DateTimeOffset timestamp => timestamp,
        DateTime timestamp => new(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
        _ => throw new InvalidOperationException(
            $"The PostgreSQL provider returned unsupported timestamp type '{value.GetType()}'."),
    };

    private static void AddSourceParameters(DbCommand command, SourceRow source)
    {
        AddParameter(command, "source", source.SourceFingerprint);
        AddParameter(command, "epoch", source.SourceEpoch);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        _ = command.Parameters.Add(parameter);
    }

    private sealed record SourceRow(
        string SourceFingerprint,
        string SystemIdentifier,
        string DatabaseName,
        string SlotName,
        string PublicationFingerprint,
        long SourceEpoch,
        long LastSequence,
        ulong LastCommitPosition);
}

public sealed class ControlPlaneStorageVersionException : Exception
{
    public ControlPlaneStorageVersionException(string message)
        : base(message)
    {
    }
}
