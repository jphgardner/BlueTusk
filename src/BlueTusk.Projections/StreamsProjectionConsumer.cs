using BlueTusk.Streams;

namespace BlueTusk.Projections;

/// <summary>
/// Streams CDC/snapshot seam. The destination transaction commits before delivery acknowledgment.
/// Supply the registered ordered Streams source; no provider or EF persistence interception is used.
/// </summary>
public sealed class StreamsProjectionConsumer : IChangeStreamConsumer
{
    private readonly PostgreSqlProjectionStore _store;
    private readonly ProjectionLease _lease;
    private readonly IProjectionDefinition _definition;
    private readonly ProjectionPublishedRetentionTargetOptions? _protectedRetention;

    public StreamsProjectionConsumer(PostgreSqlProjectionStore store, ProjectionLease lease, IProjectionDefinition definition)
        : this(store, lease, definition, null)
    {
    }

    public static StreamsProjectionConsumer CreateProtected(PostgreSqlProjectionStore store,
        ProjectionLease lease, IProjectionDefinition definition,
        ProjectionPublishedRetentionTargetOptions protectedRetention)
    {
        ArgumentNullException.ThrowIfNull(protectedRetention);
        return new StreamsProjectionConsumer(store, lease, definition, protectedRetention);
    }

    private StreamsProjectionConsumer(PostgreSqlProjectionStore store, ProjectionLease lease,
        IProjectionDefinition definition, ProjectionPublishedRetentionTargetOptions? protectedRetention)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Identity != lease.Identity)
        {
            throw new ArgumentException("The Streams projection consumer requires its leased definition.", nameof(definition));
        }

        _store = store;
        _lease = lease;
        _definition = definition;
        _protectedRetention = protectedRetention;
    }

    public ValueTask ResetSnapshotAsync(SnapshotReset reset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reset);
        cancellationToken.ThrowIfCancellationRequested();
        if (reset.Epoch.Source != _lease.Identity.Source)
        {
            throw new InvalidOperationException("The reset belongs to another source.");
        }

        // Streams always follows reset with SnapshotStart carrying table coverage. StartSnapshotAsync
        // durably blocks the unpublished version and drives its bounded fenced reset before input.
        return ValueTask.CompletedTask;
    }

    public async ValueTask StartSnapshotAsync(SnapshotStart start, CancellationToken cancellationToken = default)
    {
        var pending = await _store.ReadSnapshotResetAsync(_lease.Identity, cancellationToken).ConfigureAwait(false);
        if (pending is not null && pending.Epoch.Value != start.Epoch.Value)
        { await _store.StartSnapshotAsync(_lease, pending, cancellationToken).ConfigureAwait(false); }
        await _store.StartSnapshotAsync(_lease, start, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ConsumeSnapshotBatchAsync(ChangeSnapshotBatch batch, CancellationToken cancellationToken = default) =>
        _ = await _store.ApplySnapshotAsync(_lease, _definition, batch, cancellationToken).ConfigureAwait(false);

    public ValueTask CompleteSnapshotAsync(SnapshotComplete complete, CancellationToken cancellationToken = default) =>
        _store.CompleteSnapshotAsync(_lease, complete, cancellationToken);

    public async ValueTask ConsumeTransactionAsync(ChangeTransactionDelivery delivery, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (_protectedRetention is null)
        {
            _ = await _store.ApplyAsync(_lease, _definition, delivery.Transaction, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _ = await _store.ApplyProtectedAsync(_lease, _definition, delivery,
                _protectedRetention, cancellationToken).ConfigureAwait(false);
        }
        // An acknowledgement failure can redeliver; the committed destination checkpoint deduplicates it.
        await delivery.AcknowledgeAsync(cancellationToken).ConfigureAwait(false);
    }
}
