using BlueTusk.Streams;

namespace BlueTusk.Live.DependencyInjection;

public sealed class LiveInvalidationConsumer : IChangeStreamConsumer
{
    private readonly string _databaseIdentity;
    private readonly ILiveInvalidationSink _sink;

    public LiveInvalidationConsumer(
        string databaseIdentity,
        ILiveInvalidationSink sink)
    {
        BlueTuskGuard.NotNullOrWhiteSpace(databaseIdentity);
        BlueTuskGuard.NotNull(sink);
        _databaseIdentity = databaseIdentity;
        _sink = sink;
    }

    public ValueTask ResetSnapshotAsync(
        SnapshotReset reset,
        CancellationToken cancellationToken = default)
    {
        BlueTuskGuard.NotNull(reset);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask StartSnapshotAsync(
        SnapshotStart start,
        CancellationToken cancellationToken = default)
    {
        BlueTuskGuard.NotNull(start);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask ConsumeSnapshotBatchAsync(
        ChangeSnapshotBatch batch,
        CancellationToken cancellationToken = default)
    {
        BlueTuskGuard.NotNull(batch);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteSnapshotAsync(
        SnapshotComplete complete,
        CancellationToken cancellationToken = default)
    {
        BlueTuskGuard.NotNull(complete);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask ConsumeTransactionAsync(
        ChangeTransactionDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        BlueTuskGuard.NotNull(delivery);
        try
        {
            _ = await _sink.AppendAsync(
                _databaseIdentity,
                delivery.Transaction,
                cancellationToken).ConfigureAwait(false);
            await delivery.AcknowledgeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (delivery.State is ChangeDeliveryState.Active)
            {
                await delivery.NackAsync(exception, cancellationToken).ConfigureAwait(false);
            }

            throw;
        }
    }
}
