using System.Data;
using System.Data.Common;
using BlueTusk.TypeSystem;

namespace BlueTusk.Streams.Storage.PostgreSql;

public enum ChangeRelayAppendStatus
{
    Appended,
    AlreadyPresent,
}

public sealed record ChangeRelaySourceRegistration(
    ChangeSourceIdentity Source,
    long SourceEpoch,
    long LastSequence,
    BlueTuskLogSequenceNumber LastCommitPosition);

public sealed record ChangeRelayAppendResult(
    ChangeRelayAppendStatus Status,
    long Sequence,
    int EnvelopeBytes);

public enum ChangeRelayConsumerGroupStart
{
    EarliestAvailable,
    Latest,
}

public sealed record ChangeRelayConsumerGroup(
    string SourceFingerprint,
    long SourceEpoch,
    string Name,
    long StartSequence,
    long CheckpointSequence,
    long StoreGeneration,
    bool IsActive);

public sealed record ChangeRelayGroupLease(
    string SourceFingerprint,
    long SourceEpoch,
    string ConsumerGroup,
    string OwnerId,
    long FencingToken,
    DateTimeOffset ExpiresAt);

public sealed record ChangeRelayRecord(
    long Sequence,
    DateTimeOffset AppendedAt,
    ChangeTransactionEnvelope Envelope,
    ChangeTransaction Transaction);

public sealed record ChangeRelayReadBatch(
    ChangeRelayConsumerGroup Group,
    IReadOnlyList<ChangeRelayRecord> Records,
    long TotalBytes);

public enum ChangeRelayAcknowledgeStatus
{
    Stored,
    Conflict,
    BackwardMovement,
    Fenced,
    UnknownPosition,
}

public sealed record ChangeRelayAcknowledgeResult(
    ChangeRelayAcknowledgeStatus Status,
    ChangeRelayConsumerGroup Current);

public sealed record ChangeRelayRetentionResult(
    long DeletedTransactions,
    long DeletedBytes);

public sealed record ChangeRelayMetrics(
    long TransactionCount,
    long StorageBytes,
    long EarliestSequence,
    long LatestSequence,
    long MinimumGroupCheckpoint,
    TimeSpan OldestUnacknowledgedAge);

public sealed record ChangeRelayHealth(
    ChangeRelayMetrics Metrics,
    long WalLagBytes,
    bool IsWalRetentionDanger,
    bool IsAcknowledgementOverdue,
    bool IsStorageExhausted);

public sealed class PostgreSqlDurableChangeRelay
{
    private const string SourceLeaseConsumerGroup = "__relay_source__";
    private readonly PostgreSqlStreamsStorageOptions _options;
    private readonly DbDataSource _dataSource;
    private readonly PostgreSqlChangeStreamStateStore _stateStore;
    private readonly string _schema;
    private readonly string _metadataTable;
    private readonly string _sourcesTable;
    private readonly string _transactionsTable;
    private readonly string _groupsTable;
    private readonly string _stateTable;
    private readonly ChangeTransactionEnvelopeOptions _envelopeOptions;

