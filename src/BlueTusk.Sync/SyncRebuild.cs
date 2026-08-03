using BlueTusk.Streams;
using BlueTusk.TypeSystem;

namespace BlueTusk.Sync;

/// <summary>Identifies the current stage of a zero-downtime destination rebuild.</summary>
public enum SyncRebuildStage
{
    /// <summary>An isolated destination generation is being created or resumed.</summary>
    Preparing,

    /// <summary>A consistent source snapshot is being materialized.</summary>
    Snapshotting,

    /// <summary>Source transactions after the snapshot point are being applied.</summary>
    CatchingUp,

    /// <summary>The rebuilding generation is undergoing storage and authoritative verification.</summary>
    Verifying,

    /// <summary>Destination routing is being atomically moved to the rebuilding generation.</summary>
    Activating,

    /// <summary>The previous generation is being retired after activation.</summary>
    Retiring,

    /// <summary>The rebuild and any requested retirement completed.</summary>
    Completed,
}

/// <summary>Configures one bounded zero-downtime rebuild run.</summary>
public sealed record SyncRebuildOptions
{
    /// <summary>Gets the pipeline being rebuilt.</summary>
    public required string PipelineId { get; init; }

    /// <summary>Gets the maximum number of full snapshot attempts after exporter loss.</summary>
    public int MaximumSnapshotAttempts { get; init; } = 3;

    /// <summary>Gets whether the previously active generation is retired after activation.</summary>
    public bool RetirePreviousGeneration { get; init; }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(PipelineId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSnapshotAttempts);
    }
}

/// <summary>Reports restart-safe rebuild progress without changing execution semantics.</summary>
public sealed record SyncRebuildProgress(
    SyncRebuildStage Stage,
    Guid? SnapshotEpoch,
    BlueTuskLogSequenceNumber DurablePosition,
    long SnapshotRows,
    long SnapshotBatches,
    long CatchUpTransactions,
    string? Detail = null);

/// <summary>Identifies the isolated destination state prepared for a rebuild.</summary>
public sealed record SyncRebuildPreparation
{
    /// <summary>Initializes validated opaque destination generation metadata.</summary>
    public SyncRebuildPreparation(string previousGeneration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previousGeneration);
        PreviousGeneration = previousGeneration;
    }

    /// <summary>Gets the previously active opaque destination generation token.</summary>
    public string PreviousGeneration { get; }
}

/// <summary>Reports whether a rebuilding generation is safe to activate.</summary>
public sealed record SyncRebuildVerification(bool IsMatch, string? Detail = null);

/// <summary>Contains the durable outcome of a completed zero-downtime rebuild.</summary>
public sealed record SyncRebuildResult(
    Guid SnapshotEpoch,
    BlueTuskLogSequenceNumber SnapshotPosition,
    BlueTuskLogSequenceNumber ActivatedPosition,
    long SnapshotRows,
    long SnapshotBatches,
    long CatchUpTransactions,
    string PreviousGeneration,
    bool PreviousGenerationRetired);

