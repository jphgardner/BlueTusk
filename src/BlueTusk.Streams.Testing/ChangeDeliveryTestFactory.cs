using System.Runtime.CompilerServices;
using BlueTusk.TypeSystem;

namespace BlueTusk.Streams.Testing;

public static class ChangeDeliveryTestFactory
{
    public static ChangeTransactionDelivery CreateCommitted(
        ChangeSourceIdentity source,
        uint transactionId,
        BlueTuskLogSequenceNumber commitEndPosition,
        IEnumerable<Change>? changes = null,
        IChangeDeliveryObserver? observer = null,
        DateTimeOffset? commitTimestamp = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var materialized = changes?.ToArray() ?? [];
        if (materialized.Any(change =>
                change.Id.Source != source ||
                change.Id.CommitEndPosition != commitEndPosition ||
                change.Id.TransactionId != transactionId))
        {
            throw new ArgumentException(
                "Every test change must retain the transaction identity.",
                nameof(changes));
        }

        var transaction = new ChangeTransaction(
            source,
            transactionId,
            commitEndPosition,
            commitEndPosition,
            commitEndPosition,
            commitTimestamp ?? DateTimeOffset.UtcNow,
            origin: null,
            isSynthetic: false,
            ChangeTransactionOutcome.Committed,
            globalTransactionId: null,
            new ChangeSet(
                materialized.Length,
                estimatedBytes: 0,
                isSpooled: false,
                cancellationToken => ReadChangesAsync(materialized, cancellationToken)));
        return new ChangeTransactionDelivery(
            transaction,
            cancellationToken => observer?.AcknowledgeAsync(transaction, cancellationToken) ??
                                 ValueTask.CompletedTask,
            (failure, cancellationToken) => observer?.NackAsync(transaction, failure, cancellationToken) ??
                                            ValueTask.CompletedTask);
    }

    private static async IAsyncEnumerable<Change> ReadChangesAsync(
        IEnumerable<Change> changes,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return change;
        }
    }
}
