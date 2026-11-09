using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using BlueTusk.Streams.Storage.PostgreSql;
using BlueTusk.TypeSystem;

namespace BlueTusk.ControlPlane;

public sealed class PostgreSqlControlPlaneQueryOptions
{
    public int MaximumParallelInstances { get; init; } =
        Math.Clamp(Environment.ProcessorCount, 2, 8);

    public TimeSpan SnapshotCacheDuration { get; init; } =
        TimeSpan.FromMilliseconds(250);

    public TimeSpan InstanceTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan RefreshBudget { get; init; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumParallelInstances);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            SnapshotCacheDuration,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(InstanceTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(InstanceTimeout.TotalMilliseconds, uint.MaxValue - 1d);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RefreshBudget, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RefreshBudget.TotalMilliseconds, uint.MaxValue - 1d);
    }
}

[SuppressMessage("Design", "CA1001", Justification =
    "The concurrency semaphore never exposes its wait handle and must outlive timed-out provider reads.")]
public sealed class PostgreSqlControlPlaneQueryService : IControlPlaneQueryService
{
    private readonly ControlPlanePostgreSqlSource[] _instances;
    private readonly TimeProvider _timeProvider;
    private readonly PostgreSqlControlPlaneQueryOptions _options;
    private readonly Func<ControlPlanePostgreSqlSource, CancellationToken,
        ValueTask<IReadOnlyList<ControlPlaneSourceSnapshot>>> _readInstance;
    private readonly object _cacheGate = new();
    private readonly SemaphoreSlim _readConcurrency;
    private readonly Dictionary<string, PendingInstanceRead> _pendingReads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InstanceObservation> _lastGood = new(StringComparer.Ordinal);
    private ControlPlaneOverview? _cachedOverview;
    private DateTimeOffset _cacheExpiresAt;
    private Task<ControlPlaneOverview>? _inflightOverview;

    public PostgreSqlControlPlaneQueryService(
        IEnumerable<ControlPlanePostgreSqlSource> instances,
        TimeProvider? timeProvider = null)
        : this(instances, new PostgreSqlControlPlaneQueryOptions(), timeProvider)
    {
    }

