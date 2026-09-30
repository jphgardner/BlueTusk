using BlueTusk.Data;

namespace BlueTusk.Jobs;

public sealed record FencedJobTransactionResult<T>(bool Executed, T? Value);

public sealed partial class PostgreSqlJobStore
{
    /// <summary>Commits colocated database effects only while the job lease remains valid on the database clock.</summary>
    /// <remarks>The callback must contain only transactional database effects. External effects cannot be rolled back.</remarks>
    public async ValueTask<FencedJobTransactionResult<T>> ExecuteFencedAsync<T>(
        JobLease lease,
        Func<BlueTuskConnection, BlueTuskTransaction, CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken = default)
    {
        ValidateLease(lease);
        ArgumentNullException.ThrowIfNull(action);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = CreateCommand(connection, transaction, $"""
            SELECT id FROM {_jobs} WHERE tenant = @tenant AND queue = @queue AND id = @id AND status = 1
                AND lease_owner = @owner AND fencing_token = @token AND lease_expires > clock_timestamp()
            FOR UPDATE
            """))
        {
            AddLease(command, lease);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not Guid)
            {
                _ = RecordFencing(false);
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new(false, default);
            }
        }

        T value = await action(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using (var command = CreateCommand(connection, transaction, $"""
            SELECT lease_expires > clock_timestamp() FROM {_jobs}
            WHERE tenant = @tenant AND queue = @queue AND id = @id AND status = 1 AND lease_owner = @owner AND fencing_token = @token
            """))
        {
            AddLease(command, lease);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                _ = RecordFencing(false);
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new(false, default);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(true, value);
    }
}
