namespace BlueTusk.Edge.Server;

/// <summary>Payload-free committed capacity and replay state for one host-authorized scope.</summary>
public sealed class EdgeServerHealthSnapshot
{
    internal EdgeServerHealthSnapshot(DateTimeOffset databaseTime, long head, long floor, long records, long recordBytes,
        long receipts, long changes, long changeBytes, int snapshots, bool mutationCapacityReached, bool snapshotCapacityReached)
    {
        DatabaseTime = databaseTime; HeadPosition = head; ReplayFloor = floor; RecordCount = records; RecordBytes = recordBytes;
        ReceiptCount = receipts; ChangeCount = changes; ChangeBytes = changeBytes; ActiveSnapshots = snapshots;
        MutationCapacityReached = mutationCapacityReached; SnapshotCapacityReached = snapshotCapacityReached;
    }
    public DateTimeOffset DatabaseTime { get; }
    public long HeadPosition { get; }
    public long ReplayFloor { get; }
    public long RecordCount { get; }
    public long RecordBytes { get; }
    public long ReceiptCount { get; }
    public long ChangeCount { get; }
    public long ChangeBytes { get; }
    public int ActiveSnapshots { get; }
    public bool MutationCapacityReached { get; }
    public bool SnapshotCapacityReached { get; }
}

public sealed partial class PostgreSqlEdgeServerStore
{
    /// <summary>Reads exact counters and at most the configured scope snapshot bound, without fetching payloads or changing state.</summary>
    public async ValueTask<EdgeServerHealthSnapshot> ReadHealthAsync(EdgeScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var state = await LockScopeAsync(connection, transaction, scope, cancellationToken, write: false).ConfigureAwait(false);
        DateTimeOffset databaseTime; int snapshots;
        await using (var command = Scoped(connection, transaction, $"""
            SELECT clock_timestamp(),version,max_record_bytes,
              (SELECT count(*) FROM(SELECT snapshot_id FROM {_schema}.snapshots WHERE tenant=@tenant AND scope=@scope AND epoch=@epoch AND expires_at>clock_timestamp() LIMIT @maximum) AS bounded)
            FROM {_schema}.metadata WHERE singleton
            """, scope))
        {
            Parameter(command, "maximum", Options.MaxSnapshotsPerScope + 1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetInt32(1) != 1 || reader.GetInt32(2) != Options.MaxRecordBytes)
            { throw new InvalidOperationException("The Edge server storage version or installed record byte contract differs."); }
            databaseTime = reader.GetFieldValue<DateTimeOffset>(0); snapshots = checked((int)reader.GetInt64(3));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(databaseTime, state.Head, state.Floor, state.RecordCount, state.RecordBytes, state.ReceiptCount, state.ChangeCount, state.ChangeBytes, snapshots,
            state.RecordCount >= Options.MaxRecordsPerScope || state.RecordBytes >= Options.MaxRecordBytesPerScope ||
            state.ReceiptCount >= Options.MaxReceiptsPerScope || state.ChangeCount >= Options.MaxChangesPerScope || state.ChangeBytes >= Options.MaxChangeBytesPerScope,
            snapshots >= Options.MaxSnapshotsPerScope);
    }
}