    internal PostgreSqlControlPlaneQueryService(
        IEnumerable<ControlPlanePostgreSqlSource> instances,
        PostgreSqlControlPlaneQueryOptions options,
        TimeProvider? timeProvider,
        Func<ControlPlanePostgreSqlSource, CancellationToken,
            ValueTask<IReadOnlyList<ControlPlaneSourceSnapshot>>>? readInstance = null)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
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
        _options = options;
        _readInstance = readInstance ?? ReadInstanceAsync;
        _readConcurrency = new SemaphoreSlim(options.MaximumParallelInstances);
    }

    public static PostgreSqlControlPlaneQueryService Create(
        IEnumerable<ControlPlanePostgreSqlSource> instances,
        PostgreSqlControlPlaneQueryOptions options,
        TimeProvider? timeProvider = null) =>
        new(instances, options, timeProvider);

    public async ValueTask<ControlPlaneOverview> GetOverviewAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<ControlPlaneOverview> flight;
        TaskCompletionSource<ControlPlaneOverview>? refresh = null;
        lock (_cacheGate)
        {
            var now = _timeProvider.GetUtcNow();
            if (_cachedOverview is not null && now < _cacheExpiresAt)
            {
                return _cachedOverview;
            }

            if (_inflightOverview is null)
            {
                refresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _inflightOverview = refresh.Task;
            }
            flight = _inflightOverview;
        }

        // Install the flight before starting work, including synchronously completing reads.
        if (refresh is not null)
        {
            _ = LoadAndCacheOverviewAsync(refresh);
        }
        return await flight.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task LoadAndCacheOverviewAsync(TaskCompletionSource<ControlPlaneOverview> completion)
    {
        try
        {
            using var budget = new CancellationTokenSource(_options.RefreshBudget, _timeProvider);
            var results = await Task.WhenAll(_instances.Select(instance =>
                ObserveInstanceAsync(instance, budget.Token))).ConfigureAwait(false);
            var overview = new ControlPlaneOverview(
                _timeProvider.GetUtcNow(),
                results.SelectMany(result => result.Sources)
                    .OrderBy(source => source.InstanceName, StringComparer.Ordinal)
                    .ThenBy(source => source.DatabaseName, StringComparer.Ordinal)
                    .ThenBy(source => source.SlotName, StringComparer.Ordinal)
                    .ToArray())
            {
                Instances = results.Select(result => result.Status)
                    .OrderBy(status => status.InstanceName, StringComparer.Ordinal).ToArray(),
            };
            lock (_cacheGate)
            {
                _cachedOverview = overview;
                _cacheExpiresAt = overview.ObservedAt + _options.SnapshotCacheDuration;
                _inflightOverview = null;
            }
            completion.TrySetResult(overview);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            lock (_cacheGate)
            {
                if (ReferenceEquals(_inflightOverview, completion.Task))
                {
                    _inflightOverview = null;
                }
            }
        }
    }

    private async Task<InstanceResult> ObserveInstanceAsync(
        ControlPlanePostgreSqlSource instance, CancellationToken budget)
    {
        var attemptedAt = _timeProvider.GetUtcNow();
        PendingInstanceRead pending;
        var start = false;
        lock (_cacheGate)
        {
            if (!_pendingReads.TryGetValue(instance.InstanceName, out pending!))
            {
                pending = new PendingInstanceRead();
                _pendingReads.Add(instance.InstanceName, pending);
                start = true;
            }
        }
        if (start)
        {
            _ = ReadAndCompleteInstanceAsync(instance, pending, budget);
        }

        InstanceObservation? observation = null;
        string? diagnostic;
        try
        {
            var result = await pending.Completion.Task.WaitAsync(
                _options.InstanceTimeout, _timeProvider, budget).ConfigureAwait(false);
            observation = result.Observation;
            diagnostic = result.Diagnostic;
        }
        catch (TimeoutException)
        {
            pending.Cancel();
            diagnostic = "instance_timeout";
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            pending.Cancel();
            diagnostic = "refresh_budget_exceeded";
        }

        lock (_cacheGate)
        {
            if (observation is not null)
            {
                _lastGood[instance.InstanceName] = observation;
            }
            else
            {
                _lastGood.TryGetValue(instance.InstanceName, out observation);
            }
        }
        return new InstanceResult(
            observation is null ? [] : diagnostic is null ? observation.Sources
                : observation.Sources.Select(source => source with { IsStale = true }).ToArray(),
            new ControlPlaneInstanceStatus(instance.InstanceName,
                diagnostic is null ? ControlPlaneInstanceAvailability.Current
                    : observation is not null ? ControlPlaneInstanceAvailability.Stale
                    : ControlPlaneInstanceAvailability.Unavailable,
                attemptedAt, observation?.ObservedAt, diagnostic));
    }

    private async Task ReadAndCompleteInstanceAsync(
        ControlPlanePostgreSqlSource instance, PendingInstanceRead pending, CancellationToken budget)
    {
        var acquired = false;
        InstanceReadResult result;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(budget, pending.Cancellation.Token);
            await _readConcurrency.WaitAsync(linked.Token).ConfigureAwait(false);
            acquired = true;
            var sources = await _readInstance(instance, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            var observedAt = _timeProvider.GetUtcNow();
            result = new(new InstanceObservation(observedAt,
                sources.Select(source => source with { ObservedAt = observedAt }).ToArray()), null);
        }
        catch (OperationCanceledException)
        {
            result = new(null, budget.IsCancellationRequested ? "refresh_budget_exceeded" : "instance_timeout");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Provider messages can contain credentials or connection strings.
            result = new(null, exception is ControlPlaneStorageVersionException
                ? "unsupported_storage_version" : "instance_unavailable");
        }
        finally
        {
            if (acquired)
            {
                _readConcurrency.Release();
            }
            lock (_cacheGate)
            {
                _pendingReads.Remove(instance.InstanceName);
            }
            pending.Cancellation.Dispose();
        }
        pending.Completion.TrySetResult(result);
    }

    private sealed record InstanceObservation(DateTimeOffset ObservedAt, IReadOnlyList<ControlPlaneSourceSnapshot> Sources);
    private sealed record InstanceReadResult(InstanceObservation? Observation, string? Diagnostic);
    private sealed record InstanceResult(IReadOnlyList<ControlPlaneSourceSnapshot> Sources, ControlPlaneInstanceStatus Status);

    private sealed class PendingInstanceRead
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<InstanceReadResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Cancel()
        {
            try { Cancellation.Cancel(); }
            catch (ObjectDisposedException) { /* The read completed at the deadline. */ }
        }
    }

    private static async ValueTask<IReadOnlyList<ControlPlaneSourceSnapshot>> ReadInstanceAsync(
        ControlPlanePostgreSqlSource instance,
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
        var inventory = await ReadSetBasedInventoryAsync(
            connection,
            transaction,
            instance,
            cancellationToken).ConfigureAwait(false);
        var slots = await ReadSlotsAsync(
            instance.SourceDataSource,
            registeredSources,
            cancellationToken).ConfigureAwait(false);
        var destination = new List<ControlPlaneSourceSnapshot>(registeredSources.Count);
        foreach (var source in registeredSources)
        {
            var key = new SourceEpochKey(source.SourceFingerprint, source.SourceEpoch);
            var slot = slots.TryGetValue(source.SlotName, out var slotSnapshot)
                ? slotSnapshot
                : MissingSlot();
            var relay = inventory.Relays.TryGetValue(key, out var relaySnapshot)
                ? relaySnapshot
                : new ControlPlaneRelaySnapshot(0, 0, 0, 0, 0, TimeSpan.Zero);
            var groups = inventory.Groups.TryGetValue(key, out var groupSnapshots)
                ? groupSnapshots
                : [];
            var snapshots = inventory.Snapshots.TryGetValue(key, out var snapshotRuns)
                ? snapshotRuns
                : [];
            var checkpoints = inventory.Checkpoints.TryGetValue(
                source.SourceFingerprint,
                out var checkpointSnapshots)
                ? checkpointSnapshots
                : [];
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
        return destination;
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

    private static async ValueTask<SetBasedInventory> ReadSetBasedInventoryAsync(
        DbConnection connection,
        DbTransaction transaction,
        ControlPlanePostgreSqlSource instance,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             SELECT candidate.source_fingerprint, candidate.source_epoch,
                    COUNT(candidate.sequence),
                    COALESCE(SUM(octet_length(candidate.envelope)), 0),
                    COALESCE(MIN(candidate.sequence), 0),
                    COALESCE(MAX(candidate.sequence), 0),
                    COALESCE((
                        SELECT MIN(consumer.checkpoint_sequence)
                        FROM {instance.QuotedControlSchema}.relay_consumer_groups AS consumer
                        WHERE consumer.source_fingerprint = candidate.source_fingerprint
                          AND consumer.source_epoch = candidate.source_epoch
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
             GROUP BY candidate.source_fingerprint, candidate.source_epoch
             ORDER BY candidate.source_fingerprint, candidate.source_epoch;

             SELECT source_fingerprint, source_epoch, consumer_group,
                    start_sequence, checkpoint_sequence, store_generation, active,
                    lease_expires IS NOT NULL AND lease_expires > clock_timestamp(),
                    lease_expires, last_fencing_token, removed_at,
                    retention_protected_until
             FROM {instance.QuotedControlSchema}.relay_consumer_groups
             ORDER BY source_fingerprint, source_epoch, consumer_group;

             SELECT source_fingerprint, source_epoch, snapshot_epoch, state,
                    COALESCE(octet_length(progress), 0), updated_at
             FROM {instance.QuotedControlSchema}.snapshot_runs
             ORDER BY source_fingerprint, source_epoch, updated_at DESC, snapshot_epoch;

             SELECT source_fingerprint, consumer_group, checkpoint_format,
                    slot_name, output_plugin, mapping_fingerprint,
                    acknowledged_position, store_generation,
                    lease_expires IS NOT NULL AND lease_expires > clock_timestamp(),
                    lease_expires, last_fencing_token
             FROM {instance.QuotedControlSchema}.stream_state
             WHERE checkpoint_format IS NOT NULL
             ORDER BY source_fingerprint, consumer_group
             """;

        var relays = new Dictionary<SourceEpochKey, ControlPlaneRelaySnapshot>();
        var groups = new Dictionary<SourceEpochKey, List<ControlPlaneConsumerGroupSnapshot>>();
        var snapshots = new Dictionary<SourceEpochKey, List<ControlPlaneSnapshotRunSnapshot>>();
        var checkpoints = new Dictionary<string, List<ControlPlaneCheckpointSnapshot>>(
            StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            relays.Add(
                new SourceEpochKey(reader.GetString(0), reader.GetInt64(1)),
                new ControlPlaneRelaySnapshot(
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.GetInt64(6),
                    TimeSpan.FromSeconds(reader.GetDouble(7))));
        }

        _ = await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = new SourceEpochKey(reader.GetString(0), reader.GetInt64(1));
            GetOrAdd(groups, key).Add(new ControlPlaneConsumerGroupSnapshot(
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetBoolean(6),
                reader.GetBoolean(7),
                ReadNullableTimestamp(reader, 8),
                reader.GetInt64(9),
                ReadNullableTimestamp(reader, 10),
                ReadNullableTimestamp(reader, 11)));
        }

        _ = await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = new SourceEpochKey(reader.GetString(0), reader.GetInt64(1));
            GetOrAdd(snapshots, key).Add(new ControlPlaneSnapshotRunSnapshot(
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                ReadTimestamp(reader.GetValue(5))));
        }

        _ = await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var source = reader.GetString(0);
            if (!checkpoints.TryGetValue(source, out var destination))
            {
                destination = [];
                checkpoints.Add(source, destination);
            }

            destination.Add(new ControlPlaneCheckpointSnapshot(
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                FormatPosition(checked((ulong)reader.GetDecimal(6))),
                reader.GetInt64(7),
                reader.GetBoolean(8),
                ReadNullableTimestamp(reader, 9),
                reader.GetInt64(10)));
        }

        return new SetBasedInventory(relays, groups, snapshots, checkpoints);
    }

    private static async ValueTask<IReadOnlyDictionary<string, ControlPlaneSlotSnapshot>>
        ReadSlotsAsync(
            DbDataSource dataSource,
            IReadOnlyList<SourceRow> sources,
            CancellationToken cancellationToken)
    {
        if (sources.Count == 0)
        {
            return new Dictionary<string, ControlPlaneSlotSnapshot>(StringComparer.Ordinal);
        }

        var names = sources.Select(static source => source.SlotName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            var parameters = new string[names.Length];
            for (var index = 0; index < names.Length; index++)
            {
                parameters[index] = $"@slot{index}";
                AddParameter(command, parameters[index][1..], names[index]);
            }

            command.CommandText =
                $"""
                 SELECT slot_name, active, plugin, restart_lsn::text,
                        confirmed_flush_lsn::text, wal_status,
                        COALESCE(pg_wal_lsn_diff(
                            pg_current_wal_lsn(), confirmed_flush_lsn), 0)
                 FROM pg_catalog.pg_replication_slots
                 WHERE slot_type = 'logical'
                   AND slot_name IN ({string.Join(", ", parameters)})
                 """;
            var slots = new Dictionary<string, ControlPlaneSlotSnapshot>(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                slots.Add(
                    reader.GetString(0),
                    new ControlPlaneSlotSnapshot(
                        SourceReachable: true,
                        Exists: true,
                        reader.GetBoolean(1),
                        reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        SaturatingInt64(reader.GetDecimal(6)),
                        DiagnosticCode: null));
            }

            return slots;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            return names.ToDictionary(
                static name => name,
                static _ => UnreachableSlot(),
                StringComparer.Ordinal);
        }
    }

    private static List<TValue> GetOrAdd<TKey, TValue>(
        Dictionary<TKey, List<TValue>> values,
        TKey key)
        where TKey : notnull
    {
        if (!values.TryGetValue(key, out var destination))
        {
            destination = [];
            values.Add(key, destination);
        }

        return destination;
    }

    private static ControlPlaneSlotSnapshot MissingSlot() =>
        new(
            SourceReachable: true,
            Exists: false,
            Active: false,
            OutputPlugin: null,
            RestartPosition: null,
            ConfirmedFlushPosition: null,
            WalStatus: null,
            WalLagBytes: 0,
            DiagnosticCode: "slot-missing");

    private static ControlPlaneSlotSnapshot UnreachableSlot() =>
        new(
            SourceReachable: false,
            Exists: false,
            Active: false,
            OutputPlugin: null,
            RestartPosition: null,
            ConfirmedFlushPosition: null,
            WalStatus: null,
            WalLagBytes: 0,
            DiagnosticCode: "source-unavailable");

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

    private readonly record struct SourceEpochKey(
        string SourceFingerprint,
        long SourceEpoch);

    private sealed record SetBasedInventory(
        IReadOnlyDictionary<SourceEpochKey, ControlPlaneRelaySnapshot> Relays,
        IReadOnlyDictionary<SourceEpochKey, List<ControlPlaneConsumerGroupSnapshot>> Groups,
        IReadOnlyDictionary<SourceEpochKey, List<ControlPlaneSnapshotRunSnapshot>> Snapshots,
        IReadOnlyDictionary<string, List<ControlPlaneCheckpointSnapshot>> Checkpoints);
}

public sealed class ControlPlaneStorageVersionException : Exception
{
    public ControlPlaneStorageVersionException(string message)
        : base(message)
    {
    }
}
