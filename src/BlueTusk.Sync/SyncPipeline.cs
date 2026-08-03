using BlueTusk.Streams;

namespace BlueTusk.Sync;

public sealed record SyncPipelineOptions
{
    public required string PipelineId { get; init; }

    public SyncPoisonRecordPolicy PoisonRecordPolicy { get; init; } =
        SyncPoisonRecordPolicy.Pause;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(PipelineId);
        if (!Enum.IsDefined(PoisonRecordPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(PoisonRecordPolicy));
        }
    }
}

public sealed record SyncPipelineStatus(
    string PipelineId,
    SyncPipelineState State,
    long AppliedTransactions,
    long AppliedSnapshotBatches,
    long QuarantinedTransactions,
    DateTimeOffset? LastTransitionAt,
    string? LastError);

public sealed class SyncPipeline : IChangeStreamConsumer, IAsyncDisposable
{
    private readonly SyncPipelineOptions _options;
    private readonly ChangeSourceIdentity _source;
    private readonly ISyncTransform _transform;
    private readonly ISyncDestination _destination;
    private readonly ISyncQuarantineSink? _quarantine;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _state = (int)SyncPipelineState.Stopped;
    private long _appliedTransactions;
    private long _appliedSnapshotBatches;
    private long _quarantinedTransactions;
    private long _lastTransitionUtcTicks = long.MinValue;
    private string? _lastError;
    private int _disposed;

