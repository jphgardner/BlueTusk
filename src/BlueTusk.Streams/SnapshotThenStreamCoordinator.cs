namespace BlueTusk.Streams;

public interface IConsistentSnapshotSource
{
    ValueTask<IConsistentSnapshotAttempt> BeginAttemptAsync(
        Guid? abandonedEpoch,
        CancellationToken cancellationToken = default);
}

public interface IConsistentSnapshotAttempt : IAsyncDisposable
{
    SnapshotEpoch Epoch { get; }

    IReadOnlyList<ChangeTable> Tables { get; }

    IAsyncEnumerable<ChangeSnapshotBatch> ReadSnapshotAsync(
        CancellationToken cancellationToken = default);

    IChangeStream CreateChangeStream();
}

public sealed record SnapshotThenStreamOptions
{
    public int MaximumSnapshotAttempts { get; init; } = 3;

    internal void Validate() =>
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSnapshotAttempts);
}

public class SnapshotAttemptException : Exception
{
    public SnapshotAttemptException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class SnapshotSessionLostException : SnapshotAttemptException
{
    public SnapshotSessionLostException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class SnapshotRestartLimitExceededException : SnapshotAttemptException
{
    public SnapshotRestartLimitExceededException(
        int attempts,
        Guid? abandonedEpoch,
        Exception innerException)
        : base(
            abandonedEpoch is { } epoch
                ? $"Snapshot bootstrap failed after {attempts} attempts; epoch {epoch} was abandoned."
                : $"Snapshot bootstrap failed before an epoch could be established after {attempts} attempts.",
            innerException)
    {
        Attempts = attempts;
        AbandonedEpoch = abandonedEpoch;
    }

    public int Attempts { get; }

    public Guid? AbandonedEpoch { get; }
}

public sealed class SnapshotThenStreamCoordinator
{
    private readonly IConsistentSnapshotSource _source;
    private readonly SnapshotThenStreamOptions _options;

    public SnapshotThenStreamCoordinator(
        IConsistentSnapshotSource source,
        SnapshotThenStreamOptions? options = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _options = options ?? new SnapshotThenStreamOptions();
        _options.Validate();
    }

    public async Task RunAsync(
        IChangeStreamConsumer consumer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        Guid? abandonedEpoch = null;
        for (var attemptNumber = 1; attemptNumber <= _options.MaximumSnapshotAttempts; attemptNumber++)
        {
            IConsistentSnapshotAttempt? attempt = null;
            var snapshotComplete = false;
            try
            {
                attempt = await _source.BeginAttemptAsync(
                    abandonedEpoch,
                    cancellationToken).ConfigureAwait(false);
                await using var ownedAttempt = attempt;
                using var activity = BlueTuskStreamsDiagnostics.ActivitySource.StartActivity(
                    "bluetusk.streams.snapshot",
                    System.Diagnostics.ActivityKind.Consumer);
                activity?.SetTag("bluetusk.source", attempt.Epoch.Source.Fingerprint);
                activity?.SetTag("bluetusk.slot", attempt.Epoch.Source.SlotName);
                activity?.SetTag("bluetusk.snapshot.epoch", attempt.Epoch.Value);
                activity?.SetTag("bluetusk.snapshot.attempt", attemptNumber);
                await consumer.ResetSnapshotAsync(
                    new SnapshotReset(
                        attempt.Epoch,
                        abandonedEpoch,
                        abandonedEpoch is null
                            ? "Initial consistent snapshot."
                            : "The previous exported snapshot was abandoned after its session was lost."),
                    cancellationToken).ConfigureAwait(false);
                await consumer.StartSnapshotAsync(
                    new SnapshotStart(attempt.Epoch, attempt.Tables.Count),
                    cancellationToken).ConfigureAwait(false);

                long rowCount = 0;
                await foreach (var batch in attempt.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (batch.Epoch != attempt.Epoch)
                    {
                        throw new SnapshotAttemptException(
                            "The snapshot source emitted a batch from a different epoch.");
                    }

                    rowCount = checked(rowCount + batch.Rows.Count);
                    BlueTuskStreamsDiagnostics.RecordSnapshotBatch(batch);
                    await consumer.ConsumeSnapshotBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                }

                await consumer.CompleteSnapshotAsync(
                    new SnapshotComplete(attempt.Epoch, rowCount, attempt.Tables.Count),
                    cancellationToken).ConfigureAwait(false);
                snapshotComplete = true;
                activity?.SetTag("bluetusk.snapshot.rows", rowCount);
                activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);

                await foreach (var delivery in attempt
                    .CreateChangeStream()
                    .ReadTransactionsAsync(cancellationToken)
                    .ConfigureAwait(false))
                {
                    await consumer.ConsumeTransactionAsync(delivery, cancellationToken).ConfigureAwait(false);
                }

                return;
            }
            catch (SnapshotSessionLostException exception) when (!snapshotComplete)
            {
                abandonedEpoch = attempt?.Epoch.Value ?? abandonedEpoch;
                if (attemptNumber == _options.MaximumSnapshotAttempts)
                {
                    throw new SnapshotRestartLimitExceededException(
                        attemptNumber,
                        abandonedEpoch,
                        exception);
                }
            }
        }
    }
}
