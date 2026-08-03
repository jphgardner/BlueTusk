using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using BlueTusk.Streams;

namespace BlueTusk.Live.DependencyInjection;

public sealed class PostgreSqlLiveStoreOptions
{
    public required DbDataSource ControlDataSource { get; init; }

    public string ControlSchema { get; init; } = "bluetusk_streams";

    public int MaximumDependenciesPerTransaction { get; init; } = 1_024;

    public int MaximumDependenciesPerQuery { get; init; } = 128;

    public TimeSpan ReplayRetentionWindow { get; init; } = TimeSpan.FromHours(1);

    public int MaximumReplayEventBytes { get; init; } = 4 * 1024 * 1024;

    public int ReplayPruneBatchSize { get; init; } = 1_000;

    internal string QuotedControlSchema => QuoteIdentifier(ControlSchema);

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(ControlDataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(ControlSchema);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDependenciesPerTransaction);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDependenciesPerQuery);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ReplayRetentionWindow, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumReplayEventBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ReplayPruneBatchSize);
        if (ControlSchema.Contains('\0') || Encoding.UTF8.GetByteCount(ControlSchema) > 63)
        {
            throw new ArgumentException(
                "The Live control schema must be a valid PostgreSQL identifier of at most 63 UTF-8 bytes.",
                nameof(ControlSchema));
        }
    }

    private static string QuoteIdentifier(string value) =>
        '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}