/// <summary>
/// Provides destination-owned generation preparation, verification, atomic activation, and
/// retirement. Implementations must make every operation restart-safe.
/// </summary>
public interface ISyncRebuildDestination
{
    /// <summary>Creates or resumes an isolated generation and returns the active generation token.</summary>
    ValueTask<SyncRebuildPreparation> PrepareRebuildAsync(
        SyncProvisionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Verifies that the rebuilding generation is ready for atomic activation.</summary>
    ValueTask<SyncRebuildVerification> VerifyRebuildReadyAsync(
        string pipelineId,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically activates the prepared generation.</summary>
    ValueTask ActivateRebuildAsync(
        string pipelineId,
        CancellationToken cancellationToken = default);

    /// <summary>Retires a previously active opaque generation token.</summary>
    ValueTask RetireRebuildGenerationAsync(
        string pipelineId,
        string generation,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Quiesces the active pipeline and captures the durable relay head that a rebuilding generation
/// must reach. The active pipeline must remain quiesced until the returned lease is disposed.
/// </summary>
public interface ISyncRebuildCutoverBarrier
{
    /// <summary>Acquires the cutover barrier after the consistent snapshot completes.</summary>
    ValueTask<ISyncRebuildCutoverLease> AcquireAsync(
        string pipelineId,
        SnapshotEpoch snapshotEpoch,
        CancellationToken cancellationToken = default);
}

/// <summary>Holds active delivery at a captured durable target until activation completes.</summary>
public interface ISyncRebuildCutoverLease : IAsyncDisposable
{
    /// <summary>Gets the exact durable relay commit-end position required before activation.</summary>
    BlueTuskLogSequenceNumber TargetPosition { get; }

    /// <summary>
    /// Commits the worker handoff after destination activation. Once invoked, disposal must never
    /// resume the previous transform worker, even if the handoff reports a failure.
    /// </summary>
    ValueTask CompleteHandoffAsync(
        BlueTuskLogSequenceNumber activatedPosition,
        CancellationToken cancellationToken = default);
}

/// <summary>Performs authoritative application-level verification before rebuild activation.</summary>
public interface ISyncRebuildVerifier
{
    /// <summary>Verifies the isolated generation using the requested transform's source of truth.</summary>
    ValueTask<SyncRebuildVerification> VerifyAsync(
        string pipelineId,
        ISyncDestination destination,
        CancellationToken cancellationToken = default);
}

/// <summary>Associates one exact-content reconciliation request with its authoritative reader.</summary>
public sealed class SyncRebuildReconciliation
{
    /// <summary>Initializes one collection verification.</summary>
    public SyncRebuildReconciliation(
        SyncReconciliationRequest request,
        ISyncReconciliationReader source)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(source);
        if (request.Mode is not SyncReconciliationMode.PartitionedContentHash || request.Repair)
        {
            throw new ArgumentException(
                "Rebuild verification requires non-repairing partitioned content-hash reconciliation.",
                nameof(request));
        }

        Request = request;
        Source = source;
    }

    /// <summary>Gets the exact-content request.</summary>
    public SyncReconciliationRequest Request { get; }

    /// <summary>Gets the authoritative reader for the requested transform.</summary>
    public ISyncReconciliationReader Source { get; }
}

/// <summary>Runs exact-content reconciliation for every registered rebuild collection.</summary>
public sealed class SyncReconciliationRebuildVerifier : ISyncRebuildVerifier
{
    private readonly System.Collections.ObjectModel.ReadOnlyCollection<SyncRebuildReconciliation>
        _reconciliations;

    /// <summary>Initializes a bounded authoritative verification set.</summary>
    public SyncReconciliationRebuildVerifier(
        IEnumerable<SyncRebuildReconciliation> reconciliations)
    {
        ArgumentNullException.ThrowIfNull(reconciliations);
        _reconciliations = Array.AsReadOnly(reconciliations.ToArray());
        if (_reconciliations.Count == 0)
        {
            throw new ArgumentException(
                "At least one authoritative collection reconciliation is required.",
                nameof(reconciliations));
        }
    }

    /// <inheritdoc />
    public async ValueTask<SyncRebuildVerification> VerifyAsync(
        string pipelineId,
        ISyncDestination destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        ArgumentNullException.ThrowIfNull(destination);
        foreach (var reconciliation in _reconciliations)
        {
            if (!string.Equals(
                    reconciliation.Request.PipelineId,
                    pipelineId,
                    StringComparison.Ordinal))
            {
                return new SyncRebuildVerification(
                    false,
                    $"Reconciliation pipeline '{reconciliation.Request.PipelineId}' does not match rebuild pipeline '{pipelineId}'.");
            }

            var result = await SyncReconciler.ReconcileAsync(
                reconciliation.Request,
                reconciliation.Source,
                destination,
                cancellationToken).ConfigureAwait(false);
            if (!result.IsMatch)
            {
                return new SyncRebuildVerification(
                    false,
                    $"Collection '{reconciliation.Request.Collection}' differs: {result.MissingFromDestination} missing, {result.ExtraInDestination} extra, and {result.ContentMismatches} content mismatches.");
            }
        }

        return new SyncRebuildVerification(true);
    }
}

/// <summary>Coordinates snapshot, catch-up, verification, cutover, and optional retirement.</summary>
public sealed class SyncRebuildCoordinator
{
    private readonly SyncRebuildOptions _options;
    private readonly ChangeSourceIdentity _sourceIdentity;
    private readonly IConsistentSnapshotSource _source;
    private readonly ISyncTransform _transform;
    private readonly ISyncDestination _destination;
    private readonly ISyncRebuildDestination _rebuildDestination;
    private readonly ISyncRebuildCutoverBarrier _cutoverBarrier;
    private readonly ISyncRebuildVerifier _verifier;
    private readonly IProgress<SyncRebuildProgress>? _progress;

    /// <summary>Initializes a destination-neutral rebuild coordinator.</summary>
    public SyncRebuildCoordinator(
        SyncRebuildOptions options,
        ChangeSourceIdentity sourceIdentity,
        IConsistentSnapshotSource source,
        ISyncTransform transform,
        ISyncDestination destination,
        ISyncRebuildCutoverBarrier cutoverBarrier,
        ISyncRebuildVerifier verifier,
        IProgress<SyncRebuildProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ArgumentNullException.ThrowIfNull(sourceIdentity);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(cutoverBarrier);
        ArgumentNullException.ThrowIfNull(verifier);
        if (!destination.Capabilities.HasFlag(SyncDestinationCapabilities.AliasSwap) ||
            destination is not ISyncRebuildDestination rebuildDestination)
        {
            throw new ArgumentException(
                $"Destination '{destination.Name}' does not expose restart-safe generation rebuilds and atomic routing swaps.",
                nameof(destination));
        }

        _options = options;
        _sourceIdentity = sourceIdentity;
        _source = source;
        _transform = transform;
        _destination = destination;
        _rebuildDestination = rebuildDestination;
        _cutoverBarrier = cutoverBarrier;
        _verifier = verifier;
        _progress = progress;
    }

    /// <summary>Runs or safely resumes a rebuild through atomic activation.</summary>
    public async Task<SyncRebuildResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        Report(SyncRebuildStage.Preparing, null, default, 0, 0, 0);
        var request = new SyncProvisionRequest(
            _options.PipelineId,
            _sourceIdentity,
            _transform.Version);
        var preparation = await _rebuildDestination.PrepareRebuildAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(preparation);

        Guid? abandonedEpoch = null;
        SnapshotOutcome? outcome = null;
        ISyncRebuildCutoverLease? cutoverLease = null;
        for (var attemptNumber = 1;
             attemptNumber <= _options.MaximumSnapshotAttempts;
             attemptNumber++)
        {
            IConsistentSnapshotAttempt? attempt = null;
            var snapshotComplete = false;
            try
            {
                attempt = await _source.BeginAttemptAsync(
                    abandonedEpoch,
                    cancellationToken).ConfigureAwait(false);
                await using var ownedAttempt = attempt;
                if (attempt.Epoch.Source != _sourceIdentity)
                {
                    throw new SyncRebuildException(
                        $"Snapshot epoch '{attempt.Epoch.Value}' belongs to source '{attempt.Epoch.Source.Fingerprint}', not '{_sourceIdentity.Fingerprint}'.");
                }

                Report(
                    SyncRebuildStage.Snapshotting,
                    attempt.Epoch.Value,
                    attempt.Epoch.ConsistentPosition,
                    0,
                    0,
                    0,
                    $"Snapshot attempt {attemptNumber}.");
                await _destination.ResetSnapshotAsync(
                    _options.PipelineId,
                    new SnapshotReset(
                        attempt.Epoch,
                        abandonedEpoch,
                        abandonedEpoch is null
                            ? "Zero-downtime rebuild snapshot."
                            : "The previous rebuild snapshot was abandoned after exporter loss."),
                    cancellationToken).ConfigureAwait(false);
                await _destination.StartSnapshotAsync(
                    _options.PipelineId,
                    new SnapshotStart(attempt.Epoch, attempt.Tables.Count),
                    _transform.Version,
                    cancellationToken).ConfigureAwait(false);

                long rows = 0;
                long batches = 0;
                await foreach (var batch in attempt.ReadSnapshotAsync(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    if (batch.Epoch != attempt.Epoch)
                    {
                        throw new SyncRebuildException(
                            "The rebuild snapshot source emitted a batch from a different epoch.");
                    }

                    var mutations = await _transform.TransformSnapshotBatchAsync(
                        batch,
                        cancellationToken).ConfigureAwait(false);
                    await _destination.ApplySnapshotBatchAsync(
                        new SyncSnapshotBatch(
                            _options.PipelineId,
                            _transform.Version,
                            batch,
                            mutations),
                        cancellationToken).ConfigureAwait(false);
                    rows = checked(rows + batch.Rows.Count);
                    batches = checked(batches + 1);
                    Report(
                        SyncRebuildStage.Snapshotting,
                        attempt.Epoch.Value,
                        attempt.Epoch.ConsistentPosition,
                        rows,
                        batches,
                        0);
                }

                await _destination.CompleteSnapshotAsync(
                    _options.PipelineId,
                    new SnapshotComplete(attempt.Epoch, rows, attempt.Tables.Count),
                    _transform.Version,
                    cancellationToken).ConfigureAwait(false);
                snapshotComplete = true;
                cutoverLease = await _cutoverBarrier.AcquireAsync(
                    _options.PipelineId,
                    attempt.Epoch,
                    cancellationToken).ConfigureAwait(false);
                ArgumentNullException.ThrowIfNull(cutoverLease);
                if (cutoverLease.TargetPosition.Value == 0 ||
                    cutoverLease.TargetPosition < attempt.Epoch.ConsistentPosition)
                {
                    await cutoverLease.DisposeAsync().ConfigureAwait(false);
                    cutoverLease = null;
                    throw new SyncRebuildException(
                        "The cutover barrier returned an invalid target before the snapshot's consistent position.");
                }

                CatchUpOutcome catchUp;
                try
                {
                    catchUp = await CatchUpAsync(
                        attempt,
                        cutoverLease.TargetPosition,
                        rows,
                        batches,
                        cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await cutoverLease.DisposeAsync().ConfigureAwait(false);
                    cutoverLease = null;
                    throw;
                }
                outcome = new SnapshotOutcome(
                    attempt.Epoch,
                    rows,
                    batches,
                    catchUp.Transactions,
                    catchUp.Position);
                break;
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

        var completed = outcome ??
            throw new SyncRebuildException("The rebuild snapshot did not produce an outcome.");
        await using var ownedCutoverLease = cutoverLease ??
            throw new SyncRebuildException("The rebuild did not retain its cutover barrier.");
        Report(
            SyncRebuildStage.Verifying,
            completed.Epoch.Value,
            completed.Position,
            completed.Rows,
            completed.Batches,
            completed.Transactions);
        var verification = await _rebuildDestination.VerifyRebuildReadyAsync(
            _options.PipelineId,
            cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(verification);
        if (!verification.IsMatch)
        {
            throw new SyncRebuildVerificationException(
                verification.Detail ??
                "The destination reported that the rebuilding generation does not match the active generation.");
        }

        var authoritativeVerification = await _verifier.VerifyAsync(
            _options.PipelineId,
            _destination,
            cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(authoritativeVerification);
        if (!authoritativeVerification.IsMatch)
        {
            throw new SyncRebuildVerificationException(
                authoritativeVerification.Detail ??
                "Authoritative reconciliation rejected the rebuilding generation.");
        }

        Report(
            SyncRebuildStage.Activating,
            completed.Epoch.Value,
            completed.Position,
            completed.Rows,
            completed.Batches,
            completed.Transactions);
        await _rebuildDestination.ActivateRebuildAsync(
            _options.PipelineId,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await ownedCutoverLease.CompleteHandoffAsync(
                completed.Position,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new SyncRebuildCutoverException(completed.Position, exception);
        }

        var retired = false;
        if (_options.RetirePreviousGeneration)
        {
            Report(
                SyncRebuildStage.Retiring,
                completed.Epoch.Value,
                completed.Position,
                completed.Rows,
                completed.Batches,
                completed.Transactions);
            try
            {
                await _rebuildDestination.RetireRebuildGenerationAsync(
                    _options.PipelineId,
                    preparation.PreviousGeneration,
                    cancellationToken).ConfigureAwait(false);
                retired = true;
            }
            catch (Exception exception)
            {
                throw new SyncRebuildRetirementException(
                    preparation.PreviousGeneration,
                    exception);
            }
        }

        Report(
            SyncRebuildStage.Completed,
            completed.Epoch.Value,
            completed.Position,
            completed.Rows,
            completed.Batches,
            completed.Transactions);
        return new SyncRebuildResult(
            completed.Epoch.Value,
            completed.Epoch.ConsistentPosition,
            completed.Position,
            completed.Rows,
            completed.Batches,
            completed.Transactions,
            preparation.PreviousGeneration,
            retired);
    }

    private async ValueTask<CatchUpOutcome> CatchUpAsync(
        IConsistentSnapshotAttempt attempt,
        BlueTuskLogSequenceNumber targetPosition,
        long rows,
        long batches,
        CancellationToken cancellationToken)
    {
        var position = attempt.Epoch.ConsistentPosition;
        long transactions = 0;
        if (position >= targetPosition)
        {
            return new CatchUpOutcome(position, transactions);
        }

        Report(
            SyncRebuildStage.CatchingUp,
            attempt.Epoch.Value,
            position,
            rows,
            batches,
            transactions);
        await foreach (var delivery in attempt.CreateChangeStream()
                           .ReadTransactionsAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            await using var ownedDelivery = delivery;
            try
            {
                if (delivery.Transaction.Source != _sourceIdentity)
                {
                    throw new SyncRebuildException(
                        "The rebuild catch-up stream emitted a transaction from another source.");
                }

                if (delivery.Transaction.CommitEndPosition < position)
                {
                    throw new SyncRebuildException(
                        $"The rebuild catch-up stream moved backward from '{position}' to '{delivery.Transaction.CommitEndPosition}'.");
                }

                if (delivery.Transaction.CommitEndPosition > targetPosition)
                {
                    throw new SyncRebuildException(
                        $"The rebuild catch-up stream passed captured target '{targetPosition}' at '{delivery.Transaction.CommitEndPosition}'. The cutover barrier must return an exact transaction commit-end position.");
                }

                var mutations = await _transform.TransformTransactionAsync(
                    delivery.Transaction,
                    cancellationToken).ConfigureAwait(false);
                var result = await _destination.ApplyTransactionAsync(
                    new SyncTransactionBatch(
                        _options.PipelineId,
                        _transform.Version,
                        delivery.Transaction,
                        mutations),
                    cancellationToken).ConfigureAwait(false);
                ValidateDurableResult(result, delivery.Transaction.CommitEndPosition);
                await delivery.AcknowledgeAsync(cancellationToken).ConfigureAwait(false);
                position = delivery.Transaction.CommitEndPosition;
                transactions = checked(transactions + 1);
                Report(
                    SyncRebuildStage.CatchingUp,
                    attempt.Epoch.Value,
                    position,
                    rows,
                    batches,
                    transactions);
            }
            catch (Exception exception)
            {
                if (delivery.State is ChangeDeliveryState.Active)
                {
                    await delivery.NackAsync(exception, cancellationToken).ConfigureAwait(false);
                }

                throw;
            }

            if (position == targetPosition)
            {
                return new CatchUpOutcome(position, transactions);
            }
        }

        throw new SyncRebuildCatchUpException(position, targetPosition);
    }

    private void Report(
        SyncRebuildStage stage,
        Guid? epoch,
        BlueTuskLogSequenceNumber position,
        long rows,
        long batches,
        long transactions,
        string? detail = null)
    {
        if (_progress is null)
        {
            return;
        }

        try
        {
            _progress.Report(new SyncRebuildProgress(
                stage,
                epoch,
                position,
                rows,
                batches,
                transactions,
                detail));
        }
        catch
        {
            // Progress is observational and must never change rebuild durability semantics.
        }
    }

    private void ValidateDurableResult(
        SyncApplyResult result,
        BlueTuskLogSequenceNumber expectedPosition)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Status is SyncApplyStatus.TransformVersionMismatch)
        {
            throw new SyncTransformVersionMismatchException(
                result.Detail ?? "unknown",
                _transform.Version.Fingerprint);
        }

        if (result.Status is not (SyncApplyStatus.Applied or SyncApplyStatus.AlreadyApplied) ||
            result.DurablePosition != expectedPosition)
        {
            throw new SyncDestinationDurabilityException(
                $"The rebuild destination confirmed status '{result.Status}' at position '{result.DurablePosition}' instead of source position '{expectedPosition}'.");
        }
    }

    private sealed record SnapshotOutcome(
        SnapshotEpoch Epoch,
        long Rows,
        long Batches,
        long Transactions,
        BlueTuskLogSequenceNumber Position);

    private sealed record CatchUpOutcome(
        BlueTuskLogSequenceNumber Position,
        long Transactions);
}

/// <summary>Represents a zero-downtime rebuild protocol failure.</summary>
public class SyncRebuildException : Exception
{
    /// <summary>Initializes a rebuild failure.</summary>
    public SyncRebuildException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a rebuild failure with its cause.</summary>
    public SyncRebuildException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Indicates that a finite catch-up stream ended before the cutover target.</summary>
public sealed class SyncRebuildCatchUpException : SyncRebuildException
{
    /// <summary>Initializes an incomplete catch-up failure.</summary>
    public SyncRebuildCatchUpException(
        BlueTuskLogSequenceNumber reachedPosition,
        BlueTuskLogSequenceNumber targetPosition)
        : base($"The rebuild catch-up stream ended at '{reachedPosition}' before target '{targetPosition}'.")
    {
        ReachedPosition = reachedPosition;
        TargetPosition = targetPosition;
    }

    /// <summary>Gets the last durably applied position.</summary>
    public BlueTuskLogSequenceNumber ReachedPosition { get; }

    /// <summary>Gets the required cutover target.</summary>
    public BlueTuskLogSequenceNumber TargetPosition { get; }
}

/// <summary>Indicates that destination verification rejected atomic activation.</summary>
public sealed class SyncRebuildVerificationException : SyncRebuildException
{
    /// <summary>Initializes a verification failure.</summary>
    public SyncRebuildVerificationException(string message)
        : base(message)
    {
    }
}

/// <summary>Indicates that activation succeeded but retirement requires operator retry.</summary>
public sealed class SyncRebuildRetirementException : SyncRebuildException
{
    /// <summary>Initializes a post-activation retirement failure.</summary>
    public SyncRebuildRetirementException(string generation, Exception innerException)
        : base(
            $"The rebuilding generation was activated, but previous generation '{generation}' could not be retired. Activation must not be rolled back; retry retirement explicitly.",
            innerException)
    {
        Generation = generation;
        ActivationCompleted = true;
    }

    /// <summary>Gets the previous generation requiring retirement.</summary>
    public string Generation { get; }

    /// <summary>Gets whether activation completed before the failure.</summary>
    public bool ActivationCompleted { get; }
}

/// <summary>Indicates that activation succeeded but the worker handoff needs operator recovery.</summary>
public sealed class SyncRebuildCutoverException : SyncRebuildException
{
    /// <summary>Initializes a post-activation worker-handoff failure.</summary>
    public SyncRebuildCutoverException(
        BlueTuskLogSequenceNumber activatedPosition,
        Exception innerException)
        : base(
            $"The rebuilding generation was activated at '{activatedPosition}', but the worker handoff did not complete. The previous transform worker must remain quiesced and activation must not be rolled back.",
            innerException)
    {
        ActivatedPosition = activatedPosition;
        ActivationCompleted = true;
    }

    /// <summary>Gets the activated durable position requiring handoff recovery.</summary>
    public BlueTuskLogSequenceNumber ActivatedPosition { get; }

    /// <summary>Gets whether destination activation completed before the failure.</summary>
    public bool ActivationCompleted { get; }
}
