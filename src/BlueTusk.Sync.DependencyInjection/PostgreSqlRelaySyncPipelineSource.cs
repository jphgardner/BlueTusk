using BlueTusk.Streams;
using BlueTusk.Streams.Storage.PostgreSql;

namespace BlueTusk.Sync.DependencyInjection;

/// <summary>
/// Runs a restart-aware snapshot and CDC lifecycle protected by a PostgreSQL relay group.
/// </summary>
public sealed class PostgreSqlRelaySyncPipelineSource : ISyncPipelineSource
{
    private readonly PostgreSqlDurableChangeRelay _relay;
    private readonly ChangeSourceIdentity _source;
    private readonly IConsistentSnapshotSource _snapshotSource;
    private readonly SyncTransformVersion _transform;
    private readonly PostgreSqlRelayChangeStreamOptions _relayOptions;
    private readonly SnapshotThenStreamOptions _snapshotOptions;

    /// <summary>Initializes a durable relay source for one independently checkpointed pipeline.</summary>
    public PostgreSqlRelaySyncPipelineSource(
        PostgreSqlDurableChangeRelay relay,
        ChangeSourceIdentity source,
        IConsistentSnapshotSource snapshotSource,
        SyncTransformVersion transform,
        PostgreSqlRelayChangeStreamOptions relayOptions,
        SnapshotThenStreamOptions? snapshotOptions = null)
    {
        _relay = relay ?? throw new ArgumentNullException(nameof(relay));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _snapshotSource = snapshotSource ?? throw new ArgumentNullException(nameof(snapshotSource));
        _transform = transform ?? throw new ArgumentNullException(nameof(transform));
        _relayOptions = relayOptions ?? throw new ArgumentNullException(nameof(relayOptions));
        _snapshotOptions = snapshotOptions ?? new SnapshotThenStreamOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            _snapshotOptions.MaximumSnapshotAttempts);
    }

    /// <inheritdoc />
    public async Task RunAsync(
        IChangeStreamConsumer consumer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        await _relay.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var registration = await _relay.RegisterSourceAsync(
            _source,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var session = await PostgreSqlRelayConsumerGroupSession.AcquireAsync(
            _relay,
            registration,
            _relayOptions,
            cancellationToken).ConfigureAwait(false);
        using var deliveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            session.LeaseLostToken);
        try
        {
            var latest = await _relay.GetLatestSnapshotRunAsync(
                session.Lease,
                _transform.Fingerprint,
                deliveryCancellation.Token).ConfigureAwait(false);
            var completed = latest is { State: ChangeRelaySnapshotRunState.Completed }
                ? latest
                : await BootstrapAsync(
                    consumer,
                    session,
                    latest?.SnapshotEpoch,
                    deliveryCancellation.Token).ConfigureAwait(false);
            await ConsumeRelayAsync(
                consumer,
                session,
                completed.ConsistentPosition,
                deliveryCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            session.LeaseLostToken.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            session.EnsureLeaseActive();
            throw;
        }
    }

    private async Task<ChangeRelaySnapshotRun> BootstrapAsync(
        IChangeStreamConsumer consumer,
        PostgreSqlRelayConsumerGroupSession session,
        Guid? abandonedEpoch,
        CancellationToken cancellationToken)
    {
        for (var attemptNumber = 1;
             attemptNumber <= _snapshotOptions.MaximumSnapshotAttempts;
             attemptNumber++)
        {
            IConsistentSnapshotAttempt? attempt = null;
            var destinationCompleted = false;
            try
            {
                attempt = await _snapshotSource.BeginAttemptAsync(
                    abandonedEpoch,
                    cancellationToken).ConfigureAwait(false);
                await using var ownedAttempt = attempt;
                if (attempt.Epoch.Source != _source)
                {
                    throw new SnapshotAttemptException(
                        $"Snapshot epoch '{attempt.Epoch.Value}' belongs to source '{attempt.Epoch.Source.Fingerprint}', not '{_source.Fingerprint}'.");
                }

                var run = await _relay.BeginSnapshotRunAsync(
                    session.Lease,
                    attempt.Epoch,
                    _transform.Fingerprint,
                    cancellationToken).ConfigureAwait(false);
                await consumer.ResetSnapshotAsync(
                    new SnapshotReset(
                        attempt.Epoch,
                        abandonedEpoch,
                        abandonedEpoch is null
                            ? "Initial relay-protected Sync snapshot."
                            : "The previous relay-protected snapshot epoch was abandoned."),
                    cancellationToken).ConfigureAwait(false);
                await consumer.StartSnapshotAsync(
                    new SnapshotStart(attempt.Epoch, attempt.Tables.Count),
                    cancellationToken).ConfigureAwait(false);

                long rowCount = 0;
                await foreach (var batch in attempt.ReadSnapshotAsync(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    if (batch.Epoch != attempt.Epoch)
                    {
                        throw new SnapshotAttemptException(
                            "The snapshot source emitted a batch from a different epoch.");
                    }

                    session.EnsureLeaseActive();
                    rowCount = checked(rowCount + batch.Rows.Count);
                    await consumer.ConsumeSnapshotBatchAsync(batch, cancellationToken)
                        .ConfigureAwait(false);
                }

                await consumer.CompleteSnapshotAsync(
                    new SnapshotComplete(attempt.Epoch, rowCount, attempt.Tables.Count),
                    cancellationToken).ConfigureAwait(false);
                destinationCompleted = true;
                return await _relay.CompleteSnapshotRunAsync(
                    session.Lease,
                    run,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SnapshotSessionLostException exception) when (!destinationCompleted)
            {
                abandonedEpoch = attempt?.Epoch.Value ?? abandonedEpoch;
                if (attemptNumber == _snapshotOptions.MaximumSnapshotAttempts)
                {
                    throw new SnapshotRestartLimitExceededException(
                        attemptNumber,
                        abandonedEpoch,
                        exception);
                }
            }
        }

        throw new SnapshotAttemptException("The relay-protected snapshot did not complete.");
    }

    private static async Task ConsumeRelayAsync(
        IChangeStreamConsumer consumer,
        PostgreSqlRelayConsumerGroupSession session,
        BlueTusk.TypeSystem.BlueTuskLogSequenceNumber snapshotPosition,
        CancellationToken cancellationToken)
    {
        await foreach (var delivery in session
            .CreateChangeStream()
            .ReadTransactionsAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            if (delivery.Transaction.CommitEndPosition <= snapshotPosition)
            {
                await delivery.AcknowledgeAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            await consumer.ConsumeTransactionAsync(delivery, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
