namespace BlueTusk.Projections;

public sealed partial class PostgreSqlProjectionStore
{
    /// <summary>Permanently fences an unpublished version. Its identity, retirement tombstone and checkpoint are retained.</summary>
    public async ValueTask RetireAsync(ProjectionLease lease, int expectedActiveVersion, CancellationToken cancellationToken = default)
    {
        ValidateLease(lease);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedActiveVersion);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockHeadAsync(connection, transaction, lease.Identity.Name, cancellationToken).ConfigureAwait(false);
        await using (var command = Command(connection, transaction, $"SELECT active_version FROM {_schema}.heads WHERE projection = @projection", ("projection", lease.Identity.Name)))
        {
            var active = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (active is null or DBNull || Convert.ToInt32(active, System.Globalization.CultureInfo.InvariantCulture) != expectedActiveVersion || lease.Identity.Version == expectedActiveVersion)
            {
                throw new InvalidOperationException("Retirement requires the expected different published version. The active version cannot be retired.");
            }
        }
        EnsureLease(lease, await ReadStateAsync(connection, transaction, lease.Identity, true, cancellationToken).ConfigureAwait(false));
        await using (var command = LeaseCommand(connection, transaction, lease, $"""
            UPDATE {_schema}.state SET owner_id = NULL, expires_at = NULL, fencing_token = fencing_token + 1
            WHERE projection = @projection AND version = @version AND owner_id = @owner AND fencing_token = @token AND expires_at > clock_timestamp()
            """))
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) { throw new ProjectionFencedException(); }
        }
        await ExecuteAsync(connection, transaction, $"INSERT INTO {_schema}.retired_versions(projection, version) VALUES(@projection,@version)",
            cancellationToken, ("projection", lease.Identity.Name), ("version", lease.Identity.Version)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        ProjectionsDiagnostics.Commit("retirement");
    }

    /// <summary>Deletes at most maximumRows derived rows from a permanently retired version. Identity tombstones are never pruned.</summary>
    public async ValueTask<ProjectionRetentionResult> PruneRetiredAsync(ProjectionIdentity identity, int maximumRows = 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRows);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumRows, 65_536);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = IdentityCommand(connection, transaction, identity, $"""
            SELECT r.version FROM {_schema}.retired_versions r JOIN {_schema}.state s USING(projection,version)
            WHERE r.projection=@projection AND r.version=@version AND s.definition_fingerprint=@definition AND s.source_fingerprint=@source FOR UPDATE OF r
            """))
        {
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                throw new InvalidOperationException("Only the exact permanently retired version may be pruned.");
            }
        }
        var progress = await DeleteDerivedRowsAsync(connection, transaction, identity, maximumRows, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        ProjectionsDiagnostics.Prune(progress.DeletedRows);
        return progress;
    }

    private async ValueTask<ProjectionRetentionResult> DeleteDerivedRowsAsync(System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction, ProjectionIdentity identity, int maximumRows, CancellationToken cancellationToken)
    {
        var deleted = 0;
        var remains = false;
        // Dependencies are explicitly drained before documents, so cascading deletes cannot exceed
        // the caller's total row budget. All table names are fixed runtime literals.
        foreach (var table in new[] { "dependencies", "documents", "source_rows", "aggregates", "snapshot_row_keys", "snapshot_batches", "snapshot_tables" })
        {
            if (deleted < maximumRows)
            {
                await using var command = Command(connection, transaction, $"""
                    WITH rows AS (SELECT ctid FROM {_schema}.{table} WHERE projection=@projection AND version=@version LIMIT @limit FOR UPDATE)
                    DELETE FROM {_schema}.{table} d USING rows r WHERE d.ctid = r.ctid
                    """, ("projection", identity.Name), ("version", identity.Version), ("limit", maximumRows - deleted));
                deleted += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using var check = Command(connection, transaction, $"SELECT EXISTS(SELECT 1 FROM {_schema}.{table} WHERE projection=@projection AND version=@version)",
                ("projection", identity.Name), ("version", identity.Version));
            if ((bool)(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
            {
                remains = true;
                // Never delete documents while some dependency rows still reference them.
                break;
            }
        }
        return new ProjectionRetentionResult(deleted, remains);
    }
}
