using System.Globalization;
using Microsoft.Data.Sqlite;

namespace BlueTusk.Edge.Sqlite;

public sealed partial class SqliteEdgeStore
{
    public async ValueTask BeginSnapshotAsync(EdgeScope scope, EdgeSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Id == Guid.Empty || snapshot.Position < 0)
        {
            throw new ArgumentException("Snapshots require a stable identity and nonnegative position.", nameof(snapshot));
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var checkpoint = await CheckScopeAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);
        if (snapshot.Position < checkpoint.Position)
        {
            throw new EdgeCheckpointMismatchException();
        }

        // Repeated begin for the same snapshot preserves staged rows so a disconnected caller can resume.
        await using (var command = Command(connection, transaction, "DELETE FROM snapshot_records WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND snapshot_id<>@snapshot"))
        {
            ScopeParameters(command, scope);
            command.Parameters.AddWithValue("snapshot", snapshot.Id.ToString("N"));
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = Command(connection, transaction, "SELECT snapshot_position FROM scopes WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND snapshot_id=@snapshot"))
        {
            ScopeParameters(command, scope);
            command.Parameters.AddWithValue("snapshot", snapshot.Id.ToString("N"));
            var position = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (position is not null && Convert.ToInt64(position, CultureInfo.InvariantCulture) != snapshot.Position)
            {
                throw new EdgeCheckpointMismatchException();
            }
        }

        await using (var command = Command(connection, transaction, "UPDATE scopes SET snapshot_id=@snapshot,snapshot_position=@position WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch"))
        {
            ScopeParameters(command, scope);
            command.Parameters.AddWithValue("snapshot", snapshot.Id.ToString("N"));
            command.Parameters.AddWithValue("position", snapshot.Position);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ApplySnapshotBatchAsync(EdgeScope scope, Guid snapshotId, IReadOnlyList<EdgeRecord> records, CancellationToken cancellationToken = default)
    {
        ValidateRecords(records);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        _ = await CheckScopeAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);
        _ = await CheckSnapshotAsync(connection, transaction, scope, snapshotId, cancellationToken).ConfigureAwait(false);
        foreach (var record in records)
        {
            await StoreRecordAsync(connection, transaction, scope, record, snapshotId, cancellationToken).ConfigureAwait(false);
        }

        await CheckCacheBudgetAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CommitSnapshotAsync(EdgeScope scope, Guid snapshotId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var checkpoint = await CheckScopeAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);
        var position = await CheckSnapshotAsync(connection, transaction, scope, snapshotId, cancellationToken).ConfigureAwait(false);
        if (checkpoint.Position > position)
        {
            throw new EdgeCheckpointMismatchException();
        }
        await using (var command = Command(connection, transaction, """
            DELETE FROM records WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch;
            INSERT INTO records(tenant,scope_id,epoch,document_id,revision,payload,deleted,fingerprint)
                SELECT tenant,scope_id,epoch,document_id,revision,payload,deleted,fingerprint FROM snapshot_records
                WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND snapshot_id=@snapshot;
            DELETE FROM snapshot_records WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch;
            UPDATE scopes SET checkpoint=@position,ready=1,snapshot_id=NULL,snapshot_position=NULL
                WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch;
            """))
        {
            ScopeParameters(command, scope);
            command.Parameters.AddWithValue("snapshot", snapshotId.ToString("N"));
            command.Parameters.AddWithValue("position", position);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await CheckCacheBudgetAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ApplyChangesAsync(EdgeScope scope, EdgeChangeBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ValidateRecords(batch.Records);
        if (batch.FromPosition < 0 || batch.ToPosition <= batch.FromPosition)
        {
            throw new EdgeCheckpointMismatchException();
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var checkpoint = await CheckScopeAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);
        if (!checkpoint.SnapshotReady || checkpoint.Position != batch.FromPosition)
        {
            throw new EdgeCheckpointMismatchException();
        }

        await CheckNoSnapshotAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);

        foreach (var record in batch.Records)
        {
            await StoreRecordAsync(connection, transaction, scope, record, null, cancellationToken).ConfigureAwait(false);
        }

        await using (var command = Command(connection, transaction, "UPDATE scopes SET checkpoint=@position WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch"))
        {
            ScopeParameters(command, scope);
            command.Parameters.AddWithValue("position", batch.ToPosition);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await CheckCacheBudgetAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask CheckNoSnapshotAsync(SqliteConnection connection, SqliteTransaction transaction, EdgeScope scope, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT snapshot_id FROM scopes WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch");
        ScopeParameters(command, scope);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is not null and not DBNull)
        {
            throw new EdgeScopeMismatchException();
        }
    }

    private static async ValueTask<long> CheckSnapshotAsync(SqliteConnection connection, SqliteTransaction transaction, EdgeScope scope, Guid snapshotId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT snapshot_position FROM scopes WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND snapshot_id=@snapshot");
        ScopeParameters(command, scope);
        command.Parameters.AddWithValue("snapshot", snapshotId.ToString("N"));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull)
        {
            throw new EdgeScopeMismatchException();
        }

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async ValueTask StoreRecordAsync(SqliteConnection connection, SqliteTransaction transaction, EdgeScope scope, EdgeRecord record, Guid? snapshotId, CancellationToken cancellationToken)
    {
        var table = snapshotId is null ? "records" : "snapshot_records";
        var snapshotPredicate = snapshotId is null ? string.Empty : " AND snapshot_id=@snapshot";
        var fingerprint = RecordFingerprint(record);
        await using (var existing = Command(connection, transaction, $"SELECT revision,fingerprint FROM {table} WHERE tenant=@tenant AND scope_id=@scope AND epoch=@epoch AND document_id=@id{snapshotPredicate}"))
        {
            RecordParameters(existing, scope, record.Id, snapshotId);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var revision = reader.GetInt64(0);
                var current = reader.GetString(1);
                if (snapshotId is not null && revision != record.Revision || revision == record.Revision && !string.Equals(current, fingerprint, StringComparison.Ordinal))
                {
                    throw new EdgeRevisionConflictException();
                }

                if (revision >= record.Revision)
                {
                    return;
                }
            }
        }

        var snapshotColumn = snapshotId is null ? string.Empty : ",snapshot_id";
        var snapshotValue = snapshotId is null ? string.Empty : ",@snapshot";
        await using var command = Command(connection, transaction, $"""
            INSERT INTO {table}(tenant,scope_id,epoch,document_id,revision,payload,deleted,fingerprint{snapshotColumn})
            VALUES(@tenant,@scope,@epoch,@id,@revision,@payload,@deleted,@fingerprint{snapshotValue})
            ON CONFLICT DO UPDATE SET revision=excluded.revision,payload=excluded.payload,deleted=excluded.deleted,fingerprint=excluded.fingerprint
            """);
        RecordParameters(command, scope, record.Id, snapshotId);
        command.Parameters.AddWithValue("revision", record.Revision);
        command.Parameters.AddWithValue("payload", record.Payload.ToArray());
        command.Parameters.AddWithValue("deleted", record.Deleted ? 1 : 0);
        command.Parameters.AddWithValue("fingerprint", fingerprint);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void RecordParameters(SqliteCommand command, EdgeScope scope, string id, Guid? snapshotId)
    {
        ScopeParameters(command, scope);
        command.Parameters.AddWithValue("id", id);
        if (snapshotId is not null)
        {
            command.Parameters.AddWithValue("snapshot", snapshotId.Value.ToString("N"));
        }
    }
}
