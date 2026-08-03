using System.Data;
using System.Data.Common;
using BlueTusk.Streams;
using BlueTusk.TypeSystem;

namespace BlueTusk.Sync.PostgreSql;

public sealed class PostgreSqlSyncDestination : ISyncDestination, ISyncQuarantineSink
{
    public const int CurrentSchemaVersion = 1;

    private readonly PostgreSqlSyncOptions _options;
    private readonly DbDataSource _dataSource;
    private readonly string _schema;
    private readonly string _metadataTable;
    private readonly string _pipelinesTable;
    private readonly string _documentsTable;
    private readonly string _quarantineTable;
    private readonly IPostgreSqlSyncMutationWriter _writer;
    private readonly object _initializeLock = new();
    private Task? _initializationTask;

    public PostgreSqlSyncDestination(PostgreSqlSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _dataSource = options.DestinationDataSource;
        _schema = options.QuotedControlSchema;
        _metadataTable = _schema + ".storage_metadata";
        _pipelinesTable = _schema + ".pipelines";
        _documentsTable = _schema + ".documents";
        _quarantineTable = _schema + ".quarantine";
        _writer = options.MutationWriter ?? new PostgreSqlDocumentMutationWriter(options.ControlSchema);
    }

    public string Name => "PostgreSQL";

