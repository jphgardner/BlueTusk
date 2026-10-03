using BlueTusk.Replication;
using BlueTusk.Streams;

namespace BlueTusk.Projections.LoadHarness;

/// <summary>
/// Sends WAL feedback only after the caller has committed projection state and transactional
/// inbox effects. The source's durable projection checkpoint and inbox remain authoritative;
/// a failed status write leaves the delivery unsettled for safe redelivery.
/// </summary>
internal sealed class DurableBoundaryFeedbackObserver(
    BlueTuskLogicalReplicationConnection connection,
    Action onFeedback) : IChangeDeliveryObserver
{
    private readonly LogicalReplicationFeedbackSender _sender = new(connection);

    public async ValueTask AcknowledgeAsync(ChangeTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        await _sender.SendFeedbackAsync(transaction.CommitEndPosition, cancellationToken);
        DeliveryTrace.RecordAck(System.Diagnostics.Stopwatch.GetElapsedTime(started));
        onFeedback();
    }

    public ValueTask NackAsync(ChangeTransaction transaction, Exception? failure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        // Nack, dispose and failed business transactions never advance the sender's flush point.
        return ValueTask.CompletedTask;
    }
}
