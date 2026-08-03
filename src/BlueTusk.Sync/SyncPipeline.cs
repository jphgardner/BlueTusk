using BlueTusk.Streams;

namespace BlueTusk.Sync;

public sealed record SyncPipelineOptions
{
    public required string PipelineId { get; init; }

    public SyncPoisonRecordPolicy PoisonRecordPolicy { get; init; } =
        SyncPoisonRecordPolicy.Pause;

    public SyncRetryOptions Retry { get; init; } = new();

    public SyncRateLimitOptions RateLimit { get; init; } = new();

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(PipelineId);
        if (!Enum.IsDefined(PoisonRecordPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(PoisonRecordPolicy));
        }

        ArgumentNullException.ThrowIfNull(Retry);
        ArgumentNullException.ThrowIfNull(RateLimit);
        Retry.Validate();
        RateLimit.Validate();
    }
}

public sealed record SyncPipelineStatus(
    string PipelineId,
    SyncPipelineState State,
    long AppliedTransactions,
    long AppliedSnapshotBatches,
    long QuarantinedTransactions,
    long RetryAttempts,
    TimeSpan ThrottleDelay,
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
    private readonly ISyncRetryClassifier? _retryClassifier;
    private readonly SyncDeliveryRateLimiter _rateLimiter;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _state = (int)SyncPipelineState.Stopped;
    private long _appliedTransactions;
    private long _appliedSnapshotBatches;
    private long _quarantinedTransactions;
    private long _retryAttempts;
    private long _throttleTicks;
    private long _lastTransitionUtcTicks = long.MinValue;
    private string? _lastError;
    private int _disposed;

    public SyncPipeline(
        SyncPipelineOptions options,
        ChangeSourceIdentity source,
        ISyncTransform transform,
        ISyncDestination destination,
        ISyncQuarantineSink? quarantine = null,
        ISyncRetryClassifier? retryClassifier = null,
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
        _retryClassifier = retryClassifier ?? destination as ISyncRetryClassifier;
        _rateLimiter = new SyncDeliveryRateLimiter(options.RateLimit, _timeProvider);
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
                Interlocked.Read(ref _retryAttempts),
                TimeSpan.FromTicks(Interlocked.Read(ref _throttleTicks)),
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
                var result = await ExecuteDestinationAsync(
                    SyncPipelineOperation.Provision,
                    transformedBytes: 0,
                    countTransaction: false,
                    token => _destination.ProvisionAsync(
                        new SyncProvisionRequest(_options.PipelineId, _source, _transform.Version),
                        token),
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
            await ExecuteDestinationAsync(
                SyncPipelineOperation.ResetSnapshot,
                token => _destination.ResetSnapshotAsync(_options.PipelineId, reset, token),
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
            await ExecuteDestinationAsync(
                SyncPipelineOperation.StartSnapshot,
                token => _destination.StartSnapshotAsync(
                    _options.PipelineId,
                    start,
                    _transform.Version,
                    token),
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
            var destinationBatch = new SyncSnapshotBatch(
                _options.PipelineId,
                _transform.Version,
                batch,
                mutations);
            await ExecuteDestinationAsync(
                SyncPipelineOperation.ApplySnapshotBatch,
                SyncDeliveryRateLimiter.EstimateBytes(mutations),
                countTransaction: false,
                token => _destination.ApplySnapshotBatchAsync(destinationBatch, token),
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
            await ExecuteDestinationAsync(
                SyncPipelineOperation.CompleteSnapshot,
                token => _destination.CompleteSnapshotAsync(
                    _options.PipelineId,
                    complete,
                    _transform.Version,
                    token),
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

            try
            {
                var destinationBatch = new SyncTransactionBatch(
                    _options.PipelineId,
                    _transform.Version,
                    delivery.Transaction,
                    mutations);
                _ = await ExecuteDestinationAsync(
                    SyncPipelineOperation.ApplyTransaction,
                    SyncDeliveryRateLimiter.EstimateBytes(mutations),
                    countTransaction: true,
                    async token =>
                    {
                        var attempt = await _destination.ApplyTransactionAsync(
                            destinationBatch,
                            token).ConfigureAwait(false);
                        ValidateDurableResult(
                            attempt,
                            delivery.Transaction.CommitEndPosition);
                        return attempt;
                    },
                    cancellationToken).ConfigureAwait(false);
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
                var record = new SyncQuarantineRecord(
                        _options.PipelineId,
                        _transform.Version,
                        delivery.Transaction.Source,
                        delivery.Transaction.TransactionId,
                        delivery.Transaction.CommitEndPosition,
                        exception.GetType().FullName ?? exception.GetType().Name,
                        exception.Message,
                        _timeProvider.GetUtcNow());
                stored = await ExecuteDestinationAsync(
                    SyncPipelineOperation.StoreQuarantine,
                    transformedBytes: 0,
                    countTransaction: false,
                    token => _quarantine!.StoreAsync(record, token),
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

    private async ValueTask ExecuteDestinationAsync(
        SyncPipelineOperation operation,
        Func<CancellationToken, ValueTask> action,
        CancellationToken cancellationToken)
    {
        await ExecuteDestinationAsync(
            operation,
            transformedBytes: 0,
            countTransaction: false,
            action,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ExecuteDestinationAsync(
        SyncPipelineOperation operation,
        long transformedBytes,
        bool countTransaction,
        Func<CancellationToken, ValueTask> action,
        CancellationToken cancellationToken)
    {
        _ = await ExecuteDestinationAsync(
            operation,
            transformedBytes,
            countTransaction,
            async token =>
            {
                await action(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TResult> ExecuteDestinationAsync<TResult>(
        SyncPipelineOperation operation,
        long transformedBytes,
        bool countTransaction,
        Func<CancellationToken, ValueTask<TResult>> action,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var throttle = await _rateLimiter.WaitAsync(
                transformedBytes,
                countTransaction,
                cancellationToken).ConfigureAwait(false);
            if (throttle > TimeSpan.Zero)
            {
                Interlocked.Add(ref _throttleTicks, throttle.Ticks);
            }

            try
            {
                return await action(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (cancellationToken.IsCancellationRequested ||
                    attempt >= _options.Retry.MaximumAttempts ||
                    _retryClassifier is null ||
                    !_retryClassifier.IsTransient(
                        new SyncRetryContext(
                            _options.PipelineId,
                            _destination.Name,
                            operation,
                            attempt,
                            exception)))
                {
                    throw;
                }

                Interlocked.Increment(ref _retryAttempts);
                var delay = _options.Retry.DelayBeforeAttempt(attempt + 1);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                }
            }
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
