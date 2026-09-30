using System.Data.Common;
using System.Text;
using BlueTusk.Streams;

namespace BlueTusk.Documents.Streams;

/// <summary>The callback must durably commit all effects and causal IDs together before returning; redelivery remains possible.</summary>
public sealed class DocumentTransactionConsumer<T>
{
    private readonly DocumentChangeMapper<T> _mapper;
    private readonly Func<DocumentStreamTransaction<T>, CancellationToken, ValueTask> _apply;

    public DocumentTransactionConsumer(DocumentChangeMapper<T> mapper, Func<DocumentStreamTransaction<T>, CancellationToken, ValueTask> apply)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(apply);
        _mapper = mapper;
        _apply = apply;
    }

    public async ValueTask ConsumeAsync(ChangeTransactionDelivery delivery, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        try
        {
            var transaction = await _mapper.MapTransactionAsync(delivery.Transaction, cancellationToken).ConfigureAwait(false);
            if (transaction.Changes.Count != 0) { await _apply(transaction, cancellationToken).ConfigureAwait(false); }
            await delivery.AcknowledgeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (delivery.State is ChangeDeliveryState.Active)
            {
                // Cancellation must not prevent settlement of the upstream delivery.
                await delivery.NackAsync(exception, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }
}

public static class DocumentStreamDeployment
{
    /// <summary>Deployment operation that takes a PostgreSQL table lock and increases old-image WAL volume.</summary>
    public static async ValueTask EnableFullReplicaIdentityAsync(DbDataSource dataSource, string schema = "bluetusk_documents", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        if (schema.Contains('\0', StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(schema) > 63)
        {
            throw new ArgumentException("Schema must fit PostgreSQL's identifier byte limit and cannot contain NUL.", nameof(schema));
        }

        var quoted = '"' + schema.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
        await using var command = dataSource.CreateCommand($"ALTER TABLE {quoted}.documents REPLICA IDENTITY FULL");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