    public SyncPipeline(
        SyncPipelineOptions options,
        ChangeSourceIdentity source,
        ISyncTransform transform,
        ISyncDestination destination,
        ISyncQuarantineSink? quarantine = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentNullException.ThrowIfNull(destination);
        if (options.PoisonRecordPolicy is SyncPoisonRecordPolicy.QuarantineAndAdvance &&
            quarantine is null)
        {
            throw new ArgumentException(
                "Quarantine-and-advance requires a durable quarantine sink.",
                nameof(quarantine));
        }

        _options = options;
        _source = source;
        _transform = transform;
        _destination = destination;
        _quarantine = quarantine;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public SyncPipelineStatus Status
    {
        get
        {
            var transitionTicks = Interlocked.Read(ref _lastTransitionUtcTicks);
            return new SyncPipelineStatus(
                _options.PipelineId,
                (SyncPipelineState)Volatile.Read(ref _state),
                Interlocked.Read(ref _appliedTransactions),
                Interlocked.Read(ref _appliedSnapshotBatches),
                Interlocked.Read(ref _quarantinedTransactions),
                transitionTicks == long.MinValue
                    ? null
                    : new DateTimeOffset(transitionTicks, TimeSpan.Zero),
                Volatile.Read(ref _lastError));
        }
    }

    public async ValueTask ProvisionAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(SyncPipelineState.Stopped);
            TransitionTo(SyncPipelineState.Provisioning);
            try
            {
                var result = await _destination.ProvisionAsync(
                    new SyncProvisionRequest(_options.PipelineId, _source, _transform.Version),
                    cancellationToken).ConfigureAwait(false);
                ArgumentNullException.ThrowIfNull(result);
                if (!Enum.IsDefined(result.Status))
                {
                    throw new SyncDestinationDurabilityException(
                        $"Destination '{_destination.Name}' returned an unknown provision status.");
                }

                if (result.Status is SyncProvisionStatus.RebuildRequired)
                {
                    TransitionTo(SyncPipelineState.Rebuilding);
                    throw new SyncTransformVersionMismatchException(
                        result.ExistingTransformFingerprint ?? "unknown",
                        _transform.Version.Fingerprint);
                }

                TransitionTo(SyncPipelineState.Running);
            }
            catch (SyncTransformVersionMismatchException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Fault(exception);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask PauseAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(
                SyncPipelineState.Running,
                SyncPipelineState.CatchingUp,
                SyncPipelineState.Snapshotting,
                SyncPipelineState.Faulted);
            TransitionTo(SyncPipelineState.Paused);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ResumeAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(SyncPipelineState.Paused, SyncPipelineState.Faulted);
            TransitionTo(SyncPipelineState.Running);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TransitionTo(SyncPipelineState.Stopped);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<SyncReconciliationResult> ReconcileAsync(
        SyncReconciliationRequest request,
        ISyncReconciliationReader source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(source);
        if (!string.Equals(request.PipelineId, _options.PipelineId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Reconciliation pipeline '{request.PipelineId}' does not match '{_options.PipelineId}'.",
                nameof(request));
        }

        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previousState = (SyncPipelineState)Volatile.Read(ref _state);
            RequireState(SyncPipelineState.Running, SyncPipelineState.Paused);
            TransitionTo(SyncPipelineState.Reconciling);
            try
            {
                var result = await SyncReconciler.ReconcileAsync(
                    request,
                    source,
                    _destination,
                    cancellationToken).ConfigureAwait(false);
                TransitionTo(previousState);
                return result;
            }
            catch (Exception exception)
            {
                Fault(exception);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ResetSnapshotAsync(
        SnapshotReset reset,
        CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(
                SyncPipelineState.Running,
                SyncPipelineState.CatchingUp,
                SyncPipelineState.Snapshotting,
                SyncPipelineState.Paused,
                SyncPipelineState.Faulted);
            TransitionTo(SyncPipelineState.Snapshotting);
            await _destination.ResetSnapshotAsync(
                _options.PipelineId,
                reset,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Fault(exception);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask StartSnapshotAsync(
        SnapshotStart start,
        CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(SyncPipelineState.Snapshotting);
            await _destination.StartSnapshotAsync(
                _options.PipelineId,
                start,
                _transform.Version,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Fault(exception);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ConsumeSnapshotBatchAsync(
        ChangeSnapshotBatch batch,
        CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(SyncPipelineState.Snapshotting);
            var mutations = await _transform.TransformSnapshotBatchAsync(
                batch,
                cancellationToken).ConfigureAwait(false);
            await _destination.ApplySnapshotBatchAsync(
                new SyncSnapshotBatch(_options.PipelineId, _transform.Version, batch, mutations),
                cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _appliedSnapshotBatches);
        }
        catch (Exception exception)
        {
            Fault(exception);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask CompleteSnapshotAsync(
        SnapshotComplete complete,
        CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(SyncPipelineState.Snapshotting);
            await _destination.CompleteSnapshotAsync(
                _options.PipelineId,
                complete,
                _transform.Version,
                cancellationToken).ConfigureAwait(false);
            TransitionTo(SyncPipelineState.CatchingUp);
        }
        catch (Exception exception)
        {
            Fault(exception);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ConsumeTransactionAsync(
        ChangeTransactionDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(SyncPipelineState.Running, SyncPipelineState.CatchingUp);
            IReadOnlyList<SyncMutation> mutations;
            try
            {
                mutations = await _transform.TransformTransactionAsync(
                    delivery.Transaction,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SyncPoisonRecordException exception)
            {
                await HandlePoisonAsync(delivery, exception, cancellationToken).ConfigureAwait(false);
                return;
            }

            SyncApplyResult result;
            try
            {
                result = await _destination.ApplyTransactionAsync(
                    new SyncTransactionBatch(
                        _options.PipelineId,
                        _transform.Version,
                        delivery.Transaction,
                        mutations),
                    cancellationToken).ConfigureAwait(false);
                ValidateDurableResult(result, delivery.Transaction.CommitEndPosition);
            }
            catch (Exception exception)
            {
                await NackAndTransitionAsync(
                    delivery,
                    exception,
                    exception is SyncTransformVersionMismatchException
                        ? SyncPipelineState.Rebuilding
                        : SyncPipelineState.Faulted,
                    cancellationToken).ConfigureAwait(false);
                throw;
            }

            try
            {
                await delivery.AcknowledgeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Fault(exception);
                throw;
            }
            Interlocked.Increment(ref _appliedTransactions);
            if (Status.State is SyncPipelineState.CatchingUp)
            {
                TransitionTo(SyncPipelineState.Running);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _gate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async ValueTask HandlePoisonAsync(
        ChangeTransactionDelivery delivery,
        SyncPoisonRecordException exception,
        CancellationToken cancellationToken)
    {
        if (_options.PoisonRecordPolicy is SyncPoisonRecordPolicy.QuarantineAndAdvance)
        {
            bool stored;
            try
            {
                stored = await _quarantine!.StoreAsync(
                    new SyncQuarantineRecord(
                        _options.PipelineId,
                        _transform.Version,
                        delivery.Transaction.Source,
                        delivery.Transaction.TransactionId,
                        delivery.Transaction.CommitEndPosition,
                        exception.GetType().FullName ?? exception.GetType().Name,
                        exception.Message,
                        _timeProvider.GetUtcNow()),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception quarantineFailure)
            {
                await NackAndTransitionAsync(
                    delivery,
                    quarantineFailure,
                    SyncPipelineState.Faulted,
                    cancellationToken).ConfigureAwait(false);
                throw;
            }

            if (!stored)
            {
                var failure = new SyncDestinationDurabilityException(
                    "The quarantine sink did not confirm durable storage.");
                await NackAndTransitionAsync(
                    delivery,
                    failure,
                    SyncPipelineState.Faulted,
                    cancellationToken).ConfigureAwait(false);
                throw failure;
            }

            try
            {
                await delivery.AcknowledgeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception acknowledgeFailure)
            {
                Fault(acknowledgeFailure);
                throw;
            }

            Interlocked.Increment(ref _quarantinedTransactions);
            if (Status.State is SyncPipelineState.CatchingUp)
            {
                TransitionTo(SyncPipelineState.Running);
            }

            return;
        }

        await NackAndTransitionAsync(
            delivery,
            exception,
            SyncPipelineState.Paused,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask NackAndTransitionAsync(
        ChangeTransactionDelivery delivery,
        Exception exception,
        SyncPipelineState state,
        CancellationToken cancellationToken)
    {
        try
        {
            await delivery.NackAsync(exception, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _lastError, exception.Message);
            TransitionTo(state, preserveError: true);
        }
    }

    private void ValidateDurableResult(
        SyncApplyResult result,
        BlueTusk.TypeSystem.BlueTuskLogSequenceNumber expectedPosition)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Status is SyncApplyStatus.TransformVersionMismatch)
        {
            throw new SyncTransformVersionMismatchException(
                result.Detail ?? "unknown",
                _transform.Version.Fingerprint);
        }

        if (result.Status is not (SyncApplyStatus.Applied or SyncApplyStatus.AlreadyApplied))
        {
            throw new SyncDestinationDurabilityException(
                result.Detail ?? "The destination rejected the source transaction.");
        }

        if (result.DurablePosition != expectedPosition)
        {
            throw new SyncDestinationDurabilityException(
                $"The destination confirmed position '{result.DurablePosition}' instead of source position '{expectedPosition}'.");
        }
    }

    private async ValueTask EnterAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void RequireState(params SyncPipelineState[] allowed)
    {
        var current = (SyncPipelineState)Volatile.Read(ref _state);
        if (!allowed.Contains(current))
        {
            throw new InvalidOperationException(
                $"Pipeline '{_options.PipelineId}' is {current}; expected {string.Join(" or ", allowed)}.");
        }
    }

    private void Fault(Exception exception)
    {
        Volatile.Write(ref _lastError, exception.Message);
        TransitionTo(SyncPipelineState.Faulted, preserveError: true);
    }

    private void TransitionTo(SyncPipelineState state, bool preserveError = false)
    {
        Volatile.Write(ref _state, (int)state);
        Interlocked.Exchange(ref _lastTransitionUtcTicks, _timeProvider.GetUtcNow().UtcDateTime.Ticks);
        if (!preserveError)
        {
            Volatile.Write(ref _lastError, null);
        }
    }
}
