using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using BlueTusk.TypeSystem;

namespace BlueTusk.Streams.Storage.PostgreSql;

public static class ChangeRelayBackupFormat
{
    public const int CurrentVersion = 1;
}

public sealed record ChangeRelayBackupOptions
{
    public int MaxFrameBytes { get; init; } = 257 * 1024 * 1024;

    public bool IncludeSnapshotRuns { get; init; } = true;

    public bool IncludeDeadLetters { get; init; } = true;

    internal void Validate() => ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxFrameBytes);
}

public sealed record ChangeRelayRestoreOptions
{
    public int MaxFrameBytes { get; init; } = 257 * 1024 * 1024;

    internal void Validate() => ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxFrameBytes);
}

public sealed record ChangeRelayBackupResult(
    int FormatVersion,
    string SourceFingerprint,
    long Transactions,
    long ConsumerGroups,
    long SnapshotRuns,
    long DeadLetters,
    long BytesWritten);

public sealed record ChangeRelayRestoreResult(
    int FormatVersion,
    ChangeRelaySourceRegistration Source,
    long Transactions,
    long ConsumerGroups,
    long SnapshotRuns,
    long DeadLetters,
    long BytesRead);

public sealed partial class PostgreSqlDurableChangeRelay
{
    public async ValueTask<ChangeRelayBackupResult> BackupAsync(
        ChangeRelaySourceRegistration source,
        Stream destination,
        ChangeRelayBackupOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The relay backup destination must be writable.", nameof(destination));
        }

