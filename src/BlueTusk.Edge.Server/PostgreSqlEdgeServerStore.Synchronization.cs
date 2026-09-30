namespace BlueTusk.Edge.Server;

public sealed partial class PostgreSqlEdgeServerStore
{
    /// <summary>Copies the current selective scope and its exact feed position in one transaction, with bounded retained snapshot cardinality.</summary>
    public async ValueTask<EdgeSnapshot> BeginSnapshotAsync(EdgeScope scope, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var state = await LockScopeAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);
        await using (var expired = Scoped(connection, transaction, $"DELETE FROM {_schema}.snapshots WHERE tenant=@tenant AND scope=@scope AND epoch=@epoch AND expires_at<=clock_timestamp()", scope))
        { _ = await expired.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        await using (var count = Scoped(connection, transaction, $"SELECT count(*) FROM {_schema}.snapshots WHERE tenant=@tenant AND scope=@scope AND epoch=@epoch", scope))
        { if ((long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! >= Options.MaxSnapshotsPerScope) { throw new EdgeCapacityException("Active scope snapshot capacity is full."); } }
        var snapshot = new EdgeSnapshot(Guid.NewGuid(), state.Head);
        await using (var create = Scoped(connection, transaction, $"""
            INSERT INTO {_schema}.snapshots VALUES(@tenant,@scope,@epoch,@snapshot,@position,clock_timestamp()+@lifetime*interval '1 millisecond');
            INSERT INTO {_schema}.snapshot_records SELECT tenant,scope,epoch,@snapshot,id,revision,payload,deleted FROM {_schema}.records WHERE tenant=@tenant AND scope=@scope AND epoch=@epoch AND NOT deleted
            """, scope))
        {
            Parameter(create, "snapshot", snapshot.Id);
            Parameter(create, "position", snapshot.Position);
            Parameter(create, "lifetime", Options.SnapshotLifetime.TotalMilliseconds);
            _ = await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    public async ValueTask<EdgeServerSnapshotPage> ReadSnapshotPageAsync(EdgeScope scope, Guid snapshotId, int maxRecords = 512, string? afterId = null, CancellationToken cancellationToken = default)
    {
        ValidateBatch(maxRecords);
        if (afterId is not null) { EdgeValidation.Key(afterId, nameof(afterId), 512); }
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        _ = await LockScopeAsync(connection, transaction, scope, cancellationToken, write: false).ConfigureAwait(false);
        await using (var validate = Scoped(connection, transaction, $"SELECT snapshot_id FROM {_schema}.snapshots WHERE tenant=@tenant AND scope=@scope AND epoch=@epoch AND snapshot_id=@snapshot AND expires_at>clock_timestamp() FOR SHARE", scope))
        {
            Parameter(validate, "snapshot", snapshotId);
            if (await validate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not Guid) { throw new EdgeServerSnapshotExpiredException(); }
        }
        await using var page = Scoped(connection, transaction, $"""
            WITH candidate AS(SELECT id,revision,payload,deleted FROM {_schema}.snapshot_records WHERE tenant=@tenant AND scope=@scope AND epoch=@epoch AND snapshot_id=@snapshot AND (@after IS NULL OR id>@after) ORDER BY id LIMIT @limit),
            bounded AS(SELECT *,sum(octet_length(payload)) OVER(ORDER BY id) AS bytes FROM candidate)
            SELECT id,revision,payload,deleted FROM bounded WHERE bytes<=@bytes ORDER BY id
            """, scope);
        Parameter(page, "snapshot", snapshotId);
        Parameter(page, "after", afterId, System.Data.DbType.String);
        Parameter(page, "limit", maxRecords + 1);
        Parameter(page, "bytes", Options.MaxBatchBytes);
        var records = new List<EdgeRecord>(maxRecords + 1);
        await using (var reader = await page.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        { while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { records.Add(ReadRecord(reader)); } }
        // If a byte bound rather than row count stopped the page, an existence probe preserves a resumable keyset cursor.
        var more = records.Count > maxRecords;
        if (more) { records.RemoveAt(records.Count - 1); }
        if (!more && records.Count != 0)
        {
            await using var probe = Scoped(connection, transaction, $"SELECT EXISTS(SELECT 1 FROM {_schema}.snapshot_records WHERE tenant=@tenant AND scope=@scope AND epoch=@epoch AND snapshot_id=@snapshot AND id>@last)", scope);
            Parameter(probe, "snapshot", snapshotId);
            Parameter(probe, "last", records[^1].Id);
            more = await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(records.AsReadOnly(), more ? records[^1].Id : null);
    }

    public async ValueTask ReleaseSnapshotAsync(EdgeScope scope, Guid snapshotId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        _ = await LockScopeAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);
        await using var command = Scoped(connection, transaction, $"DELETE FROM {_schema}.snapshots WHERE tenant=@tenant AND scope=@scope AND epoch=@epoch AND snapshot_id=@snapshot", scope);
        Parameter(command, "snapshot", snapshotId);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<EdgeChangeBatch?> ReadChangesAsync(EdgeScope scope, long afterPosition, int maxRecords = 512, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterPosition);
        ValidateBatch(maxRecords);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var state = await LockScopeAsync(connection, transaction, scope, cancellationToken, write: false).ConfigureAwait(false);
        if (afterPosition < state.Floor) { throw new EdgeServerReplayExpiredException(); }
        if (afterPosition > state.Head) { throw new EdgeCheckpointMismatchException(); }
        await using var page = Scoped(connection, transaction, $"""
            WITH candidate AS(SELECT position,id,revision,payload,deleted FROM {_schema}.changes WHERE tenant=@tenant AND scope=@scope AND epoch=@epoch AND position>@after ORDER BY position LIMIT @limit),
            bounded AS(SELECT *,sum(octet_length(payload)) OVER(ORDER BY position) AS bytes FROM candidate)
            SELECT position,id,revision,payload,deleted FROM bounded WHERE bytes<=@bytes ORDER BY position
            """, scope);
        Parameter(page, "after", afterPosition);
        Parameter(page, "limit", maxRecords);
        Parameter(page, "bytes", Options.MaxBatchBytes);
        var records = new List<EdgeRecord>(maxRecords);
        var through = afterPosition;
        await using (var reader = await page.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                through = reader.GetInt64(0);
                records.Add(ReadRecord(reader, 1));
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return records.Count == 0 ? null : new(afterPosition, through, records.AsReadOnly());
    }

    /// <summary>Administrative retention floor. Lagging clients must resnapshot; mutation receipts and record tombstones are retained.</summary>
    public async ValueTask<int> PruneChangesAsync(EdgeScope scope, long throughPosition, int maximumCount = 1000, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(throughPosition);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCount, 10_000);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var state = await LockScopeAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);
        if (throughPosition > state.Head) { throw new EdgeCheckpointMismatchException(); }
        int count;
        long bytes;
        long floor;
        await using (var prune = Scoped(connection, transaction, $"""
            WITH victims AS(SELECT position FROM {_schema}.changes WHERE tenant=@tenant AND scope=@scope AND epoch=@epoch AND position<=@through ORDER BY position LIMIT @limit),
            removed AS(DELETE FROM {_schema}.changes WHERE tenant=@tenant AND scope=@scope AND epoch=@epoch AND position IN(SELECT position FROM victims) RETURNING position,payload)
            SELECT count(*)::integer,coalesce(sum(octet_length(payload)),0),coalesce(max(position),@floor) FROM removed
            """, scope))
        {
            Parameter(prune, "through", throughPosition);
            Parameter(prune, "limit", maximumCount);
            Parameter(prune, "floor", state.Floor);
            await using var reader = await prune.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            count = reader.GetInt32(0); bytes = reader.GetInt64(1); floor = reader.GetInt64(2);
        }
        await using (var update = Scoped(connection, transaction, $"UPDATE {_schema}.scopes SET floor=@floor,change_count=change_count-@count,change_bytes=change_bytes-@bytes WHERE tenant=@tenant AND scope=@scope AND epoch=@epoch", scope))
        {
            Parameter(update, "floor", floor); Parameter(update, "count", count); Parameter(update, "bytes", bytes);
            _ = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    private void ValidateBatch(int maxRecords)
    {
        if (maxRecords < 1 || maxRecords > Options.MaxBatchRecords) { throw new ArgumentOutOfRangeException(nameof(maxRecords)); }
    }
}