    public SyncDestinationCapabilities Capabilities =>
        SyncDestinationCapabilities.TransactionalBatches |
        SyncDestinationCapabilities.IdempotentUpserts |
        SyncDestinationCapabilities.Deletes |
        SyncDestinationCapabilities.CoLocatedCheckpoint |
        SyncDestinationCapabilities.Reconciliation;

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
            ("schema", _options.ControlSchema)).ConfigureAwait(false);
        foreach (var statement in CreateSchemaStatements())
        {
            await ExecuteAsync(connection, transaction, statement, CancellationToken.None).ConfigureAwait(false);
        }

        var version = await ReadSchemaVersionAsync(connection, transaction, CancellationToken.None)
            .ConfigureAwait(false);
        if (version != CurrentSchemaVersion)
        {
            throw new PostgreSqlSyncException(
                $"PostgreSQL Sync schema version {version} is unsupported; this build requires version {CurrentSchemaVersion}.");
        }

        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public async ValueTask<SyncProvisionResult> ProvisionAsync(
        SyncProvisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        var existing = await ReadPipelineAsync(
            connection,
            transaction,
            request.PipelineId,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await ExecuteAsync(
                connection,
                transaction,
                $"""
                INSERT INTO {_pipelinesTable} (
                    pipeline_id, source_fingerprint, transform_name, transform_fingerprint)
                VALUES (@pipeline, @source, @transform_name, @transform)
                """,
                cancellationToken,
                ("pipeline", request.PipelineId),
                ("source", request.Source.Fingerprint),
                ("transform_name", request.Transform.Name),
                ("transform", request.Transform.Fingerprint)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SyncProvisionResult(SyncProvisionStatus.Ready);
        }

        EnsureSource(request.Source.Fingerprint, existing.SourceFingerprint, request.PipelineId);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return string.Equals(
            request.Transform.Fingerprint,
            existing.TransformFingerprint,
            StringComparison.Ordinal)
            ? new SyncProvisionResult(SyncProvisionStatus.Ready)
            : new SyncProvisionResult(
                SyncProvisionStatus.RebuildRequired,
                existing.TransformFingerprint);
    }

    public async ValueTask ResetSnapshotAsync(
        string pipelineId,
        SnapshotReset reset,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        ArgumentNullException.ThrowIfNull(reset);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        _ = await RequirePipelineAsync(connection, transaction, pipelineId, cancellationToken)
            .ConfigureAwait(false);
        await _writer.ResetSnapshotAsync(
            connection,
            transaction,
            pipelineId,
            reset,
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            $"""
            UPDATE {_pipelinesTable}
            SET snapshot_epoch = @epoch,
                snapshot_complete = false,
                checkpoint_position = NULL,
                checkpoint_transaction_id = NULL,
                store_generation = store_generation + 1,
                updated_at = clock_timestamp()
            WHERE pipeline_id = @pipeline
            """,
            cancellationToken,
            ("epoch", reset.Epoch.Value),
            ("pipeline", pipelineId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask StartSnapshotAsync(
        string pipelineId,
        SnapshotStart start,
        SyncTransformVersion transform,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(transform);
        await ValidateSnapshotAsync(
            pipelineId,
            start.Epoch.Value,
            transform,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ApplySnapshotBatchAsync(
        SyncSnapshotBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ValidateContentLimits(batch.Mutations.Select(mutation => mutation.Content));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        var state = await RequirePipelineAsync(
            connection,
            transaction,
            batch.PipelineId,
            cancellationToken).ConfigureAwait(false);
        EnsureTransform(batch.Transform, state);
        EnsureSnapshot(batch.SourceBatch.Epoch.Value, state);
        await _writer.ApplySnapshotBatchAsync(
            connection,
            transaction,
            batch,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CompleteSnapshotAsync(
        string pipelineId,
        SnapshotComplete complete,
        SyncTransformVersion transform,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(complete);
        ArgumentNullException.ThrowIfNull(transform);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        var state = await RequirePipelineAsync(connection, transaction, pipelineId, cancellationToken)
            .ConfigureAwait(false);
        EnsureTransform(transform, state);
        EnsureSnapshot(complete.Epoch.Value, state);
        await ExecuteAsync(
            connection,
            transaction,
            $"""
            UPDATE {_pipelinesTable}
            SET snapshot_complete = true,
                updated_at = clock_timestamp()
            WHERE pipeline_id = @pipeline
            """,
            cancellationToken,
            ("pipeline", pipelineId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SyncApplyResult> ApplyTransactionAsync(
        SyncTransactionBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ValidateContentLimits(batch.Mutations.Select(mutation => mutation.Content));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        var state = await RequirePipelineAsync(
            connection,
            transaction,
            batch.PipelineId,
            cancellationToken).ConfigureAwait(false);
        EnsureSource(batch.Transaction.Source.Fingerprint, state.SourceFingerprint, batch.PipelineId);
        if (!string.Equals(
                batch.Transform.Fingerprint,
                state.TransformFingerprint,
                StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SyncApplyResult(
                SyncApplyStatus.TransformVersionMismatch,
                state.CheckpointPosition,
                state.TransformFingerprint);
        }

        if (state.CheckpointPosition is not null &&
            state.CheckpointPosition.Value >= batch.Transaction.CommitEndPosition)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return SyncApplyResult.AlreadyApplied(batch.Transaction.CommitEndPosition);
        }

        await _writer.ApplyTransactionAsync(
            connection,
            transaction,
            batch,
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            $"""
            UPDATE {_pipelinesTable}
            SET checkpoint_position = @position,
                checkpoint_transaction_id = @transaction_id,
                store_generation = store_generation + 1,
                updated_at = clock_timestamp()
            WHERE pipeline_id = @pipeline
            """,
            cancellationToken,
            ("position", (decimal)batch.Transaction.CommitEndPosition.Value),
            ("transaction_id", (long)batch.Transaction.TransactionId),
            ("pipeline", batch.PipelineId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return SyncApplyResult.Applied(batch.Transaction.CommitEndPosition);
    }

    public async ValueTask<bool> StoreAsync(
        SyncQuarantineRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await ExecuteAsync(
            connection,
            transaction: null,
            $"""
            INSERT INTO {_quarantineTable} (
                pipeline_id, source_fingerprint, transaction_id, commit_position,
                transform_fingerprint, error_type, error_message, recorded_at)
            VALUES (
                @pipeline, @source, @transaction_id, @position,
                @transform, @error_type, @error_message, @recorded_at)
            ON CONFLICT (pipeline_id, source_fingerprint, commit_position, transaction_id)
            DO NOTHING
            """,
            cancellationToken,
            ("pipeline", record.PipelineId),
            ("source", record.Source.Fingerprint),
            ("transaction_id", (long)record.TransactionId),
            ("position", (decimal)record.CommitEndPosition.Value),
            ("transform", record.Transform.Fingerprint),
            ("error_type", record.ErrorType),
            ("error_message", record.ErrorMessage),
            ("recorded_at", record.RecordedAt)).ConfigureAwait(false);
        return rows is 0 or 1;
    }

    private async ValueTask ValidateSnapshotAsync(
        string pipelineId,
        Guid epoch,
        SyncTransformVersion transform,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        var state = await RequirePipelineAsync(connection, transaction, pipelineId, cancellationToken)
            .ConfigureAwait(false);
        EnsureTransform(transform, state);
        EnsureSnapshot(epoch, state);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<PipelineState> RequirePipelineAsync(
        DbConnection connection,
        DbTransaction transaction,
        string pipelineId,
        CancellationToken cancellationToken) =>
        await ReadPipelineAsync(connection, transaction, pipelineId, true, cancellationToken)
            .ConfigureAwait(false) ??
        throw new PostgreSqlSyncException($"Sync pipeline '{pipelineId}' is not provisioned.");

    private async ValueTask<PipelineState?> ReadPipelineAsync(
        DbConnection connection,
        DbTransaction transaction,
        string pipelineId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT source_fingerprint, transform_fingerprint, checkpoint_position,
                   snapshot_epoch, snapshot_complete
            FROM {_pipelinesTable}
            WHERE pipeline_id = @pipeline
            {(forUpdate ? "FOR UPDATE" : string.Empty)}
            """;
        AddParameter(command, "pipeline", pipelineId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new PipelineState(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2)
                ? null
                : new BlueTuskLogSequenceNumber(checked((ulong)reader.GetDecimal(2))),
            reader.IsDBNull(3) ? null : reader.GetGuid(3),
            reader.GetBoolean(4));
    }

    private void ValidateContentLimits(IEnumerable<ReadOnlyMemory<byte>> contents)
    {
        long total = 0;
        foreach (var content in contents)
        {
            if (content.Length > _options.MaxDocumentBytes)
            {
                throw new PostgreSqlSyncException(
                    $"A {content.Length}-byte document exceeds the {_options.MaxDocumentBytes}-byte limit.");
            }

            total = checked(total + content.Length);
            if (total > _options.MaxTransactionBytes)
            {
                throw new PostgreSqlSyncException(
                    $"The transformed transaction exceeds the {_options.MaxTransactionBytes}-byte limit.");
            }
        }
    }

    private static void EnsureTransform(SyncTransformVersion transform, PipelineState state)
    {
        if (!string.Equals(transform.Fingerprint, state.TransformFingerprint, StringComparison.Ordinal))
        {
            throw new SyncTransformVersionMismatchException(
                state.TransformFingerprint,
                transform.Fingerprint);
        }
    }

    private static void EnsureSnapshot(Guid epoch, PipelineState state)
    {
        if (state.SnapshotEpoch != epoch || state.SnapshotComplete)
        {
            throw new PostgreSqlSyncSnapshotException(
                $"Snapshot epoch '{epoch}' is not the active incomplete destination epoch.");
        }
    }

    private static void EnsureSource(string requested, string existing, string pipelineId)
    {
        if (!string.Equals(requested, existing, StringComparison.Ordinal))
        {
            throw new PostgreSqlSyncSourceMismatchException(
                $"Pipeline '{pipelineId}' belongs to source '{existing}', not '{requested}'.");
        }
    }

    private string[] CreateSchemaStatements() =>
    [
        $"CREATE SCHEMA IF NOT EXISTS {_schema}",
        $"""
        CREATE TABLE IF NOT EXISTS {_metadataTable} (
            singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
            schema_version integer NOT NULL CHECK (schema_version > 0),
            updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
        )
        """,
        $"""
        INSERT INTO {_metadataTable} (singleton, schema_version)
        VALUES (true, {CurrentSchemaVersion})
        ON CONFLICT (singleton) DO NOTHING
        """,
        $"""
        CREATE TABLE IF NOT EXISTS {_pipelinesTable} (
            pipeline_id text PRIMARY KEY,
            source_fingerprint text NOT NULL,
            transform_name text NOT NULL,
            transform_fingerprint char(64) NOT NULL,
            checkpoint_position numeric(20, 0) NULL,
            checkpoint_transaction_id bigint NULL,
            store_generation bigint NOT NULL DEFAULT 0 CHECK (store_generation >= 0),
            snapshot_epoch uuid NULL,
            snapshot_complete boolean NOT NULL DEFAULT false,
            updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            CHECK (checkpoint_position IS NULL OR checkpoint_position >= 0),
            CHECK (checkpoint_transaction_id IS NULL OR checkpoint_transaction_id >= 0)
        )
        """,
        $"""
        CREATE TABLE IF NOT EXISTS {_documentsTable} (
            pipeline_id text NOT NULL REFERENCES {_pipelinesTable}(pipeline_id) ON DELETE CASCADE,
            collection_name text NOT NULL,
            document_key text NOT NULL,
            partition_key text NULL,
            content_type text NOT NULL,
            content bytea NOT NULL,
            source_change_id text NOT NULL,
            snapshot_epoch uuid NULL,
            updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            PRIMARY KEY (pipeline_id, collection_name, document_key)
        )
        """,
        $"""
        CREATE TABLE IF NOT EXISTS {_quarantineTable} (
            pipeline_id text NOT NULL,
            source_fingerprint text NOT NULL,
            transaction_id bigint NOT NULL CHECK (transaction_id >= 0),
            commit_position numeric(20, 0) NOT NULL CHECK (commit_position >= 0),
            transform_fingerprint char(64) NOT NULL,
            error_type text NOT NULL,
            error_message text NOT NULL,
            recorded_at timestamptz NOT NULL,
            PRIMARY KEY (pipeline_id, source_fingerprint, commit_position, transaction_id)
        )
        """,
    ];

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

    private async ValueTask<int> ReadSchemaVersionAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT schema_version FROM {_metadataTable} WHERE singleton";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record PipelineState(
        string SourceFingerprint,
        string TransformFingerprint,
        BlueTuskLogSequenceNumber? CheckpointPosition,
        Guid? SnapshotEpoch,
        bool SnapshotComplete);
}