        var effectiveOptions = options ?? new ChangeRelayBackupOptions();
        effectiveOptions.Validate();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken).ConfigureAwait(false);
        var current = await ReadSourceAsync(
            connection,
            transaction,
            source.Source.Fingerprint,
            forUpdate: false,
            cancellationToken).ConfigureAwait(false) ??
            throw new ChangeRelaySourceMismatchException("The relay source is not registered.");
        EnsureSourceCompatible(source.Source, current.Source);
        if (current.SourceEpoch != source.SourceEpoch)
        {
            throw new ChangeRelaySourceMismatchException(
                $"Relay source epoch {source.SourceEpoch} is no longer active; current epoch is {current.SourceEpoch}.");
        }

        long bytesWritten = 0;
        long transactions = 0;
        long consumerGroups = 0;
        long snapshotRuns = 0;
        long deadLetters = 0;
        bytesWritten += await WriteFrameAsync(
            destination,
            BackupFrameKind.Source,
            CreatePayload(writer =>
            {
                writer.Write(ChangeRelayBackupFormat.CurrentVersion);
                writer.Write(CurrentSchemaVersion);
                WriteString(writer, current.Source.SystemIdentifier);
                WriteString(writer, current.Source.DatabaseName);
                WriteString(writer, current.Source.SlotName);
                WriteString(writer, current.Source.PublicationFingerprint);
                writer.Write(current.SourceEpoch);
                writer.Write(current.LastSequence);
                writer.Write(current.LastCommitPosition.Value);
            }),
            effectiveOptions.MaxFrameBytes,
            cancellationToken).ConfigureAwait(false);

        await using (var command = CreateCommand(
                         connection,
                         transaction,
                         $"""
                          SELECT sequence, commit_position, transaction_id, envelope_format,
                                 protection_id, envelope, appended_at
                          FROM {_transactionsTable}
                          WHERE source_fingerprint = @source AND source_epoch = @epoch
                          ORDER BY sequence
                          """))
        {
            AddParameter(command, "source", current.Source.Fingerprint);
            AddParameter(command, "epoch", current.SourceEpoch);
            await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess,
                cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var payload = CreatePayload(writer =>
                {
                    writer.Write(reader.GetInt64(0));
                    writer.Write(checked((ulong)reader.GetDecimal(1)));
                    writer.Write(checked((uint)reader.GetInt64(2)));
                    writer.Write(reader.GetInt32(3));
                    WriteOptionalString(writer, reader.IsDBNull(4) ? null : reader.GetString(4));
                    WriteBytes(writer, reader.GetFieldValue<byte[]>(5));
                    writer.Write(reader.GetFieldValue<DateTimeOffset>(6).UtcTicks);
                });
                bytesWritten += await WriteFrameAsync(
                    destination,
                    BackupFrameKind.Transaction,
                    payload,
                    effectiveOptions.MaxFrameBytes,
                    cancellationToken).ConfigureAwait(false);
                transactions++;
            }
        }

        await using (var command = CreateCommand(
                         connection,
                         transaction,
                         $"""
                          SELECT consumer_group, start_sequence, checkpoint_sequence,
                                 store_generation, active, removed_at,
                                 retention_protected_until, last_fencing_token
                          FROM {_groupsTable}
                          WHERE source_fingerprint = @source AND source_epoch = @epoch
                          ORDER BY consumer_group
                          """))
        {
            AddParameter(command, "source", current.Source.Fingerprint);
            AddParameter(command, "epoch", current.SourceEpoch);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var payload = CreatePayload(writer =>
                {
                    WriteString(writer, reader.GetString(0));
                    writer.Write(reader.GetInt64(1));
                    writer.Write(reader.GetInt64(2));
                    writer.Write(reader.GetInt64(3));
                    writer.Write(reader.GetBoolean(4));
                    WriteOptionalTimestamp(writer, reader, 5);
                    WriteOptionalTimestamp(writer, reader, 6);
                    writer.Write(reader.GetInt64(7));
                });
                bytesWritten += await WriteFrameAsync(
                    destination,
                    BackupFrameKind.ConsumerGroup,
                    payload,
                    effectiveOptions.MaxFrameBytes,
                    cancellationToken).ConfigureAwait(false);
                consumerGroups++;
            }
        }

        if (effectiveOptions.IncludeSnapshotRuns)
        {
            await using var command = CreateCommand(
                connection,
                transaction,
                $"""
                 SELECT snapshot_epoch, state, progress, updated_at
                 FROM {_schema}.snapshot_runs
                 WHERE source_fingerprint = @source AND source_epoch = @epoch
                 ORDER BY snapshot_epoch
                 """);
            AddParameter(command, "source", current.Source.Fingerprint);
            AddParameter(command, "epoch", current.SourceEpoch);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var payload = CreatePayload(writer =>
                {
                    WriteString(writer, reader.GetString(0));
                    WriteString(writer, reader.GetString(1));
                    WriteOptionalBytes(writer, reader.IsDBNull(2) ? null : reader.GetFieldValue<byte[]>(2));
                    writer.Write(reader.GetFieldValue<DateTimeOffset>(3).UtcTicks);
                });
                bytesWritten += await WriteFrameAsync(
                    destination,
                    BackupFrameKind.SnapshotRun,
                    payload,
                    effectiveOptions.MaxFrameBytes,
                    cancellationToken).ConfigureAwait(false);
                snapshotRuns++;
            }
        }

        if (effectiveOptions.IncludeDeadLetters)
        {
            await using var command = CreateCommand(
                connection,
                transaction,
                $"""
                 SELECT consumer_group, sequence, reason, payload, created_at
                 FROM {_schema}.dead_letters
                 WHERE source_fingerprint = @source AND source_epoch = @epoch
                 ORDER BY id
                 """);
            AddParameter(command, "source", current.Source.Fingerprint);
            AddParameter(command, "epoch", current.SourceEpoch);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var payload = CreatePayload(writer =>
                {
                    WriteString(writer, reader.GetString(0));
                    WriteOptionalInt64(writer, reader.IsDBNull(1) ? null : reader.GetInt64(1));
                    WriteString(writer, reader.GetString(2));
                    WriteOptionalBytes(writer, reader.IsDBNull(3) ? null : reader.GetFieldValue<byte[]>(3));
                    writer.Write(reader.GetFieldValue<DateTimeOffset>(4).UtcTicks);
                });
                bytesWritten += await WriteFrameAsync(
                    destination,
                    BackupFrameKind.DeadLetter,
                    payload,
                    effectiveOptions.MaxFrameBytes,
                    cancellationToken).ConfigureAwait(false);
                deadLetters++;
            }
        }

        await using (var command = CreateCommand(
                         connection,
                         transaction,
                         $"""
                          SELECT retained_after_sequence, updated_at
                          FROM {_schema}.retention_watermarks
                          WHERE source_fingerprint = @source AND source_epoch = @epoch
                          """))
        {
            AddParameter(command, "source", current.Source.Fingerprint);
            AddParameter(command, "epoch", current.SourceEpoch);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                bytesWritten += await WriteFrameAsync(
                    destination,
                    BackupFrameKind.RetentionWatermark,
                    CreatePayload(writer =>
                    {
                        writer.Write(reader.GetInt64(0));
                        writer.Write(reader.GetFieldValue<DateTimeOffset>(1).UtcTicks);
                    }),
                    effectiveOptions.MaxFrameBytes,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        bytesWritten += await WriteFrameAsync(
            destination,
            BackupFrameKind.End,
            ReadOnlyMemory<byte>.Empty,
            effectiveOptions.MaxFrameBytes,
            cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ChangeRelayBackupResult(
            ChangeRelayBackupFormat.CurrentVersion,
            current.Source.Fingerprint,
            transactions,
            consumerGroups,
            snapshotRuns,
            deadLetters,
            bytesWritten);
    }

    public async ValueTask<ChangeRelayRestoreResult> RestoreAsync(
        Stream backup,
        string confirmation,
        ChangeRelayRestoreOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmation);
        if (!backup.CanRead)
        {
            throw new ArgumentException("The relay backup source must be readable.", nameof(backup));
        }

        var effectiveOptions = options ?? new ChangeRelayRestoreOptions();
        effectiveOptions.Validate();
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        await EnsureRelayEmptyAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        ChangeRelaySourceRegistration? restoredSource = null;
        long transactions = 0;
        long consumerGroups = 0;
        long snapshotRuns = 0;
        long deadLetters = 0;
        long storedBytes = 0;
        long bytesRead = 0;
        var ended = false;
        while (!ended)
        {
            var frame = await ReadFrameAsync(
                backup,
                effectiveOptions.MaxFrameBytes,
                cancellationToken).ConfigureAwait(false);
            bytesRead = checked(bytesRead + frame.BytesRead);
            if (restoredSource is null && frame.Kind != BackupFrameKind.Source)
            {
                throw new ChangeRelayBackupException("The relay backup does not begin with a source frame.");
            }

            try
            {
                switch (frame.Kind)
                {
                    case BackupFrameKind.Source:
                        if (restoredSource is not null)
                        {
                            throw new ChangeRelayBackupException("The relay backup contains more than one source frame.");
                        }

                        restoredSource = await RestoreSourceAsync(
                            connection,
                            transaction,
                            frame.Payload,
                            confirmation,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case BackupFrameKind.Transaction:
                        storedBytes = checked(storedBytes + await RestoreTransactionAsync(
                            connection,
                            transaction,
                            restoredSource!,
                            frame.Payload,
                            cancellationToken).ConfigureAwait(false));
                        transactions++;
                        break;
                    case BackupFrameKind.ConsumerGroup:
                        await RestoreConsumerGroupAsync(
                            connection,
                            transaction,
                            restoredSource!,
                            frame.Payload,
                            cancellationToken).ConfigureAwait(false);
                        consumerGroups++;
                        break;
                    case BackupFrameKind.SnapshotRun:
                        await RestoreSnapshotRunAsync(
                            connection,
                            transaction,
                            restoredSource!,
                            frame.Payload,
                            cancellationToken).ConfigureAwait(false);
                        snapshotRuns++;
                        break;
                    case BackupFrameKind.DeadLetter:
                        await RestoreDeadLetterAsync(
                            connection,
                            transaction,
                            restoredSource!,
                            frame.Payload,
                            cancellationToken).ConfigureAwait(false);
                        deadLetters++;
                        break;
                    case BackupFrameKind.RetentionWatermark:
                        await RestoreRetentionWatermarkAsync(
                            connection,
                            transaction,
                            restoredSource!,
                            frame.Payload,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case BackupFrameKind.End:
                        if (!frame.Payload.IsEmpty)
                        {
                            throw new ChangeRelayBackupException("The relay backup end frame is not empty.");
                        }

                        ended = true;
                        break;
                    default:
                        throw new ChangeRelayBackupException(
                            $"The relay backup contains unknown frame kind {(byte)frame.Kind}.");
                }
            }
            catch (ChangeRelayException)
            {
                throw;
            }
            catch (Exception exception) when (exception is
                IOException or
                ArgumentException or
                OverflowException or
                ChangeTransactionEnvelopeException)
            {
                throw new ChangeRelayBackupException(
                    $"Relay backup frame {(byte)frame.Kind} contains invalid payload data.",
                    exception);
            }
        }

        if (restoredSource is null)
        {
            throw new ChangeRelayBackupException("The relay backup does not contain source metadata.");
        }

        var trailing = new byte[1];
        if (await backup.ReadAsync(trailing, cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new ChangeRelayBackupException("The relay backup contains trailing data.");
        }

        await using (var metadata = CreateCommand(
                         connection,
                         transaction,
                         $"""
                          UPDATE {_metadataTable}
                          SET relay_bytes = @bytes, updated_at = clock_timestamp()
                          WHERE singleton
                          """))
        {
            AddParameter(metadata, "bytes", storedBytes);
            _ = await metadata.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (restoredSource.LastSequence > 0)
        {
            await using var sequence = CreateCommand(
                connection,
                transaction,
                $"""
                 SELECT setval(
                     pg_get_serial_sequence(
                         format('%I.%I', @schema, 'relay_transactions'),
                         'sequence'),
                     @sequence,
                     true)
                 """);
            AddParameter(sequence, "schema", _options.ControlSchema);
            AddParameter(sequence, "sequence", restoredSource.LastSequence);
            _ = await sequence.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ChangeRelayRestoreResult(
            ChangeRelayBackupFormat.CurrentVersion,
            restoredSource,
            transactions,
            consumerGroups,
            snapshotRuns,
            deadLetters,
            bytesRead);
    }

    private async ValueTask EnsureRelayEmptyAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            $"""
             SELECT
                 (SELECT COUNT(*) FROM {_sourcesTable}) +
                 (SELECT COUNT(*) FROM {_transactionsTable}) +
                 (SELECT COUNT(*) FROM {_groupsTable}) +
                 (SELECT COUNT(*) FROM {_stateTable}) +
                 (SELECT COUNT(*) FROM {_schema}.snapshot_runs) +
                 (SELECT COUNT(*) FROM {_schema}.dead_letters) +
                 (SELECT COUNT(*) FROM {_schema}.retention_watermarks)
             """);
        var count = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (count != 0)
        {
            throw new ChangeRelayBackupException(
                "Relay restore requires an empty control schema; restore into a new schema or database.");
        }
    }

    private async ValueTask<ChangeRelaySourceRegistration> RestoreSourceAsync(
        DbConnection connection,
        DbTransaction transaction,
        ReadOnlyMemory<byte> payload,
        string confirmation,
        CancellationToken cancellationToken)
    {
        using var reader = CreateReader(payload);
        var formatVersion = reader.ReadInt32();
        if (formatVersion != ChangeRelayBackupFormat.CurrentVersion)
        {
            throw new ChangeRelayBackupException(
                $"Relay backup format {formatVersion} is not supported.");
        }

        var schemaVersion = reader.ReadInt32();
        if (schemaVersion > CurrentSchemaVersion)
        {
            throw new ChangeRelayBackupException(
                $"Relay backup schema version {schemaVersion} is newer than supported version {CurrentSchemaVersion}.");
        }

        var source = new ChangeSourceIdentity(
            ReadString(reader),
            ReadString(reader),
            ReadString(reader),
            ReadString(reader));
        var sourceEpoch = reader.ReadInt64();
        var lastSequence = reader.ReadInt64();
        var lastCommitPosition = new BlueTuskLogSequenceNumber(reader.ReadUInt64());
        EnsurePayloadComplete(reader);
        if (!string.Equals(confirmation, source.Fingerprint, StringComparison.Ordinal))
        {
            throw new ChangeRelayBackupException(
                "Relay restore confirmation must exactly match the backup source fingerprint.");
        }

        if (sourceEpoch <= 0 || lastSequence < 0)
        {
            throw new ChangeRelayBackupException("The relay backup source counters are invalid.");
        }

        await using var command = CreateCommand(
            connection,
            transaction,
            $"""
             INSERT INTO {_sourcesTable} (
                 source_fingerprint, system_identifier, database_name, slot_name,
                 publication_fingerprint, source_epoch, last_sequence,
                 last_commit_position)
             VALUES (
                 @source, @system, @database, @slot, @publication, @epoch,
                 @sequence, @position)
             """);
        AddParameter(command, "source", source.Fingerprint);
        AddParameter(command, "system", source.SystemIdentifier);
        AddParameter(command, "database", source.DatabaseName);
        AddParameter(command, "slot", source.SlotName);
        AddParameter(command, "publication", source.PublicationFingerprint);
        AddParameter(command, "epoch", sourceEpoch);
        AddParameter(command, "sequence", lastSequence);
        AddParameter(command, "position", (decimal)lastCommitPosition.Value);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return new ChangeRelaySourceRegistration(source, sourceEpoch, lastSequence, lastCommitPosition);
    }

    private async ValueTask<int> RestoreTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        ChangeRelaySourceRegistration source,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        using var reader = CreateReader(payload);
        var sequence = reader.ReadInt64();
        var commitPosition = reader.ReadUInt64();
        var transactionId = reader.ReadUInt32();
        var envelopeFormat = reader.ReadInt32();
        var protectionId = ReadOptionalString(reader);
        var envelope = ReadBytes(reader, _options.MaxEnvelopeBytes);
        var appendedAt = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        EnsurePayloadComplete(reader);
        if (sequence <= 0 || sequence > source.LastSequence)
        {
            throw new ChangeRelayBackupException(
                $"Relay transaction sequence {sequence} is outside the source watermark.");
        }

        var plaintext = UnprotectEnvelope(protectionId, envelope);
        var decodedEnvelope = ChangeTransactionEnvelopeCodec.FromData(plaintext, _envelopeOptions);
        var decoded = ChangeTransactionEnvelopeCodec.Decode(decodedEnvelope, _envelopeOptions);
        if (!Equals(decoded.Source, source.Source) ||
            decoded.TransactionId != transactionId ||
            decoded.CommitEndPosition.Value != commitPosition ||
            decodedEnvelope.FormatVersion != envelopeFormat)
        {
            throw new ChangeRelayBackupException(
                $"Relay transaction sequence {sequence} does not match its decoded envelope identity.");
        }

        await using var command = CreateCommand(
            connection,
            transaction,
            $"""
             INSERT INTO {_transactionsTable} (
                 sequence, source_fingerprint, source_epoch, commit_position,
                 transaction_id, envelope_format, protection_id, envelope, appended_at)
             OVERRIDING SYSTEM VALUE
             VALUES (
                 @sequence, @source, @epoch, @position, @transaction_id,
                 @format, @protection_id, @envelope, @appended_at)
             """);
        AddParameter(command, "sequence", sequence);
        AddParameter(command, "source", source.Source.Fingerprint);
        AddParameter(command, "epoch", source.SourceEpoch);
        AddParameter(command, "position", (decimal)commitPosition);
        AddParameter(command, "transaction_id", (long)transactionId);
        AddParameter(command, "format", envelopeFormat);
        AddNullableStringParameter(command, "protection_id", protectionId);
        AddParameter(command, "envelope", envelope);
        AddParameter(command, "appended_at", appendedAt);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return envelope.Length;
    }

    private async ValueTask RestoreConsumerGroupAsync(
        DbConnection connection,
        DbTransaction transaction,
        ChangeRelaySourceRegistration source,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        using var reader = CreateReader(payload);
        var name = ReadString(reader);
        var startSequence = reader.ReadInt64();
        var checkpointSequence = reader.ReadInt64();
        var storeGeneration = reader.ReadInt64();
        var active = reader.ReadBoolean();
        var removedAt = ReadOptionalTimestamp(reader);
        var retentionProtectedUntil = ReadOptionalTimestamp(reader);
        var lastFencingToken = reader.ReadInt64();
        EnsurePayloadComplete(reader);
        if (startSequence < 0 || checkpointSequence < startSequence ||
            checkpointSequence > source.LastSequence || storeGeneration < 0 ||
            lastFencingToken < 0 || (active && removedAt is not null))
        {
            throw new ChangeRelayBackupException($"Relay consumer group '{name}' has invalid state.");
        }

        await using var command = CreateCommand(
            connection,
            transaction,
            $"""
             INSERT INTO {_groupsTable} (
                 source_fingerprint, source_epoch, consumer_group, start_sequence,
                 checkpoint_sequence, store_generation, last_fencing_token,
                 active, removed_at, retention_protected_until)
             VALUES (
                 @source, @epoch, @consumer, @start, @checkpoint, @generation,
                 @last_token, @active, @removed_at, @protected_until)
             """);
        AddParameter(command, "source", source.Source.Fingerprint);
        AddParameter(command, "epoch", source.SourceEpoch);
        AddParameter(command, "consumer", name);
        AddParameter(command, "start", startSequence);
        AddParameter(command, "checkpoint", checkpointSequence);
        AddParameter(command, "generation", storeGeneration);
        AddParameter(command, "last_token", lastFencingToken);
        AddParameter(command, "active", active);
        AddNullableTimestampParameter(command, "removed_at", removedAt);
        AddNullableTimestampParameter(command, "protected_until", retentionProtectedUntil);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RestoreSnapshotRunAsync(
        DbConnection connection,
        DbTransaction transaction,
        ChangeRelaySourceRegistration source,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        using var reader = CreateReader(payload);
        var snapshotEpoch = ReadString(reader);
        var state = ReadString(reader);
        var progress = ReadOptionalBytes(reader, _options.MaxEnvelopeBytes);
        var updatedAt = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        EnsurePayloadComplete(reader);
        await using var command = CreateCommand(
            connection,
            transaction,
            $"""
             INSERT INTO {_schema}.snapshot_runs (
                 source_fingerprint, source_epoch, snapshot_epoch, state,
                 progress, updated_at)
             VALUES (@source, @epoch, @snapshot, @state, @progress, @updated_at)
             """);
        AddParameter(command, "source", source.Source.Fingerprint);
        AddParameter(command, "epoch", source.SourceEpoch);
        AddParameter(command, "snapshot", snapshotEpoch);
        AddParameter(command, "state", state);
        AddNullableBinaryParameter(command, "progress", progress);
        AddParameter(command, "updated_at", updatedAt);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RestoreDeadLetterAsync(
        DbConnection connection,
        DbTransaction transaction,
        ChangeRelaySourceRegistration source,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        using var reader = CreateReader(payload);
        var consumerGroup = ReadString(reader);
        var sequence = ReadOptionalInt64(reader);
        var reason = ReadString(reader);
        var deadPayload = ReadOptionalBytes(reader, _options.MaxEnvelopeBytes);
        var createdAt = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        EnsurePayloadComplete(reader);
        if (sequence is < 0 || sequence > source.LastSequence)
        {
            throw new ChangeRelayBackupException("A relay dead letter has an invalid source sequence.");
        }

        await using var command = CreateCommand(
            connection,
            transaction,
            $"""
             INSERT INTO {_schema}.dead_letters (
                 source_fingerprint, source_epoch, consumer_group, sequence,
                 reason, payload, created_at)
             VALUES (
                 @source, @epoch, @consumer, @sequence, @reason, @payload,
                 @created_at)
             """);
        AddParameter(command, "source", source.Source.Fingerprint);
        AddParameter(command, "epoch", source.SourceEpoch);
        AddParameter(command, "consumer", consumerGroup);
        AddNullableInt64Parameter(command, "sequence", sequence);
        AddParameter(command, "reason", reason);
        AddNullableBinaryParameter(command, "payload", deadPayload);
        AddParameter(command, "created_at", createdAt);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RestoreRetentionWatermarkAsync(
        DbConnection connection,
        DbTransaction transaction,
        ChangeRelaySourceRegistration source,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        using var reader = CreateReader(payload);
        var watermark = reader.ReadInt64();
        var updatedAt = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        EnsurePayloadComplete(reader);
        if (watermark < 0 || watermark > source.LastSequence)
        {
            throw new ChangeRelayBackupException("The relay retention watermark is invalid.");
        }

        await using var command = CreateCommand(
            connection,
            transaction,
            $"""
             INSERT INTO {_schema}.retention_watermarks (
                 source_fingerprint, source_epoch, retained_after_sequence,
                 updated_at)
             VALUES (@source, @epoch, @watermark, @updated_at)
             """);
        AddParameter(command, "source", source.Source.Fingerprint);
        AddParameter(command, "epoch", source.SourceEpoch);
        AddParameter(command, "watermark", watermark);
        AddParameter(command, "updated_at", updatedAt);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static byte[] CreatePayload(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        write(writer);
        writer.Flush();
        return stream.ToArray();
    }

    private static async ValueTask<long> WriteFrameAsync(
        Stream destination,
        BackupFrameKind kind,
        ReadOnlyMemory<byte> payload,
        int maximumFrameBytes,
        CancellationToken cancellationToken)
    {
        if (payload.Length > maximumFrameBytes)
        {
            throw new ChangeRelayBackupException(
                $"Relay backup frame {(byte)kind} exceeds {maximumFrameBytes} bytes.");
        }

        var header = new byte[5];
        header[0] = (byte)kind;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), payload.Length);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header);
        hash.AppendData(payload.Span);
        var digest = hash.GetHashAndReset();
        await destination.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await destination.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await destination.WriteAsync(digest, cancellationToken).ConfigureAwait(false);
        return checked(header.Length + payload.Length + digest.Length);
    }

    private static async ValueTask<BackupFrame> ReadFrameAsync(
        Stream source,
        int maximumFrameBytes,
        CancellationToken cancellationToken)
    {
        var header = new byte[5];
        await ReadExactlyAsync(source, header, cancellationToken).ConfigureAwait(false);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (length < 0 || length > maximumFrameBytes)
        {
            throw new ChangeRelayBackupException($"The relay backup frame length {length} is invalid.");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(source, payload, cancellationToken).ConfigureAwait(false);
        var expectedDigest = new byte[32];
        await ReadExactlyAsync(source, expectedDigest, cancellationToken).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header);
        hash.AppendData(payload);
        var actualDigest = hash.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(expectedDigest, actualDigest))
        {
            throw new ChangeRelayBackupException("The relay backup frame integrity hash is invalid.");
        }

        return new BackupFrame(
            (BackupFrameKind)header[0],
            payload,
            checked(header.Length + payload.Length + expectedDigest.Length));
    }

    private static async ValueTask ReadExactlyAsync(
        Stream source,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await source.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new ChangeRelayBackupException("The relay backup ended before its current frame completed.");
            }

            offset += read;
        }
    }

    private static BinaryReader CreateReader(ReadOnlyMemory<byte> payload) =>
        new(new MemoryStream(payload.ToArray(), writable: false), Encoding.UTF8, leaveOpen: false);

    private static void EnsurePayloadComplete(BinaryReader reader)
    {
        if (reader.BaseStream.Position != reader.BaseStream.Length)
        {
            throw new ChangeRelayBackupException("A relay backup frame contains trailing payload data.");
        }
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.Write(value);
    }

    private static string ReadString(BinaryReader reader)
    {
        var value = reader.ReadString();
        if (Encoding.UTF8.GetByteCount(value) > 1024 * 1024)
        {
            throw new ChangeRelayBackupException("A relay backup string exceeds one MiB.");
        }

        return value;
    }

    private static void WriteOptionalString(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            WriteString(writer, value);
        }
    }

    private static string? ReadOptionalString(BinaryReader reader) => reader.ReadBoolean()
        ? ReadString(reader)
        : null;

    private static void WriteBytes(BinaryWriter writer, byte[] value)
    {
        writer.Write(value.Length);
        writer.Write(value);
    }

    private static byte[] ReadBytes(BinaryReader reader, int maximum)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > maximum || length > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new ChangeRelayBackupException($"A relay backup byte payload length {length} is invalid.");
        }

        var value = reader.ReadBytes(length);
        if (value.Length != length)
        {
            throw new EndOfStreamException();
        }

        return value;
    }

    private static void WriteOptionalBytes(BinaryWriter writer, byte[]? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            WriteBytes(writer, value);
        }
    }

    private static byte[]? ReadOptionalBytes(BinaryReader reader, int maximum) => reader.ReadBoolean()
        ? ReadBytes(reader, maximum)
        : null;

    private static void WriteOptionalInt64(BinaryWriter writer, long? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue)
        {
            writer.Write(value.Value);
        }
    }

    private static long? ReadOptionalInt64(BinaryReader reader) => reader.ReadBoolean()
        ? reader.ReadInt64()
        : null;

    private static void WriteOptionalTimestamp(BinaryWriter writer, DbDataReader reader, int ordinal)
    {
        writer.Write(!reader.IsDBNull(ordinal));
        if (!reader.IsDBNull(ordinal))
        {
            writer.Write(reader.GetFieldValue<DateTimeOffset>(ordinal).UtcTicks);
        }
    }

    private static DateTimeOffset? ReadOptionalTimestamp(BinaryReader reader) => reader.ReadBoolean()
        ? new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero)
        : null;

    private static void AddNullableBinaryParameter(DbCommand command, string name, byte[]? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.Binary;
        parameter.Value = (object?)value ?? DBNull.Value;
        _ = command.Parameters.Add(parameter);
    }

    private static void AddNullableInt64Parameter(DbCommand command, string name, long? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.Int64;
        parameter.Value = (object?)value ?? DBNull.Value;
        _ = command.Parameters.Add(parameter);
    }

    private static void AddNullableTimestampParameter(
        DbCommand command,
        string name,
        DateTimeOffset? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.DateTimeOffset;
        parameter.Value = (object?)value ?? DBNull.Value;
        _ = command.Parameters.Add(parameter);
    }

    private enum BackupFrameKind : byte
    {
        End,
        Source,
        Transaction,
        ConsumerGroup,
        SnapshotRun,
        DeadLetter,
        RetentionWatermark,
    }

    private sealed record BackupFrame(
        BackupFrameKind Kind,
        ReadOnlyMemory<byte> Payload,
        int BytesRead);
}

public sealed class ChangeRelayBackupException : ChangeRelayException
{
    public ChangeRelayBackupException(string message)
        : base(message)
    {
    }

    public ChangeRelayBackupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