public sealed class PostgreSqlLiveInvalidationStore :
    ILiveInvalidationLog,
    ILiveInvalidationSink,
    ILiveReplayStore
{
    public const int CurrentSchemaVersion = 2;

    private readonly PostgreSqlLiveStoreOptions _options;
    private readonly DbDataSource _dataSource;
    private readonly string _schema;
    private readonly string _metadataTable;
    private readonly string _invalidationsTable;
    private readonly string _dependenciesTable;
    private readonly string _replaySubscriptionsTable;
    private readonly string _replayEventsTable;
    private readonly object _initializeLock = new();
    private Task? _initializationTask;

    public PostgreSqlLiveInvalidationStore(PostgreSqlLiveStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _dataSource = options.ControlDataSource;
        _schema = options.QuotedControlSchema;
        _metadataTable = _schema + ".live_storage_metadata";
        _invalidationsTable = _schema + ".live_invalidations";
        _dependenciesTable = _schema + ".live_invalidation_dependencies";
        _replaySubscriptionsTable = _schema + ".live_replay_subscriptions";
        _replayEventsTable = _schema + ".live_replay_events";
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        Task initialization;
        lock (_initializeLock)
        {
            initialization = _initializationTask ??= InitializeCoreAsync();
        }

        try
        {
            await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_initializeLock)
            {
                if (ReferenceEquals(_initializationTask, initialization) && initialization.IsFaulted)
                {
                    _initializationTask = null;
                }
            }

            throw;
        }
    }

    public async ValueTask<LiveInvalidationCursor> AppendAsync(
        string databaseIdentity,
        ChangeTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseIdentity);
        ArgumentNullException.ThrowIfNull(transaction);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var dependencies = await LiveChangeDependencyExtractor.ExtractAsync(
            transaction,
            _options.MaximumDependenciesPerTransaction,
            cancellationToken).ConfigureAwait(false);
        if (dependencies.Count == 0)
        {
            return await GetCurrentCursorAsync(databaseIdentity, cancellationToken).ConfigureAwait(false);
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var dbTransaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        var cursor = await InsertInvalidationAsync(
            connection,
            dbTransaction,
            databaseIdentity,
            transaction,
            cancellationToken).ConfigureAwait(false);
        foreach (var dependency in dependencies)
        {
            await ExecuteAsync(
                connection,
                dbTransaction,
                $"""
                INSERT INTO {_dependenciesTable} (invalidation_cursor, schema_name, table_name)
                VALUES (@cursor, @schema, @table)
                ON CONFLICT DO NOTHING
                """,
                cancellationToken,
                ("cursor", cursor.Value),
                ("schema", dependency.Schema),
                ("table", dependency.Table)).ConfigureAwait(false);
        }

        await dbTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return cursor;
    }

    public async ValueTask<LiveInvalidationCursor> GetCurrentCursorAsync(
        string databaseIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseIdentity);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COALESCE(max(cursor), 0) FROM {_invalidationsTable} WHERE database_identity = @database";
        AddParameter(command, "database", databaseIdentity);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return new LiveInvalidationCursor(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
    }

    public async ValueTask<bool> HasChangesAsync(
        string databaseIdentity,
        IReadOnlyCollection<LiveTableDependency> dependencies,
        LiveInvalidationCursor afterExclusive,
        LiveInvalidationCursor throughInclusive,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseIdentity);
        ArgumentNullException.ThrowIfNull(dependencies);
        if (dependencies.Count == 0)
        {
            throw new ArgumentException("At least one Live table dependency is required.", nameof(dependencies));
        }

        if (dependencies.Count > _options.MaximumDependenciesPerQuery)
        {
            throw new ArgumentException(
                $"A Live invalidation query cannot exceed {_options.MaximumDependenciesPerQuery} dependencies.",
                nameof(dependencies));
        }

        if (afterExclusive.Value < 0 || throughInclusive.Value < afterExclusive.Value)
        {
            throw new ArgumentOutOfRangeException(
                nameof(throughInclusive),
                "Live invalidation cursor ranges must be non-negative and monotonic.");
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var dependencyArray = dependencies.Distinct().ToArray();
        var predicates = string.Join(
            " OR ",
            dependencyArray.Select((_, index) =>
                $"(d.schema_name = @schema{index} AND d.table_name = @table{index})"));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT EXISTS (
                SELECT 1
                FROM {_invalidationsTable} i
                JOIN {_dependenciesTable} d ON d.invalidation_cursor = i.cursor
                WHERE i.database_identity = @database
                  AND i.cursor > @after
                  AND i.cursor <= @through
                  AND ({predicates}))
            """;
        AddParameter(command, "database", databaseIdentity);
        AddParameter(command, "after", afterExclusive.Value);
        AddParameter(command, "through", throughInclusive.Value);
        for (var index = 0; index < dependencyArray.Length; index++)
        {
            AddParameter(command, $"schema{index}", dependencyArray[index].Schema);
            AddParameter(command, $"table{index}", dependencyArray[index].Table);
        }

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async ValueTask<LiveReplayAppendResult> AppendReplayAsync(
        LiveReplayAppendRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (var replayEvent in request.Events)
        {
            if (replayEvent.Payload.Length > _options.MaximumReplayEventBytes)
            {
                throw new PostgreSqlLiveStoreException(
                    $"Live replay event {replayEvent.Sequence} exceeds the configured {_options.MaximumReplayEventBytes}-byte limit.");
            }

            if (!LiveReplayJsonSerializer.VerifyIntegrity(replayEvent))
            {
                throw new PostgreSqlLiveStoreException(
                    $"Live replay event {replayEvent.Sequence} failed its integrity check before persistence.");
            }
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            $"""
            INSERT INTO {_replaySubscriptionsTable} (
                identity_fingerprint, first_available_sequence, last_sequence, retain_until)
            VALUES (@identity, 1, 0, clock_timestamp() + (@retention_ms * interval '1 millisecond'))
            ON CONFLICT (identity_fingerprint) DO NOTHING
            """,
            cancellationToken,
            ("identity", request.Identity.Fingerprint),
            ("retention_ms", _options.ReplayRetentionWindow.TotalMilliseconds)).ConfigureAwait(false);
        var state = await ReadReplayStateAsync(
            connection,
            transaction,
            request.Identity.Fingerprint,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false) ??
            throw new PostgreSqlLiveStoreException("Live replay subscription state disappeared during append.");
        var finalSequence = request.Events[^1].Sequence;
        if (state.LastSequence != request.ExpectedLastSequence)
        {
            if (state.LastSequence >= finalSequence &&
                await EventsMatchAsync(connection, transaction, request, cancellationToken).ConfigureAwait(false))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new LiveReplayAppendResult(LiveReplayAppendStatus.AlreadyStored, state.LastSequence);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new LiveReplayAppendResult(LiveReplayAppendStatus.SequenceConflict, state.LastSequence);
        }

        foreach (var replayEvent in request.Events)
        {
            await ExecuteAsync(
                connection,
                transaction,
                $"""
                INSERT INTO {_replayEventsTable} (
                    identity_fingerprint, sequence, event_kind, content_type, payload, integrity_hash)
                VALUES (@identity, @sequence, @kind, @content_type, @payload, @integrity)
                """,
                cancellationToken,
                ("identity", request.Identity.Fingerprint),
                ("sequence", replayEvent.Sequence),
                ("kind", (int)replayEvent.Kind),
                ("content_type", replayEvent.ContentType),
                ("payload", replayEvent.Payload.ToArray()),
                ("integrity", replayEvent.IntegrityHash.ToArray())).ConfigureAwait(false);
        }

        await ExecuteAsync(
            connection,
            transaction,
            $"""
            UPDATE {_replaySubscriptionsTable}
            SET last_sequence = @last,
                retain_until = clock_timestamp() + (@retention_ms * interval '1 millisecond'),
                updated_at = clock_timestamp()
            WHERE identity_fingerprint = @identity
            """,
            cancellationToken,
            ("last", finalSequence),
            ("retention_ms", _options.ReplayRetentionWindow.TotalMilliseconds),
            ("identity", request.Identity.Fingerprint)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new LiveReplayAppendResult(LiveReplayAppendStatus.Stored, finalSequence);
    }

    ValueTask<LiveReplayAppendResult> ILiveReplayStore.AppendAsync(
        LiveReplayAppendRequest request,
        CancellationToken cancellationToken) =>
        AppendReplayAsync(request, cancellationToken);

    public async ValueTask<LiveReplayReadResult> ReadAsync(
        LiveSubscriptionIdentity identity,
        long afterSequence,
        int maximumEvents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEvents);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var state = await ReadReplayStateAsync(
            connection,
            transaction: null,
            identity.Fingerprint,
            forUpdate: false,
            cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return new LiveReplayReadResult(LiveReplayReadStatus.NotFound, 0, 0);
        }

        if (afterSequence < state.FirstAvailableSequence - 1)
        {
            return new LiveReplayReadResult(
                LiveReplayReadStatus.Expired,
                state.FirstAvailableSequence,
                state.LastSequence);
        }

        if (afterSequence >= state.LastSequence)
        {
            return new LiveReplayReadResult(
                LiveReplayReadStatus.Current,
                state.FirstAvailableSequence,
                state.LastSequence);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT sequence, event_kind, content_type, payload, integrity_hash
            FROM {_replayEventsTable}
            WHERE identity_fingerprint = @identity AND sequence > @after
            ORDER BY sequence
            LIMIT @limit
            """;
        AddParameter(command, "identity", identity.Fingerprint);
        AddParameter(command, "after", afterSequence);
        AddParameter(command, "limit", maximumEvents);
        var events = new List<LiveReplayEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var sequence = reader.GetInt64(0);
            var kindValue = reader.GetInt32(1);
            if (!Enum.IsDefined(typeof(LiveEventKind), kindValue))
            {
                throw new PostgreSqlLiveStoreException(
                    $"Stored Live replay event {sequence} has unknown kind {kindValue}.");
            }

            LiveReplayEvent replayEvent;
            try
            {
                replayEvent = LiveReplayEvent.Restore(
                sequence,
                (LiveEventKind)kindValue,
                reader.GetString(2),
                (byte[])reader.GetValue(3),
                (byte[])reader.GetValue(4));
            }
            catch (ArgumentException exception)
            {
                throw new PostgreSqlLiveStoreException(
                    $"Stored Live replay event {sequence} failed its integrity check.",
                    exception);
            }

            events.Add(replayEvent);
        }

        return new LiveReplayReadResult(
            LiveReplayReadStatus.Available,
            state.FirstAvailableSequence,
            state.LastSequence,
            events);
    }

    public async ValueTask<int> PruneAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        var removed = await ExecuteAsync(
            connection,
            transaction,
            $"""
            WITH expired AS (
                SELECT identity_fingerprint, sequence
                FROM {_replayEventsTable}
                WHERE recorded_at < clock_timestamp() - (@retention_ms * interval '1 millisecond')
                ORDER BY recorded_at, identity_fingerprint, sequence
                LIMIT @batch)
            DELETE FROM {_replayEventsTable} e
            USING expired x
            WHERE e.identity_fingerprint = x.identity_fingerprint AND e.sequence = x.sequence
            """,
            cancellationToken,
            ("retention_ms", _options.ReplayRetentionWindow.TotalMilliseconds),
            ("batch", _options.ReplayPruneBatchSize)).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            $"""
            UPDATE {_replaySubscriptionsTable} s
            SET first_available_sequence = COALESCE(
                    (SELECT min(e.sequence) FROM {_replayEventsTable} e
                     WHERE e.identity_fingerprint = s.identity_fingerprint),
                    s.last_sequence + 1),
                updated_at = clock_timestamp()
            WHERE s.retain_until < clock_timestamp()
               OR NOT EXISTS (
                    SELECT 1 FROM {_replayEventsTable} e
                    WHERE e.identity_fingerprint = s.identity_fingerprint
                      AND e.sequence < s.first_available_sequence)
            """,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return removed;
    }

    private async Task InitializeCoreAsync()
    {
        await using var connection = await _dataSource.OpenConnectionAsync(CancellationToken.None)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            CancellationToken.None).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended(@schema, 0))",
            CancellationToken.None,
            ("schema", _options.ControlSchema + ":live")).ConfigureAwait(false);
        foreach (var statement in CreateSchemaStatements())
        {
            await ExecuteAsync(connection, transaction, statement, CancellationToken.None).ConfigureAwait(false);
        }

        await using var versionCommand = connection.CreateCommand();
        versionCommand.Transaction = transaction;
        versionCommand.CommandText = $"SELECT schema_version FROM {_metadataTable} WHERE singleton";
        var version = Convert.ToInt32(
            await versionCommand.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (version < 1 || version > CurrentSchemaVersion)
        {
            throw new PostgreSqlLiveStoreException(
                $"PostgreSQL Live schema version {version} is unsupported; this build requires {CurrentSchemaVersion}.");
        }

        if (version == 1)
        {
            await ExecuteAsync(
                connection,
                transaction,
                $"UPDATE {_metadataTable} SET schema_version = 2, updated_at = clock_timestamp() WHERE singleton",
                CancellationToken.None).ConfigureAwait(false);
        }

        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async ValueTask<LiveInvalidationCursor> InsertInvalidationAsync(
        DbConnection connection,
        DbTransaction transaction,
        string databaseIdentity,
        ChangeTransaction sourceTransaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {_invalidationsTable} (
                database_identity, source_fingerprint, commit_position, transaction_id, committed_at)
            VALUES (@database, @source, @position, @transaction_id, @committed_at)
            ON CONFLICT (database_identity, source_fingerprint, commit_position, transaction_id)
            DO UPDATE SET transaction_id = {_invalidationsTable}.transaction_id
            RETURNING cursor
            """;
        AddParameter(command, "database", databaseIdentity);
        AddParameter(command, "source", sourceTransaction.Source.Fingerprint);
        AddParameter(command, "position", (decimal)sourceTransaction.CommitEndPosition.Value);
        AddParameter(command, "transaction_id", (long)sourceTransaction.TransactionId);
        AddParameter(command, "committed_at", sourceTransaction.CommitTimestamp);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return new LiveInvalidationCursor(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
    }

    private string[] CreateSchemaStatements() =>
    [
        $"CREATE SCHEMA IF NOT EXISTS {_schema}",
        $"""
        CREATE TABLE IF NOT EXISTS {_metadataTable} (
            singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
            schema_version integer NOT NULL CHECK (schema_version > 0),
            updated_at timestamptz NOT NULL DEFAULT clock_timestamp())
        """,
        $"""
        INSERT INTO {_metadataTable} (singleton, schema_version)
        VALUES (true, {CurrentSchemaVersion})
        ON CONFLICT (singleton) DO NOTHING
        """,
        $"""
        CREATE TABLE IF NOT EXISTS {_invalidationsTable} (
            cursor bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            database_identity text NOT NULL,
            source_fingerprint char(64) NOT NULL,
            commit_position numeric(20, 0) NOT NULL CHECK (commit_position >= 0),
            transaction_id bigint NOT NULL CHECK (transaction_id >= 0),
            committed_at timestamptz NOT NULL,
            recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            UNIQUE (database_identity, source_fingerprint, commit_position, transaction_id))
        """,
        $"""
        CREATE TABLE IF NOT EXISTS {_dependenciesTable} (
            invalidation_cursor bigint NOT NULL REFERENCES {_invalidationsTable}(cursor) ON DELETE CASCADE,
            schema_name text NOT NULL,
            table_name text NOT NULL,
            PRIMARY KEY (invalidation_cursor, schema_name, table_name))
        """,
        $"CREATE INDEX IF NOT EXISTS live_invalidations_database_cursor_idx ON {_invalidationsTable} (database_identity, cursor)",
        $"CREATE INDEX IF NOT EXISTS live_invalidation_dependencies_table_idx ON {_dependenciesTable} (schema_name, table_name, invalidation_cursor)",
        $"""
        CREATE TABLE IF NOT EXISTS {_replaySubscriptionsTable} (
            identity_fingerprint char(64) PRIMARY KEY,
            first_available_sequence bigint NOT NULL DEFAULT 1 CHECK (first_available_sequence > 0),
            last_sequence bigint NOT NULL DEFAULT 0 CHECK (last_sequence >= 0),
            retain_until timestamptz NOT NULL,
            updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            CHECK (first_available_sequence <= last_sequence + 1))
        """,
        $"""
        CREATE TABLE IF NOT EXISTS {_replayEventsTable} (
            identity_fingerprint char(64) NOT NULL REFERENCES {_replaySubscriptionsTable}(identity_fingerprint) ON DELETE CASCADE,
            sequence bigint NOT NULL CHECK (sequence > 0),
            event_kind integer NOT NULL,
            content_type text NOT NULL,
            payload bytea NOT NULL,
            integrity_hash bytea NOT NULL CHECK (octet_length(integrity_hash) = 32),
            recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            PRIMARY KEY (identity_fingerprint, sequence))
        """,
        $"CREATE INDEX IF NOT EXISTS live_replay_events_retention_idx ON {_replayEventsTable} (recorded_at, identity_fingerprint, sequence)",
    ];

    private async ValueTask<ReplayState?> ReadReplayStateAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string identityFingerprint,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT first_available_sequence, last_sequence
            FROM {_replaySubscriptionsTable}
            WHERE identity_fingerprint = @identity
            {(forUpdate ? "FOR UPDATE" : string.Empty)}
            """;
        AddParameter(command, "identity", identityFingerprint);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ReplayState(reader.GetInt64(0), reader.GetInt64(1))
            : null;
    }

    private async ValueTask<bool> EventsMatchAsync(
        DbConnection connection,
        DbTransaction transaction,
        LiveReplayAppendRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT sequence, integrity_hash
            FROM {_replayEventsTable}
            WHERE identity_fingerprint = @identity
              AND sequence >= @first AND sequence <= @last
            ORDER BY sequence
            """;
        AddParameter(command, "identity", request.Identity.Fingerprint);
        AddParameter(command, "first", request.Events[0].Sequence);
        AddParameter(command, "last", request.Events[^1].Sequence);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var index = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (index >= request.Events.Count ||
                reader.GetInt64(0) != request.Events[index].Sequence ||
                !CryptographicOperations.FixedTimeEquals(
                    (byte[])reader.GetValue(1),
                    request.Events[index].IntegrityHash.Span))
            {
                return false;
            }

            index++;
        }

        return index == request.Events.Count;
    }

    private static async ValueTask<int> ExecuteAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string commandText,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        foreach (var (name, value) in parameters)
        {
            AddParameter(command, name, value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record ReplayState(long FirstAvailableSequence, long LastSequence);
}

public class PostgreSqlLiveStoreException : LiveQueryException
{
    public PostgreSqlLiveStoreException(string message)
        : base(message)
    {
    }

    public PostgreSqlLiveStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal static class LiveChangeDependencyExtractor
{
    private static readonly ConcurrentDictionary<Type, Func<Change, IReadOnlyList<ChangeTable>>> TypedExtractors = new();

    public static async ValueTask<IReadOnlyList<LiveTableDependency>> ExtractAsync(
        ChangeTransaction transaction,
        int maximumDependencies,
        CancellationToken cancellationToken)
    {
        var dependencies = new HashSet<LiveTableDependency>();
        await foreach (var change in transaction.Changes.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            foreach (var table in ExtractTables(change))
            {
                dependencies.Add(new LiveTableDependency(table.Schema, table.Name));
                if (dependencies.Count > maximumDependencies)
                {
                    throw new PostgreSqlLiveStoreException(
                        $"Transaction {transaction.TransactionId} exceeds the configured Live dependency limit of {maximumDependencies} tables.");
                }
            }
        }

        return dependencies.ToArray();
    }

    private static IReadOnlyList<ChangeTable> ExtractTables(Change change) =>
        change switch
        {
            InsertChange insert => [insert.NewRow.Table],
            UpdateChange update => [update.NewRow.Table],
            DeleteChange delete => [delete.OldRow.Table],
            TruncateChange truncate => truncate.Tables,
            LogicalMessageChange => [],
            _ => TypedExtractors.GetOrAdd(change.GetType(), CreateTypedExtractor)(change),
        };

    private static Func<Change, IReadOnlyList<ChangeTable>> CreateTypedExtractor(Type changeType)
    {
        if (!changeType.IsGenericType)
        {
            throw new PostgreSqlLiveStoreException(
                $"Change type '{changeType}' cannot be mapped to a Live table dependency.");
        }

        var definition = changeType.GetGenericTypeDefinition();
        var rowType = changeType.GetGenericArguments()[0];
        var methodName = definition == typeof(InsertChange<>)
            ? nameof(ExtractTypedInsert)
            : definition == typeof(UpdateChange<>)
                ? nameof(ExtractTypedUpdate)
                : definition == typeof(DeleteChange<>)
                    ? nameof(ExtractTypedDelete)
                    : definition == typeof(TruncateChange<>)
                        ? nameof(ExtractTypedTruncate)
                        : throw new PostgreSqlLiveStoreException(
                            $"Change type '{changeType}' cannot be mapped to a Live table dependency.");
        var method = typeof(LiveChangeDependencyExtractor).GetMethod(
            methodName,
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .MakeGenericMethod(rowType);
        return method.CreateDelegate<Func<Change, IReadOnlyList<ChangeTable>>>();
    }

    private static IReadOnlyList<ChangeTable> ExtractTypedInsert<T>(Change change) =>
        [((InsertChange<T>)change).NewRow.Columns.Table];

    private static IReadOnlyList<ChangeTable> ExtractTypedUpdate<T>(Change change) =>
        [((UpdateChange<T>)change).NewRow.Columns.Table];

    private static IReadOnlyList<ChangeTable> ExtractTypedDelete<T>(Change change) =>
        [((DeleteChange<T>)change).OldRow.Columns.Table];

    private static IReadOnlyList<ChangeTable> ExtractTypedTruncate<T>(Change change) =>
        ((TruncateChange<T>)change).Tables;
}