    public PostgreSqlDurableChangeRelay(PostgreSqlStreamsStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _dataSource = options.ControlDataSource;
        _stateStore = new PostgreSqlChangeStreamStateStore(options);
        _schema = options.QuotedControlSchema;
        _metadataTable = _schema + ".storage_metadata";
        _sourcesTable = _schema + ".relay_sources";
        _transactionsTable = _schema + ".relay_transactions";
        _groupsTable = _schema + ".relay_consumer_groups";
        _stateTable = _schema + ".stream_state";
        _envelopeOptions = new ChangeTransactionEnvelopeOptions
        {
            MaxEnvelopeBytes = options.MaxEnvelopeBytes,
        };
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        string[] statements =
        [
            $"""
            CREATE TABLE IF NOT EXISTS {_metadataTable} (
                singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
                schema_version integer NOT NULL,
                relay_bytes bigint NOT NULL DEFAULT 0 CHECK (relay_bytes >= 0),
                updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
            )
            """,
            $"""
            INSERT INTO {_metadataTable} (singleton, schema_version)
            VALUES (true, 1)
            ON CONFLICT (singleton) DO NOTHING
            """,
            $"""
            CREATE TABLE IF NOT EXISTS {_sourcesTable} (
                source_fingerprint text PRIMARY KEY,
                system_identifier text NOT NULL,
                database_name text NOT NULL,
                slot_name text NOT NULL,
                publication_fingerprint text NOT NULL,
                source_epoch bigint NOT NULL CHECK (source_epoch > 0),
                last_sequence bigint NOT NULL DEFAULT 0 CHECK (last_sequence >= 0),
                last_commit_position numeric(20, 0) NOT NULL DEFAULT 0,
                created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
            )
            """,
            $"""
            CREATE TABLE IF NOT EXISTS {_transactionsTable} (
                sequence bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                source_fingerprint text NOT NULL REFERENCES {_sourcesTable}(source_fingerprint),
                source_epoch bigint NOT NULL CHECK (source_epoch > 0),
                commit_position numeric(20, 0) NOT NULL,
                transaction_id bigint NOT NULL CHECK (transaction_id >= 0),
                envelope_format integer NOT NULL CHECK (envelope_format > 0),
                envelope bytea NOT NULL,
                appended_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                UNIQUE (source_fingerprint, source_epoch, commit_position, transaction_id)
            )
            """,
            $"""
            CREATE INDEX IF NOT EXISTS relay_transactions_source_sequence_idx
            ON {_transactionsTable} (source_fingerprint, source_epoch, sequence)
            """,
            $"""
            CREATE TABLE IF NOT EXISTS {_groupsTable} (
                source_fingerprint text NOT NULL REFERENCES {_sourcesTable}(source_fingerprint),
                source_epoch bigint NOT NULL,
                consumer_group text NOT NULL,
                start_sequence bigint NOT NULL CHECK (start_sequence >= 0),
                checkpoint_sequence bigint NOT NULL CHECK (checkpoint_sequence >= 0),
                store_generation bigint NOT NULL DEFAULT 0 CHECK (store_generation >= 0),
                lease_owner text NULL,
                lease_token bigint NULL CHECK (lease_token IS NULL OR lease_token > 0),
                lease_expires timestamptz NULL,
                last_fencing_token bigint NOT NULL DEFAULT 0 CHECK (last_fencing_token >= 0),
                active boolean NOT NULL DEFAULT true,
                created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                PRIMARY KEY (source_fingerprint, source_epoch, consumer_group)
            )
            """,
            $"""
            CREATE TABLE IF NOT EXISTS {_schema}.snapshot_runs (
                source_fingerprint text NOT NULL,
                source_epoch bigint NOT NULL,
                snapshot_epoch text NOT NULL,
                state text NOT NULL,
                progress bytea NULL,
                updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                PRIMARY KEY (source_fingerprint, source_epoch, snapshot_epoch)
            )
            """,
            $"""
            CREATE TABLE IF NOT EXISTS {_schema}.dead_letters (
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                source_fingerprint text NOT NULL,
                source_epoch bigint NOT NULL,
                consumer_group text NOT NULL,
                sequence bigint NULL,
                reason text NOT NULL,
                payload bytea NULL,
                created_at timestamptz NOT NULL DEFAULT clock_timestamp()
            )
            """,
            $"""
            CREATE TABLE IF NOT EXISTS {_schema}.retention_watermarks (
                source_fingerprint text NOT NULL,
                source_epoch bigint NOT NULL,
                retained_after_sequence bigint NOT NULL DEFAULT 0,
                updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                PRIMARY KEY (source_fingerprint, source_epoch)
            )
            """,
        ];

        foreach (var statement in statements)
        {
            await ExecuteAsync(connection, transaction: null, statement, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask<ChangeRelaySourceRegistration> RegisterSourceAsync(
        ChangeSourceIdentity source,
        bool beginNewEpoch = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        var current = await ReadSourceAsync(
            connection,
            transaction,
            source.Fingerprint,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            await using var insert = CreateCommand(
                connection,
                transaction,
                $"""
                INSERT INTO {_sourcesTable} (
                    source_fingerprint, system_identifier, database_name, slot_name,
                    publication_fingerprint, source_epoch)
                VALUES (@source, @system, @database, @slot, @publication, 1)
                """);
            AddParameter(insert, "source", source.Fingerprint);
            AddParameter(insert, "system", source.SystemIdentifier);
            AddParameter(insert, "database", source.DatabaseName);
            AddParameter(insert, "slot", source.SlotName);
            AddParameter(insert, "publication", source.PublicationFingerprint);
            _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            current = new ChangeRelaySourceRegistration(
                source,
                1,
                0,
                BlueTuskLogSequenceNumber.Zero);
        }
        else
        {
            EnsureSourceCompatible(source, current.Source);
            if (beginNewEpoch)
            {
                var epoch = checked(current.SourceEpoch + 1);
                await using var update = CreateCommand(
                    connection,
                    transaction,
                    $"""
                    UPDATE {_sourcesTable}
                    SET source_epoch = @epoch, last_sequence = 0,
                        last_commit_position = 0, updated_at = clock_timestamp()
                    WHERE source_fingerprint = @source
                    """);
                AddParameter(update, "epoch", epoch);
                AddParameter(update, "source", source.Fingerprint);
                _ = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                current = new ChangeRelaySourceRegistration(
                    source,
                    epoch,
                    0,
                    BlueTuskLogSequenceNumber.Zero);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return current;
    }

    public ValueTask<ChangeLeaseAcquireResult> AcquireSourceLeaseAsync(
        ChangeRelaySourceRegistration source,
        string ownerId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return _stateStore.AcquireAsync(
            ChangeStreamStateKey.Create(source.Source, SourceLeaseConsumerGroup),
            ownerId,
            duration,
            cancellationToken);
    }

    public ValueTask<ChangeStreamLease?> RenewSourceLeaseAsync(
        ChangeStreamLease lease,
        TimeSpan duration,
        CancellationToken cancellationToken = default) =>
        _stateStore.RenewAsync(lease, duration, cancellationToken);

    public ValueTask<bool> ReleaseSourceLeaseAsync(
        ChangeStreamLease lease,
        CancellationToken cancellationToken = default) =>
        _stateStore.ReleaseAsync(lease, cancellationToken);

    public async ValueTask<ChangeRelayAppendResult> AppendAsync(
        ChangeRelaySourceRegistration source,
        ChangeTransaction transactionToAppend,
        ChangeStreamLease sourceLease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(transactionToAppend);
        ArgumentNullException.ThrowIfNull(sourceLease);
        EnsureSourceCompatible(source.Source, transactionToAppend.Source);
        var envelope = await ChangeTransactionEnvelopeCodec.EncodeAsync(
            transactionToAppend,
            _envelopeOptions,
            cancellationToken).ConfigureAwait(false);
        var envelopeBytes = envelope.Data.Length;
        if (envelopeBytes > _options.MaxEnvelopeBytes)
        {
            throw new ChangeRelayStorageExhaustedException(
                $"A relay envelope of {envelopeBytes} bytes exceeds the {_options.MaxEnvelopeBytes}-byte transaction limit.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var databaseTransaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        var registered = await ReadSourceAsync(
            connection,
            databaseTransaction,
            source.Source.Fingerprint,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false) ??
            throw new ChangeRelaySourceMismatchException("The relay source is not registered.");
        if (registered.SourceEpoch != source.SourceEpoch)
        {
            throw new ChangeRelaySourceMismatchException(
                $"Relay source epoch {source.SourceEpoch} is no longer active; current epoch is {registered.SourceEpoch}.");
        }

        await EnsureSourceLeaseAsync(
            connection,
            databaseTransaction,
            source,
            sourceLease,
            cancellationToken).ConfigureAwait(false);
        var duplicate = await ReadDuplicateAsync(
            connection,
            databaseTransaction,
            source,
            transactionToAppend,
            cancellationToken).ConfigureAwait(false);
        if (duplicate is not null)
        {
            if (!duplicate.Value.Envelope.AsSpan().SequenceEqual(envelope.Data.Span))
            {
                throw new ChangeRelayIntegrityException(
                    "A relay transaction identity already exists with a different envelope.");
            }

            await databaseTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ChangeRelayAppendResult(
                ChangeRelayAppendStatus.AlreadyPresent,
                duplicate.Value.Sequence,
                envelopeBytes);
        }

        await ReserveStorageAsync(
            connection,
            databaseTransaction,
            envelopeBytes,
            cancellationToken).ConfigureAwait(false);
        long sequence;
        await using (var insert = CreateCommand(
                         connection,
                         databaseTransaction,
                         $"""
                         INSERT INTO {_transactionsTable} (
                             source_fingerprint, source_epoch, commit_position,
                             transaction_id, envelope_format, envelope)
                         VALUES (@source, @epoch, @position, @transaction_id, @format, @envelope)
                         RETURNING sequence
                         """))
        {
            AddParameter(insert, "source", source.Source.Fingerprint);
            AddParameter(insert, "epoch", source.SourceEpoch);
            AddParameter(insert, "position", (decimal)transactionToAppend.CommitEndPosition.Value);
            AddParameter(insert, "transaction_id", (long)transactionToAppend.TransactionId);
            AddParameter(insert, "format", envelope.FormatVersion);
            AddParameter(insert, "envelope", envelope.Data.ToArray());
            sequence = Convert.ToInt64(
                await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        await using (var update = CreateCommand(
                         connection,
                         databaseTransaction,
                         $"""
                         UPDATE {_sourcesTable}
                         SET last_sequence = @sequence,
                             last_commit_position = @position,
                             updated_at = clock_timestamp()
                         WHERE source_fingerprint = @source AND source_epoch = @epoch
                         """))
        {
            AddParameter(update, "sequence", sequence);
            AddParameter(update, "position", (decimal)transactionToAppend.CommitEndPosition.Value);
            AddParameter(update, "source", source.Source.Fingerprint);
            AddParameter(update, "epoch", source.SourceEpoch);
            _ = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await databaseTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ChangeRelayAppendResult(ChangeRelayAppendStatus.Appended, sequence, envelopeBytes);
    }

    public async ValueTask<ChangeRelayConsumerGroup> CreateConsumerGroupAsync(
        ChangeRelaySourceRegistration source,
        string consumerGroup,
        ChangeRelayConsumerGroupStart start = ChangeRelayConsumerGroupStart.EarliestAvailable,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroup);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        var existing = await ReadGroupAsync(
            connection,
            transaction,
            source.Source.Fingerprint,
            source.SourceEpoch,
            consumerGroup,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing.Group;
        }

        long startSequence;
        await using (var baseline = CreateCommand(
                         connection,
                         transaction,
                         start == ChangeRelayConsumerGroupStart.Latest
                             ? $"""
                               SELECT last_sequence FROM {_sourcesTable}
                               WHERE source_fingerprint = @source AND source_epoch = @epoch
                               """
                             : $"""
                               SELECT COALESCE(MIN(sequence) - 1, 0)
                               FROM {_transactionsTable}
                               WHERE source_fingerprint = @source AND source_epoch = @epoch
                               """))
        {
            AddParameter(baseline, "source", source.Source.Fingerprint);
            AddParameter(baseline, "epoch", source.SourceEpoch);
            var value = await baseline.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is null || value is DBNull)
            {
                throw new ChangeRelaySourceMismatchException("The active relay source epoch was not found.");
            }

            startSequence = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        await using (var insert = CreateCommand(
                         connection,
                         transaction,
                         $"""
                         INSERT INTO {_groupsTable} (
                             source_fingerprint, source_epoch, consumer_group,
                             start_sequence, checkpoint_sequence)
                         VALUES (@source, @epoch, @consumer, @start, @start)
                         """))
        {
            AddParameter(insert, "source", source.Source.Fingerprint);
            AddParameter(insert, "epoch", source.SourceEpoch);
            AddParameter(insert, "consumer", consumerGroup);
            AddParameter(insert, "start", startSequence);
            _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ChangeRelayConsumerGroup(
            source.Source.Fingerprint,
            source.SourceEpoch,
            consumerGroup,
            startSequence,
            startSequence,
            0,
            IsActive: true);
    }

    public async ValueTask<ChangeRelayGroupLease?> AcquireConsumerGroupAsync(
        ChangeRelayConsumerGroup group,
        string ownerId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        ValidateLeaseArguments(ownerId, duration);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        var state = await ReadGroupAsync(
            connection,
            transaction,
            group.SourceFingerprint,
            group.SourceEpoch,
            group.Name,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false) ??
            throw new ChangeRelayConsumerGroupException("The relay consumer group does not exist.");
        if (!state.Group.IsActive)
        {
            throw new ChangeRelayConsumerGroupException("The relay consumer group is inactive.");
        }

        if (state.IsLeaseActive &&
            state.Lease is not null &&
            !string.Equals(state.Lease.OwnerId, ownerId, StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var token = state.IsLeaseActive &&
                    state.Lease is not null &&
                    string.Equals(state.Lease.OwnerId, ownerId, StringComparison.Ordinal)
            ? state.Lease.FencingToken
            : checked(state.LastFencingToken + 1);
        await using var update = CreateCommand(
            connection,
            transaction,
            $"""
            UPDATE {_groupsTable}
            SET lease_owner = @owner, lease_token = @token,
                lease_expires = clock_timestamp() + (@duration_ms * interval '1 millisecond'),
                last_fencing_token = CASE WHEN @advance THEN @token ELSE last_fencing_token END,
                updated_at = clock_timestamp()
            WHERE source_fingerprint = @source AND source_epoch = @epoch
              AND consumer_group = @consumer
            RETURNING lease_expires
            """);
        AddGroupParameters(update, group.SourceFingerprint, group.SourceEpoch, group.Name);
        AddParameter(update, "owner", ownerId);
        AddParameter(update, "token", token);
        AddParameter(update, "duration_ms", duration.TotalMilliseconds);
        AddParameter(update, "advance", token > state.LastFencingToken);
        var expires = await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ??
            throw new ChangeRelayConsumerGroupException("The relay consumer group disappeared while locked.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ChangeRelayGroupLease(
            group.SourceFingerprint,
            group.SourceEpoch,
            group.Name,
            ownerId,
            token,
            ReadTimestamp(expires));
    }

    public async ValueTask<ChangeRelayGroupLease?> RenewConsumerGroupAsync(
        ChangeRelayGroupLease lease,
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
            UPDATE {_groupsTable}
            SET lease_expires = clock_timestamp() + (@duration_ms * interval '1 millisecond'),
                updated_at = clock_timestamp()
            WHERE source_fingerprint = @source AND source_epoch = @epoch
              AND consumer_group = @consumer AND active
              AND lease_owner = @owner AND lease_token = @token
              AND lease_expires > clock_timestamp()
            RETURNING lease_expires
            """);
        AddGroupParameters(command, lease.SourceFingerprint, lease.SourceEpoch, lease.ConsumerGroup);
        AddParameter(command, "owner", lease.OwnerId);
        AddParameter(command, "token", lease.FencingToken);
        AddParameter(command, "duration_ms", duration.TotalMilliseconds);
        var expires = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return expires is null or DBNull
            ? null
            : lease with { ExpiresAt = ReadTimestamp(expires) };
    }

    public async ValueTask<bool> ReleaseConsumerGroupAsync(
        ChangeRelayGroupLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = CreateCommand(
            connection,
            transaction: null,
            $"""
            UPDATE {_groupsTable}
            SET lease_owner = NULL, lease_token = NULL, lease_expires = NULL,
                updated_at = clock_timestamp()
            WHERE source_fingerprint = @source AND source_epoch = @epoch
              AND consumer_group = @consumer
              AND lease_owner = @owner AND lease_token = @token
            """);
        AddGroupParameters(command, lease.SourceFingerprint, lease.SourceEpoch, lease.ConsumerGroup);
        AddParameter(command, "owner", lease.OwnerId);
        AddParameter(command, "token", lease.FencingToken);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async ValueTask<ChangeRelayReadBatch> ReadConsumerGroupAsync(
        ChangeRelayGroupLease lease,
        int maxTransactions,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTransactions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var groupState = await ReadGroupAsync(
            connection,
            transaction: null,
            lease.SourceFingerprint,
            lease.SourceEpoch,
            lease.ConsumerGroup,
            forUpdate: false,
            cancellationToken).ConfigureAwait(false) ??
            throw new ChangeRelayConsumerGroupException("The relay consumer group does not exist.");
        if (!groupState.Group.IsActive ||
            !groupState.IsLeaseActive ||
            !GroupLeaseMatches(groupState.Lease, lease))
        {
            throw new ChangeRelayLeaseLostException("The relay consumer-group lease was lost.");
        }

        await using var command = CreateCommand(
            connection,
            transaction: null,
            $"""
            WITH candidates AS (
                SELECT sequence, appended_at, envelope,
                       row_number() OVER (ORDER BY sequence) AS row_number,
                       sum(octet_length(envelope)) OVER (ORDER BY sequence) AS running_bytes
                FROM (
                    SELECT sequence, appended_at, envelope
                    FROM {_transactionsTable}
                    WHERE source_fingerprint = @source AND source_epoch = @epoch
                      AND sequence > @checkpoint
                    ORDER BY sequence
                    LIMIT @maximum_transactions
                ) AS limited
            )
            SELECT sequence, appended_at, envelope
            FROM candidates
            WHERE running_bytes <= @maximum_bytes OR row_number = 1
            ORDER BY sequence
            """);
        AddParameter(command, "source", lease.SourceFingerprint);
        AddParameter(command, "epoch", lease.SourceEpoch);
        AddParameter(command, "checkpoint", groupState.Group.CheckpointSequence);
        AddParameter(command, "maximum_transactions", maxTransactions);
        AddParameter(command, "maximum_bytes", maxBytes);
        var records = new List<ChangeRelayRecord>();
        long totalBytes = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var bytes = reader.GetFieldValue<byte[]>(2);
            var envelope = ChangeTransactionEnvelopeCodec.FromData(bytes, _envelopeOptions);
            var transaction = ChangeTransactionEnvelopeCodec.Decode(envelope, _envelopeOptions);
            records.Add(new ChangeRelayRecord(
                reader.GetInt64(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                envelope,
                transaction));
            totalBytes = checked(totalBytes + bytes.Length);
        }

        return new ChangeRelayReadBatch(groupState.Group, records.AsReadOnly(), totalBytes);
    }

    public async ValueTask<ChangeRelayAcknowledgeResult> AcknowledgeConsumerGroupAsync(
        ChangeRelayGroupLease lease,
        long expectedGeneration,
        long sequence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        var state = await ReadGroupAsync(
            connection,
            transaction,
            lease.SourceFingerprint,
            lease.SourceEpoch,
            lease.ConsumerGroup,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false) ??
            throw new ChangeRelayConsumerGroupException("The relay consumer group does not exist.");
        ChangeRelayAcknowledgeStatus? failure = null;
        if (!state.Group.IsActive || !state.IsLeaseActive || !GroupLeaseMatches(state.Lease, lease))
        {
            failure = ChangeRelayAcknowledgeStatus.Fenced;
        }
        else if (state.Group.StoreGeneration != expectedGeneration)
        {
            failure = ChangeRelayAcknowledgeStatus.Conflict;
        }
        else if (sequence < state.Group.CheckpointSequence)
        {
            failure = ChangeRelayAcknowledgeStatus.BackwardMovement;
        }
        else if (!await IsKnownSequenceAsync(
                     connection,
                     transaction,
                     lease.SourceFingerprint,
                     lease.SourceEpoch,
                     sequence,
                     state.Group.CheckpointSequence,
                     cancellationToken).ConfigureAwait(false))
        {
            failure = ChangeRelayAcknowledgeStatus.UnknownPosition;
        }

        if (failure.HasValue)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ChangeRelayAcknowledgeResult(failure.Value, state.Group);
        }

        var updated = state.Group with
        {
            CheckpointSequence = sequence,
            StoreGeneration = checked(expectedGeneration + 1),
        };
        await using (var command = CreateCommand(
                         connection,
                         transaction,
                         $"""
                         UPDATE {_groupsTable}
                         SET checkpoint_sequence = @sequence, store_generation = @generation,
                             updated_at = clock_timestamp()
                         WHERE source_fingerprint = @source AND source_epoch = @epoch
                           AND consumer_group = @consumer
                         """))
        {
            AddGroupParameters(command, lease.SourceFingerprint, lease.SourceEpoch, lease.ConsumerGroup);
            AddParameter(command, "sequence", sequence);
            AddParameter(command, "generation", updated.StoreGeneration);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ChangeRelayAcknowledgeResult(ChangeRelayAcknowledgeStatus.Stored, updated);
    }

    public async ValueTask<ChangeRelayRetentionResult> ApplyRetentionAsync(
        ChangeRelaySourceRegistration source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(
            connection,
            transaction,
            $"""
            DELETE FROM {_transactionsTable} AS candidate
            WHERE candidate.source_fingerprint = @source
              AND candidate.source_epoch = @epoch
              AND candidate.appended_at <=
                  clock_timestamp() - (@retention_ms * interval '1 millisecond')
              AND NOT EXISTS (
                  SELECT 1 FROM {_groupsTable} AS consumer
                  WHERE consumer.source_fingerprint = candidate.source_fingerprint
                    AND consumer.source_epoch = candidate.source_epoch
                    AND consumer.active
                    AND consumer.start_sequence < candidate.sequence
                    AND consumer.checkpoint_sequence < candidate.sequence)
            RETURNING sequence, octet_length(envelope)
            """);
        AddParameter(command, "source", source.Source.Fingerprint);
        AddParameter(command, "epoch", source.SourceEpoch);
        AddParameter(command, "retention_ms", _options.ResumeRetentionWindow.TotalMilliseconds);
        long deletedCount = 0;
        long deletedBytes = 0;
        long watermark = 0;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                watermark = Math.Max(watermark, reader.GetInt64(0));
                deletedBytes = checked(deletedBytes + reader.GetInt32(1));
                deletedCount++;
            }
        }

        if (deletedBytes > 0)
        {
            await using var metadata = CreateCommand(
                connection,
                transaction,
                $"""
                UPDATE {_metadataTable}
                SET relay_bytes = GREATEST(relay_bytes - @bytes, 0),
                    updated_at = clock_timestamp()
                WHERE singleton
                """);
            AddParameter(metadata, "bytes", deletedBytes);
            _ = await metadata.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var retention = CreateCommand(
                connection,
                transaction,
                $"""
                INSERT INTO {_schema}.retention_watermarks (
                    source_fingerprint, source_epoch, retained_after_sequence)
                VALUES (@source, @epoch, @watermark)
                ON CONFLICT (source_fingerprint, source_epoch) DO UPDATE
                SET retained_after_sequence = GREATEST(
                        {_schema}.retention_watermarks.retained_after_sequence,
                        EXCLUDED.retained_after_sequence),
                    updated_at = clock_timestamp()
                """);
            AddParameter(retention, "source", source.Source.Fingerprint);
            AddParameter(retention, "epoch", source.SourceEpoch);
            AddParameter(retention, "watermark", watermark);
            _ = await retention.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ChangeRelayRetentionResult(deletedCount, deletedBytes);
    }

    public async ValueTask<ChangeRelayMetrics> GetMetricsAsync(
        ChangeRelaySourceRegistration source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = CreateCommand(
            connection,
            transaction: null,
            $"""
            SELECT COUNT(candidate.sequence),
                   COALESCE(SUM(octet_length(candidate.envelope)), 0),
                   COALESCE(MIN(candidate.sequence), 0),
                   COALESCE(MAX(candidate.sequence), 0),
                   COALESCE((
                       SELECT MIN(consumer.checkpoint_sequence)
                       FROM {_groupsTable} AS consumer
                       WHERE consumer.source_fingerprint = @source
                         AND consumer.source_epoch = @epoch AND consumer.active), 0),
                   COALESCE(EXTRACT(EPOCH FROM (
                       clock_timestamp() - MIN(candidate.appended_at) FILTER (
                           WHERE EXISTS (
                               SELECT 1 FROM {_groupsTable} AS pending_consumer
                               WHERE pending_consumer.source_fingerprint = candidate.source_fingerprint
                                 AND pending_consumer.source_epoch = candidate.source_epoch
                                 AND pending_consumer.active
                                 AND pending_consumer.start_sequence < candidate.sequence
                                 AND pending_consumer.checkpoint_sequence < candidate.sequence))))::double precision, 0)
            FROM {_transactionsTable} AS candidate
            WHERE candidate.source_fingerprint = @source AND candidate.source_epoch = @epoch
            """);
        AddParameter(command, "source", source.Source.Fingerprint);
        AddParameter(command, "epoch", source.SourceEpoch);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new ChangeRelayMetrics(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            TimeSpan.FromSeconds(reader.GetDouble(5)));
    }

    public async ValueTask<ChangeRelayHealth> GetHealthAsync(
        ChangeRelaySourceRegistration source,
        BlueTuskLogSequenceNumber serverWalEnd,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var metrics = await GetMetricsAsync(source, cancellationToken).ConfigureAwait(false);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var current = await ReadSourceAsync(
            connection,
            transaction: null,
            source.Source.Fingerprint,
            forUpdate: false,
            cancellationToken).ConfigureAwait(false) ??
            throw new ChangeRelaySourceMismatchException("The relay source is not registered.");
        var unsignedLag = serverWalEnd.Value > current.LastCommitPosition.Value
            ? serverWalEnd.Value - current.LastCommitPosition.Value
            : 0;
        var walLagBytes = unsignedLag > long.MaxValue ? long.MaxValue : (long)unsignedLag;
        return new ChangeRelayHealth(
            metrics,
            walLagBytes,
            walLagBytes >= _options.MaxWalLagBytes,
            metrics.OldestUnacknowledgedAge >= _options.MaxAcknowledgementAge,
            metrics.StorageBytes >= _options.MaxRelayStorageBytes);
    }

    private async ValueTask<ChangeRelaySourceRegistration?> ReadSourceAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sourceFingerprint,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            $"""
            SELECT system_identifier, database_name, slot_name, publication_fingerprint,
                   source_epoch, last_sequence, last_commit_position
            FROM {_sourcesTable}
            WHERE source_fingerprint = @source
            {(forUpdate ? "FOR UPDATE" : string.Empty)}
            """);
        AddParameter(command, "source", sourceFingerprint);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ChangeRelaySourceRegistration(
            new ChangeSourceIdentity(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)),
            reader.GetInt64(4),
            reader.GetInt64(5),
            new BlueTuskLogSequenceNumber(checked((ulong)reader.GetDecimal(6))));
    }

    private async ValueTask<GroupState?> ReadGroupAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sourceFingerprint,
        long sourceEpoch,
        string consumerGroup,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            $"""
            SELECT start_sequence, checkpoint_sequence, store_generation, active,
                   lease_owner, lease_token, lease_expires,
                   COALESCE(lease_expires > clock_timestamp(), false),
                   last_fencing_token
            FROM {_groupsTable}
            WHERE source_fingerprint = @source AND source_epoch = @epoch
              AND consumer_group = @consumer
            {(forUpdate ? "FOR UPDATE" : string.Empty)}
            """);
        AddGroupParameters(command, sourceFingerprint, sourceEpoch, consumerGroup);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var group = new ChangeRelayConsumerGroup(
            sourceFingerprint,
            sourceEpoch,
            consumerGroup,
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetBoolean(3));
        ChangeRelayGroupLease? lease = null;
        if (!reader.IsDBNull(4))
        {
            lease = new ChangeRelayGroupLease(
                sourceFingerprint,
                sourceEpoch,
                consumerGroup,
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetFieldValue<DateTimeOffset>(6));
        }

        return new GroupState(group, lease, reader.GetBoolean(7), reader.GetInt64(8));
    }

    private async ValueTask EnsureSourceLeaseAsync(
        DbConnection connection,
        DbTransaction transaction,
        ChangeRelaySourceRegistration source,
        ChangeStreamLease lease,
        CancellationToken cancellationToken)
    {
        var key = ChangeStreamStateKey.Create(source.Source, SourceLeaseConsumerGroup);
        if (lease.Key != key)
        {
            throw new ChangeRelayLeaseLostException("The lease belongs to a different relay source.");
        }

        await using var command = CreateCommand(
            connection,
            transaction,
            $"""
            SELECT lease_token
            FROM {_stateTable}
            WHERE source_fingerprint = @source AND consumer_group = @consumer
              AND lease_owner = @owner AND lease_token = @token
              AND lease_expires > clock_timestamp()
            FOR UPDATE
            """);
        AddParameter(command, "source", key.SourceFingerprint);
        AddParameter(command, "consumer", key.ConsumerGroup);
        AddParameter(command, "owner", lease.OwnerId);
        AddParameter(command, "token", lease.FencingToken);
        var activeToken = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (activeToken is null or DBNull ||
            Convert.ToInt64(activeToken, System.Globalization.CultureInfo.InvariantCulture) != lease.FencingToken)
        {
            throw new ChangeRelayLeaseLostException("The relay source lease was lost.");
        }
    }

    private async ValueTask<(long Sequence, byte[] Envelope)?> ReadDuplicateAsync(
        DbConnection connection,
        DbTransaction transaction,
        ChangeRelaySourceRegistration source,
        ChangeTransaction changeTransaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            $"""
            SELECT sequence, envelope
            FROM {_transactionsTable}
            WHERE source_fingerprint = @source AND source_epoch = @epoch
              AND commit_position = @position AND transaction_id = @transaction_id
            """);
        AddParameter(command, "source", source.Source.Fingerprint);
        AddParameter(command, "epoch", source.SourceEpoch);
        AddParameter(command, "position", (decimal)changeTransaction.CommitEndPosition.Value);
        AddParameter(command, "transaction_id", (long)changeTransaction.TransactionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetInt64(0), reader.GetFieldValue<byte[]>(1))
            : null;
    }

    private async ValueTask ReserveStorageAsync(
        DbConnection connection,
        DbTransaction transaction,
        int envelopeBytes,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            $"""
            UPDATE {_metadataTable}
            SET relay_bytes = relay_bytes + @bytes, updated_at = clock_timestamp()
            WHERE singleton AND relay_bytes <= @maximum - @bytes
            RETURNING relay_bytes
            """);
        AddParameter(command, "bytes", envelopeBytes);
        AddParameter(command, "maximum", _options.MaxRelayStorageBytes);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            throw new ChangeRelayStorageExhaustedException(
                $"The relay storage limit of {_options.MaxRelayStorageBytes} bytes would be exceeded.");
        }
    }

    private async ValueTask<bool> IsKnownSequenceAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sourceFingerprint,
        long sourceEpoch,
        long sequence,
        long currentSequence,
        CancellationToken cancellationToken)
    {
        if (sequence == currentSequence)
        {
            return true;
        }

        await using var command = CreateCommand(
            connection,
            transaction,
            $"""
            SELECT COUNT(*) FROM {_transactionsTable}
            WHERE source_fingerprint = @source AND source_epoch = @epoch
              AND sequence = @sequence
            """);
        AddParameter(command, "source", sourceFingerprint);
        AddParameter(command, "epoch", sourceEpoch);
        AddParameter(command, "sequence", sequence);
        return Convert.ToInt64(
                   await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                   System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static bool GroupLeaseMatches(
        ChangeRelayGroupLease? current,
        ChangeRelayGroupLease candidate) =>
        current is not null &&
        current.SourceFingerprint == candidate.SourceFingerprint &&
        current.SourceEpoch == candidate.SourceEpoch &&
        string.Equals(current.ConsumerGroup, candidate.ConsumerGroup, StringComparison.Ordinal) &&
        string.Equals(current.OwnerId, candidate.OwnerId, StringComparison.Ordinal) &&
        current.FencingToken == candidate.FencingToken;

    private static void EnsureSourceCompatible(
        ChangeSourceIdentity expected,
        ChangeSourceIdentity actual)
    {
        if (!Equals(expected, actual))
        {
            throw new ChangeRelaySourceMismatchException(
                "The relay source identity does not match the registered source.");
        }
    }

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

    private static void AddGroupParameters(
        DbCommand command,
        string sourceFingerprint,
        long sourceEpoch,
        string consumerGroup)
    {
        AddParameter(command, "source", sourceFingerprint);
        AddParameter(command, "epoch", sourceEpoch);
        AddParameter(command, "consumer", consumerGroup);
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

    private sealed record GroupState(
        ChangeRelayConsumerGroup Group,
        ChangeRelayGroupLease? Lease,
        bool IsLeaseActive,
        long LastFencingToken);
}

public sealed class PostgreSqlRelayChangeDeliveryObserver : IChangeDeliveryObserver, IAsyncDisposable
{
    private readonly PostgreSqlDurableChangeRelay _relay;
    private readonly IReplicationFeedbackSender _feedback;
    private readonly TimeSpan _leaseDuration;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ChangeStreamLease _lease;
    private int _disposed;

    private PostgreSqlRelayChangeDeliveryObserver(
        PostgreSqlDurableChangeRelay relay,
        ChangeRelaySourceRegistration source,
        ChangeStreamLease lease,
        TimeSpan leaseDuration,
        IReplicationFeedbackSender feedback)
    {
        _relay = relay;
        Source = source;
        _lease = lease;
        _leaseDuration = leaseDuration;
        _feedback = feedback;
    }

    public ChangeRelaySourceRegistration Source { get; }

    public ChangeStreamLease Lease => _lease;

    public static async ValueTask<PostgreSqlRelayChangeDeliveryObserver> AcquireAsync(
        PostgreSqlDurableChangeRelay relay,
        ChangeSourceIdentity source,
        string ownerId,
        TimeSpan leaseDuration,
        IReplicationFeedbackSender feedback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(feedback);
        var registration = await relay.RegisterSourceAsync(source, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var acquired = await relay.AcquireSourceLeaseAsync(
            registration,
            ownerId,
            leaseDuration,
            cancellationToken).ConfigureAwait(false);
        if (acquired.Status != ChangeLeaseAcquireStatus.Acquired || acquired.Lease is null)
        {
            throw new ChangeRelayLeaseUnavailableException(
                $"Relay source slot '{source.SlotName}' is already owned by '{acquired.Lease?.OwnerId}'.");
        }

        return new PostgreSqlRelayChangeDeliveryObserver(
            relay,
            registration,
            acquired.Lease,
            leaseDuration,
            feedback);
    }

    public async ValueTask AcknowledgeAsync(
        ChangeTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _lease = await _relay.RenewSourceLeaseAsync(
                    _lease,
                    _leaseDuration,
                    cancellationToken)
                .ConfigureAwait(false) ?? throw new ChangeRelayLeaseLostException(
                    "The relay source lease was lost before append.");
            _ = await _relay.AppendAsync(Source, transaction, _lease, cancellationToken)
                .ConfigureAwait(false);
            await _feedback.SendFeedbackAsync(transaction.CommitEndPosition, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask NackAsync(
        ChangeTransaction transaction,
        Exception? failure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _ = await _relay.ReleaseSourceLeaseAsync(_lease).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}

public class ChangeRelayException : Exception
{
    public ChangeRelayException(string message)
        : base(message)
    {
    }
}

public sealed class ChangeRelaySourceMismatchException : ChangeRelayException
{
    public ChangeRelaySourceMismatchException(string message)
        : base(message)
    {
    }
}

public sealed class ChangeRelayIntegrityException : ChangeRelayException
{
    public ChangeRelayIntegrityException(string message)
        : base(message)
    {
    }
}

public sealed class ChangeRelayStorageExhaustedException : ChangeRelayException
{
    public ChangeRelayStorageExhaustedException(string message)
        : base(message)
    {
    }
}

public sealed class ChangeRelayConsumerGroupException : ChangeRelayException
{
    public ChangeRelayConsumerGroupException(string message)
        : base(message)
    {
    }
}

public sealed class ChangeRelayLeaseUnavailableException : ChangeRelayException
{
    public ChangeRelayLeaseUnavailableException(string message)
        : base(message)
    {
    }
}

public sealed class ChangeRelayLeaseLostException : ChangeRelayException
{
    public ChangeRelayLeaseLostException(string message)
        : base(message)
    {
    }
}
