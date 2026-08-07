using BlueTusk.Streams;

namespace BlueTusk.Sync;

/// <summary>Runs one restart-aware source lifecycle into a Sync pipeline consumer.</summary>
public interface ISyncPipelineSource
{
    /// <summary>Runs bootstrap or resume delivery until cancellation or a terminal source failure.</summary>
    Task RunAsync(
        IChangeStreamConsumer consumer,
        CancellationToken cancellationToken = default);
}

/// <summary>Adapts the standard Streams snapshot-then-stream lifecycle for direct sources.</summary>
public sealed class ConsistentSnapshotSyncPipelineSource : ISyncPipelineSource
{
    private readonly SnapshotThenStreamCoordinator _coordinator;

    /// <summary>Initializes a direct snapshot-then-stream source.</summary>
    public ConsistentSnapshotSyncPipelineSource(
        IConsistentSnapshotSource source,
        SnapshotThenStreamOptions? options = null)
    {
        _coordinator = new SnapshotThenStreamCoordinator(
            source ?? throw new ArgumentNullException(nameof(source)),
            options);
    }

    /// <inheritdoc />
    public Task RunAsync(
        IChangeStreamConsumer consumer,
        CancellationToken cancellationToken = default) =>
        _coordinator.RunAsync(consumer, cancellationToken);
}
