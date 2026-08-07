using BlueTusk.Streams.Storage.PostgreSql;

namespace BlueTusk.Sync.DependencyInjection;

/// <summary>Reads exact quarantined transactions from the durable PostgreSQL relay.</summary>
public sealed class PostgreSqlRelaySyncQuarantineReplaySource : ISyncQuarantineReplaySource
{
    private readonly PostgreSqlDurableChangeRelay _relay;

    public PostgreSqlRelaySyncQuarantineReplaySource(PostgreSqlDurableChangeRelay relay)
    {
        ArgumentNullException.ThrowIfNull(relay);
        _relay = relay;
    }

    public async ValueTask<Streams.ChangeTransaction?> ReadTransactionAsync(
        SyncQuarantineIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var record = await _relay.ReadRetainedTransactionAsync(
            identity.Source,
            identity.CommitEndPosition,
            identity.TransactionId,
            cancellationToken).ConfigureAwait(false);
        return record?.Transaction;
    }
}
