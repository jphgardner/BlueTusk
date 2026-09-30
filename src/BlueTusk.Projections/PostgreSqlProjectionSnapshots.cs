using System.Buffers;
using System.Buffers.Binary;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueTusk.Streams;
using BlueTusk.TypeSystem;

namespace BlueTusk.Projections;

public sealed partial class PostgreSqlProjectionStore
{
    /// <summary>Recovers the durably bound reset epoch after process loss. The start timestamp has PostgreSQL microsecond precision.</summary>
    public async ValueTask<SnapshotStart?> ReadSnapshotResetAsync(ProjectionIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var locked = await ReadStateAsync(connection, null, identity, false, cancellationToken).ConfigureAwait(false);
        return locked.State.Phase == ProjectionBuildPhase.Resetting
            ? new SnapshotStart(new(locked.State.SnapshotEpoch!.Value, identity.Source, locked.SnapshotPosition,
                locked.SnapshotStartedAt ?? throw new InvalidOperationException("The reset epoch timestamp is missing.")), locked.State.ExpectedTables) : null;
    }
    /// <summary>Begin or restart an unpublished version from a Streams consistent snapshot epoch.</summary>
    public async ValueTask StartSnapshotAsync(ProjectionLease lease, SnapshotStart start,
        CancellationToken cancellationToken = default)
    {
        var progress = await BeginSnapshotResetAsync(lease, start, cancellationToken).ConfigureAwait(false);
        while (!progress.IsComplete)
        {
            progress = await ContinueSnapshotResetAsync(lease, start, _options.MaximumResetBatchRows, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Durably blocks application/cutover and binds one replacement epoch before bounded reset deletion. Resume exactly this epoch after interruption.</summary>
    public async ValueTask<ProjectionSnapshotResetProgress> BeginSnapshotResetAsync(ProjectionLease lease, SnapshotStart start,
        CancellationToken cancellationToken = default)
    {
        ValidateLease(lease);
        ArgumentNullException.ThrowIfNull(start);
        ValidateEpoch(lease.Identity, start.Epoch);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(start.TableCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start.TableCount, 4096);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockHeadAsync(connection, transaction, lease.Identity.Name, cancellationToken).ConfigureAwait(false);
        var locked = await ReadStateAsync(connection, transaction, lease.Identity, true, cancellationToken).ConfigureAwait(false);
        EnsureLease(lease, locked);
        await ValidateLineageCoverageAsync(connection, transaction, lease.Identity, start.TableCount, null, cancellationToken).ConfigureAwait(false);
        if (locked.State.SnapshotEpoch == start.Epoch.Value && locked.State.Phase != ProjectionBuildPhase.Empty)
        {
            if (locked.State.ExpectedTables != start.TableCount || locked.SnapshotPosition != start.Epoch.ConsistentPosition)
            {
                throw new InvalidOperationException("A snapshot epoch cannot be reused with different table coverage or consistent position.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ProjectionSnapshotResetProgress(start.Epoch.Value, 0, locked.State.Phase != ProjectionBuildPhase.Resetting);
        }

        if (locked.State.Phase == ProjectionBuildPhase.Resetting)
        {
            throw new InvalidOperationException("A durable reset is in progress. Resume its exact epoch before replacing it.");
        }

        await EnsureUnpublishedAsync(connection, transaction, lease.Identity, cancellationToken).ConfigureAwait(false);
        await using (var command = LeaseCommand(connection, transaction, lease, $"""
            UPDATE {_schema}.state SET phase = 3, checkpoint = 0, generation = generation + 1,
                snapshot_epoch = @epoch, snapshot_position = @position,snapshot_started_at=@started, expected_tables = @tables, completed_tables = 0, snapshot_rows = 0
            WHERE projection = @projection AND version = @version AND owner_id = @owner AND fencing_token = @token AND expires_at > clock_timestamp()
            """, ("epoch", start.Epoch.Value), ("position", (decimal)start.Epoch.ConsistentPosition.Value), ("started", start.Epoch.StartedAt), ("tables", start.TableCount)))
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new ProjectionFencedException();
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ProjectionSnapshotResetProgress(start.Epoch.Value, 0, false);
    }

    /// <summary>Deletes at most maximumRows derived rows in one fenced transaction, then admits snapshot input only after all reset state is empty.</summary>
    public async ValueTask<ProjectionSnapshotResetProgress> ContinueSnapshotResetAsync(ProjectionLease lease, SnapshotStart start,
        int maximumRows = 1024, CancellationToken cancellationToken = default)
    {
        ValidateLease(lease);
        ArgumentNullException.ThrowIfNull(start);
        ValidateEpoch(lease.Identity, start.Epoch);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRows);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumRows, _options.MaximumResetBatchRows);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockHeadAsync(connection, transaction, lease.Identity.Name, cancellationToken).ConfigureAwait(false);
        var locked = await ReadStateAsync(connection, transaction, lease.Identity, true, cancellationToken).ConfigureAwait(false);
        EnsureLease(lease, locked);
        if (locked.State.SnapshotEpoch != start.Epoch.Value || locked.SnapshotPosition != start.Epoch.ConsistentPosition || locked.State.ExpectedTables != start.TableCount)
        {
            throw new InvalidOperationException("Reset continuation must use the exact durably bound snapshot epoch, position and table coverage.");
        }
        if (locked.State.Phase != ProjectionBuildPhase.Resetting)
        {
            if (locked.State.Phase is not ProjectionBuildPhase.Snapshot and not ProjectionBuildPhase.CatchingUp)
            {
                throw new InvalidOperationException("The projection has no reset to continue.");
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ProjectionSnapshotResetProgress(start.Epoch.Value, 0, true);
        }
        await EnsureUnpublishedAsync(connection, transaction, lease.Identity, cancellationToken).ConfigureAwait(false);
        var progress = await DeleteDerivedRowsAsync(connection, transaction, lease.Identity, maximumRows, cancellationToken).ConfigureAwait(false);
        await using (var command = LeaseCommand(connection, transaction, lease, $"""
            UPDATE {_schema}.state SET generation=generation+1,phase=CASE WHEN @complete THEN 1 ELSE 3 END
            WHERE projection=@projection AND version=@version AND owner_id=@owner AND fencing_token=@token AND expires_at>clock_timestamp()
            """, ("complete", !progress.HasRemainingRows)))
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) { throw new ProjectionFencedException(); }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        ProjectionsDiagnostics.Reset(progress.DeletedRows);
        return new ProjectionSnapshotResetProgress(start.Epoch.Value, progress.DeletedRows, !progress.HasRemainingRows);
    }

    /// <summary>Apply a snapshot batch exactly once per epoch/table/sequence and reject conflicting retry content.</summary>
    public async ValueTask<bool> ApplySnapshotAsync(ProjectionLease lease, IProjectionDefinition definition,
        ChangeSnapshotBatch batch, CancellationToken cancellationToken = default)
    {
        ValidateDefinition(lease, definition);
        ArgumentNullException.ThrowIfNull(batch);
        ValidateEpoch(lease.Identity, batch.Epoch);
        if (batch.Rows.Count > _options.MaximumSnapshotBatchRows)
        {
            throw new ProjectionBoundExceededException("The snapshot batch row bound was exceeded.");
        }

        var tableId = batch.Table.Schema + "." + batch.Table.Name;
        ProjectionValidation.Key(tableId, nameof(batch));
        var fingerprint = Fingerprint(batch);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var locked = await ReadStateAsync(connection, transaction, lease.Identity, true, cancellationToken).ConfigureAwait(false);
        EnsureLease(lease, locked);
        if (locked.State.Phase == ProjectionBuildPhase.Resetting)
        {
            throw new InvalidOperationException("The bounded reset must complete before accepting snapshot input.");
        }
        await ValidateLineageCoverageAsync(connection, transaction, lease.Identity, null, batch.Table, cancellationToken).ConfigureAwait(false);
        if (locked.State.SnapshotEpoch != batch.Epoch.Value || locked.SnapshotPosition != batch.Epoch.ConsistentPosition)
        {
            throw new InvalidOperationException("The batch belongs to an abandoned or incompatible snapshot epoch.");
        }

        await using (var command = Command(connection, transaction, $"""
            SELECT fingerprint FROM {_schema}.snapshot_batches WHERE projection = @projection AND version = @version AND table_id = @table AND sequence = @sequence
            """, ("projection", lease.Identity.Name), ("version", lease.Identity.Version), ("table", tableId), ("sequence", batch.Sequence)))
        {
            var prior = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (prior is byte[] priorFingerprint)
            {
                if (!priorFingerprint.AsSpan().SequenceEqual(fingerprint))
                {
                    throw new InvalidOperationException("A snapshot batch identity was reused with different content.");
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        if (locked.State.Phase != ProjectionBuildPhase.Snapshot)
        {
            throw new InvalidOperationException("Snapshot input is closed; a new batch cannot be added after completion.");
        }

        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO {_schema}.snapshot_tables(projection, version, table_id) VALUES(@projection, @version, @table) ON CONFLICT DO NOTHING
            """, cancellationToken, ("projection", lease.Identity.Name), ("version", lease.Identity.Version), ("table", tableId)).ConfigureAwait(false);
        await using (var command = Command(connection, transaction, $"""
            SELECT next_sequence, completed FROM {_schema}.snapshot_tables WHERE projection = @projection AND version = @version AND table_id = @table
            """, ("projection", lease.Identity.Name), ("version", lease.Identity.Version), ("table", tableId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetInt64(0) != batch.Sequence || reader.GetBoolean(1))
            {
                throw new InvalidOperationException("Snapshot table batches must be contiguous from sequence zero and end exactly once.");
            }
        }

        await using (var command = Command(connection, transaction, $"SELECT count(*) FROM {_schema}.snapshot_tables WHERE projection = @projection AND version = @version",
                         ("projection", lease.Identity.Name), ("version", lease.Identity.Version)))
        {
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > locked.State.ExpectedTables)
            {
                throw new InvalidOperationException("Snapshot input contains more tables than its declared coverage.");
            }
        }

        if (batch.Rows.Count != 0)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartArray();
                foreach (var row in batch.Rows)
                {
                    writer.WriteStringValue(row.Id.KeyIdentity);
                }

                writer.WriteEndArray();
            }

            await using var command = Command(connection, transaction, $"""
                INSERT INTO {_schema}.snapshot_row_keys(projection, version, table_id, key_id)
                SELECT @projection, @version, @table, i.value FROM jsonb_array_elements_text(CAST(@keys AS jsonb)) i
                ON CONFLICT DO NOTHING
                """, ("projection", lease.Identity.Name), ("version", lease.Identity.Version), ("table", tableId),
                ("keys", Encoding.UTF8.GetString(buffer.WrittenSpan)));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != batch.Rows.Count)
            {
                throw new InvalidOperationException("The snapshot repeated a source row across different batches; aggregate effects cannot be applied twice.");
            }
        }

        var context = new ProjectionWriteContext(connection, transaction, lease.Identity, _options);
        try
        {
            await definition.ApplySnapshotAsync(batch, context, cancellationToken).ConfigureAwait(false);
            context.EnsureComplete();
        }
        finally
        {
            context.Close();
        }

        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO {_schema}.snapshot_batches(projection, version, table_id, sequence, fingerprint)
            VALUES(@projection, @version, @table, @sequence, @fingerprint)
            """, cancellationToken, ("projection", lease.Identity.Name), ("version", lease.Identity.Version), ("table", tableId),
            ("sequence", batch.Sequence), ("fingerprint", fingerprint)).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"""
            UPDATE {_schema}.snapshot_tables SET next_sequence = next_sequence + 1, completed = @completed
            WHERE projection = @projection AND version = @version AND table_id = @table
            """, cancellationToken, ("projection", lease.Identity.Name), ("version", lease.Identity.Version), ("table", tableId),
            ("completed", batch.IsLastForTable)).ConfigureAwait(false);
        await using (var command = LeaseCommand(connection, transaction, lease, $"""
            UPDATE {_schema}.state SET snapshot_rows = snapshot_rows + @rows,
                completed_tables = completed_tables + @completed, generation = generation + 1
            WHERE projection = @projection AND version = @version AND owner_id = @owner AND fencing_token = @token AND expires_at > clock_timestamp()
            """, ("rows", (long)batch.Rows.Count), ("completed", batch.IsLastForTable ? 1 : 0)))
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new ProjectionFencedException();
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        ProjectionsDiagnostics.Commit("snapshot", context);
        return true;
    }

    private async ValueTask ValidateLineageCoverageAsync(DbConnection connection, DbTransaction transaction, ProjectionIdentity identity,
        int? tableCount, ChangeTable? table, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"""
            SELECT source_snapshot_contract IS NULL OR (
                (@is_count AND jsonb_array_length(source_snapshot_contract)=@count) OR
                (NOT @is_count AND EXISTS(SELECT 1 FROM jsonb_array_elements(source_snapshot_contract) i
                    WHERE i->>'schema'=@schema AND i->>'name'=@name AND i->>'fingerprint'=@fingerprint)))
            FROM {_schema}.state WHERE projection=@projection AND version=@version
            """, ("projection", identity.Name), ("version", identity.Version), ("is_count", tableCount is not null), ("count", tableCount ?? 0),
            ("schema", table?.Schema ?? string.Empty), ("name", table?.Name ?? string.Empty),
            ("fingerprint", table is null ? string.Empty : ProjectionLineageTables.Fingerprint(table)));
        if ((bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == false)
        {
            throw new InvalidOperationException("Snapshot coverage/table metadata differs from the actual immutable publication contract bound before rebuild.");
        }
    }

    /// <summary>Open live CDC only when every declared snapshot table and row count has been durably covered.</summary>
    public async ValueTask CompleteSnapshotAsync(ProjectionLease lease, SnapshotComplete complete,
        CancellationToken cancellationToken = default)
    {
        ValidateLease(lease);
        ArgumentNullException.ThrowIfNull(complete);
        ValidateEpoch(lease.Identity, complete.Epoch);
        ArgumentOutOfRangeException.ThrowIfNegative(complete.RowCount);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var locked = await ReadStateAsync(connection, transaction, lease.Identity, true, cancellationToken).ConfigureAwait(false);
        EnsureLease(lease, locked);
        if (locked.State.SnapshotEpoch != complete.Epoch.Value || locked.SnapshotPosition != complete.Epoch.ConsistentPosition ||
            locked.State.ExpectedTables != complete.TableCount || locked.State.CompletedTables != complete.TableCount ||
            locked.State.SnapshotRows != complete.RowCount)
        {
            throw new InvalidOperationException("Snapshot completion does not match the durable epoch, complete table coverage, or row count.");
        }

        if (locked.State.Phase == ProjectionBuildPhase.CatchingUp)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (locked.State.Phase != ProjectionBuildPhase.Snapshot)
        {
            throw new InvalidOperationException("The projection does not have an active snapshot to complete.");
        }

        await using (var command = LeaseCommand(connection, transaction, lease, $"""
            UPDATE {_schema}.state SET phase = 2, checkpoint = snapshot_position, generation = generation + 1
            WHERE projection = @projection AND version = @version AND owner_id = @owner AND fencing_token = @token AND expires_at > clock_timestamp()
            """))
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new ProjectionFencedException();
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EnsureUnpublishedAsync(DbConnection connection, DbTransaction transaction,
        ProjectionIdentity identity, CancellationToken cancellationToken)
    {
        // Do not lock the head here: cutover locks the head before version states. Lock inversion would
        // deadlock. The already-held candidate state lock makes concurrent promotion wait for this reset.
        await using var command = Command(connection, transaction, $"SELECT active_version FROM {_schema}.heads WHERE projection = @projection", ("projection", identity.Name));
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is not null && result is not DBNull && Convert.ToInt32(result, CultureInfo.InvariantCulture) == identity.Version)
        {
            throw new InvalidOperationException("A published projection version cannot be reset. Build a new version beside it.");
        }
    }

    private static void ValidateEpoch(ProjectionIdentity identity, SnapshotEpoch epoch)
    {
        if (epoch.Value == Guid.Empty || epoch.Source != identity.Source || epoch.ConsistentPosition == BlueTuskLogSequenceNumber.Zero)
        {
            throw new ArgumentException("A snapshot epoch must carry a nonempty Streams source-bound consistent position.", nameof(epoch));
        }
    }

    private byte[] Fingerprint(ChangeSnapshotBatch batch)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, batch.Table.Schema);
        Append(hash, batch.Table.Name);
        Append(hash, batch.Table.RelationId.ToString(CultureInfo.InvariantCulture));
        Append(hash, batch.Table.ReplicaIdentity.ToString());
        foreach (var column in batch.Table.Columns)
        {
            Append(hash, column.Name);
            Append(hash, column.TypeOid.ToString(CultureInfo.InvariantCulture));
            Append(hash, column.TypeModifier.ToString(CultureInfo.InvariantCulture));
            Append(hash, column.IsKey ? "key" : "value");
            Append(hash, column.Type?.Oid.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            Append(hash, column.Type?.Namespace ?? string.Empty);
            Append(hash, column.Type?.Name ?? string.Empty);
        }

        Append(hash, batch.IsLastForTable ? "final" : "more");
        long bytes = 0;
        Span<byte> length = stackalloc byte[4];
        var rowIdentities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in batch.Rows)
        {
            if (row.Id.Epoch != batch.Epoch.Value || !string.Equals(row.Id.TableIdentity, batch.Table.Schema + "." + batch.Table.Name, StringComparison.Ordinal) ||
                row.Row.Table.Schema != batch.Table.Schema || row.Row.Table.Name != batch.Table.Name ||
                row.Row.Table.RelationId != batch.Table.RelationId || row.Row.Table.ReplicaIdentity != batch.Table.ReplicaIdentity ||
                !row.Row.Table.Columns.SequenceEqual(batch.Table.Columns) || !rowIdentities.Add(row.Id.KeyIdentity))
            {
                throw new ArgumentException("Snapshot rows must retain the batch's table/epoch identity and unique source keys.", nameof(batch));
            }

            ProjectionValidation.Key(row.Id.KeyIdentity, nameof(batch));
            Append(hash, row.Id.KeyIdentity);
            foreach (var value in row.Row.Values)
            {
                bytes = checked(bytes + value.Data.Length);
                if (bytes > _options.MaximumTransactionBytes)
                {
                    throw new ProjectionBoundExceededException("The snapshot batch exceeds the configured source byte bound.");
                }

                hash.AppendData([(byte)value.State, (byte)value.Encoding]);
                Append(hash, value.DecodingError ?? string.Empty);
                BinaryPrimitives.WriteInt32LittleEndian(length, value.Data.Length);
                hash.AppendData(length);
                hash.AppendData(value.Data.Span);
            }
        }

        return hash.GetHashAndReset();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
