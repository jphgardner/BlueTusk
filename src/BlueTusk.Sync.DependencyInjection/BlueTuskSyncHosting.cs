using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using BlueTusk.Streams;
using BlueTusk.Streams.Storage.PostgreSql;
using BlueTusk.TypeSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BlueTusk.Sync.DependencyInjection;

/// <summary>Names the diagnostics emitted by BlueTusk Sync hosting.</summary>
public static class BlueTuskSyncTelemetry
{
    /// <summary>Gets the ActivitySource name used for Sync worker operations.</summary>
    public const string ActivitySourceName = "BlueTusk.Sync";

    /// <summary>Gets the Meter name used for Sync worker measurements.</summary>
    public const string MeterName = "BlueTusk.Sync";
}

/// <summary>Contains the latest observable state of one hosted Sync pipeline.</summary>
public sealed record BlueTuskSyncWorkerStatus(
    string PipelineId,
    SyncPipelineState State,
    DateTimeOffset ChangedAt,
    long AppliedTransactions,
    long AppliedSnapshotBatches,
    long SnapshotRows,
    long QuarantinedTransactions,
    long RetryAttempts,
    TimeSpan ThrottleDelay,
    BlueTuskLogSequenceNumber LastCommitPosition,
    Guid? SnapshotEpoch,
    bool HandoffCommitted,
    string? Error);

/// <summary>Stores lock-free health snapshots for all hosted Sync pipelines.</summary>
public sealed class BlueTuskSyncHealthRegistry
{
    private readonly ConcurrentDictionary<string, BlueTuskSyncWorkerStatus> _workers =
        new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes an empty registry.</summary>
    public BlueTuskSyncHealthRegistry(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Gets stable pipeline-ordered worker snapshots.</summary>
    public IReadOnlyList<BlueTuskSyncWorkerStatus> GetStatuses() =>
        _workers.Values.OrderBy(static worker => worker.PipelineId, StringComparer.Ordinal).ToArray();

    internal void Update(
        string pipelineId,
        SyncPipelineStatus status,
        long snapshotRows,
        BlueTuskLogSequenceNumber lastCommitPosition,
        Guid? snapshotEpoch,
        bool handoffCommitted,
        Exception? error = null) =>
        _workers[pipelineId] = new BlueTuskSyncWorkerStatus(
            pipelineId,
            status.State,
            _timeProvider.GetUtcNow(),
            status.AppliedTransactions,
            status.AppliedSnapshotBatches,
            snapshotRows,
            status.QuarantinedTransactions,
            status.RetryAttempts,
            status.ThrottleDelay,
            lastCommitPosition,
            snapshotEpoch,
            handoffCommitted,
            error?.Message ?? status.LastError);
}

/// <summary>Reports hosted Sync pipeline readiness.</summary>
public sealed class BlueTuskSyncHealthCheck : IHealthCheck
{
    private readonly BlueTuskSyncHealthRegistry _registry;

    /// <summary>Initializes the health check.</summary>
    public BlueTuskSyncHealthCheck(BlueTuskSyncHealthRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var statuses = _registry.GetStatuses();
        var data = statuses.ToDictionary(
            static worker => worker.PipelineId,
            static worker => (object)worker,
            StringComparer.Ordinal);
        if (statuses.Any(static worker =>
                worker.Error is not null ||
                worker.State is SyncPipelineState.Faulted or SyncPipelineState.Rebuilding))
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "One or more BlueTusk Sync pipelines require operator action.",
                data: data));
        }

        if (statuses.Count == 0 || statuses.All(static worker =>
                worker.State is SyncPipelineState.Stopped or
                    SyncPipelineState.Provisioning or
                    SyncPipelineState.Paused))
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                "No BlueTusk Sync pipeline is currently applying changes.",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            "BlueTusk Sync pipelines are healthy.",
            data));
    }
}

/// <summary>Reads a durable source boundary while an active worker is quiesced.</summary>
public interface ISyncCutoverPositionProvider
{
    /// <summary>Gets the durable relay boundary for one rebuild snapshot epoch.</summary>
    ValueTask<BlueTuskLogSequenceNumber> GetDurableHeadAsync(
        string pipelineId,
        SnapshotEpoch snapshotEpoch,
        CancellationToken cancellationToken = default);
}

/// <summary>Reads cutover boundaries from the production PostgreSQL durable relay.</summary>
public sealed class PostgreSqlRelaySyncCutoverPositionProvider : ISyncCutoverPositionProvider
{
    private readonly PostgreSqlDurableChangeRelay _relay;

    /// <summary>Initializes a provider over the separately configured relay control data source.</summary>
    public PostgreSqlRelaySyncCutoverPositionProvider(PostgreSqlDurableChangeRelay relay)
    {
        _relay = relay ?? throw new ArgumentNullException(nameof(relay));
    }

    /// <inheritdoc />
    public async ValueTask<BlueTuskLogSequenceNumber> GetDurableHeadAsync(
        string pipelineId,
        SnapshotEpoch snapshotEpoch,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        await _relay.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var source = await _relay.RegisterSourceAsync(
            snapshotEpoch.Source,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return source.LastCommitPosition >= snapshotEpoch.ConsistentPosition
            ? source.LastCommitPosition
            : snapshotEpoch.ConsistentPosition;
    }
}

/// <summary>Promotes the replacement worker after its destination generation is activated.</summary>
public interface ISyncWorkerHandoffHandler
{
    /// <summary>
    /// Completes a restart-safe handoff. The previous worker has already been permanently stopped.
    /// </summary>
    ValueTask CompleteHandoffAsync(
        string pipelineId,
        BlueTuskLogSequenceNumber activatedPosition,
        CancellationToken cancellationToken = default);
}

/// <summary>Registers hosted Sync pipelines and optional rebuild-cutover services.</summary>
public sealed class BlueTuskSyncBuilder
{
    internal BlueTuskSyncBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>Gets the underlying service collection.</summary>
    public IServiceCollection Services { get; }

    /// <summary>Registers one in-process snapshot-then-stream Sync worker.</summary>
    public BlueTuskSyncBuilder AddHostedPipeline<TTransform, TDestination>(
        SyncPipelineOptions options,
        ChangeSourceIdentity source,
        Func<IServiceProvider, IConsistentSnapshotSource> sourceFactory,
        SnapshotThenStreamOptions? snapshotOptions = null,
        Func<IServiceProvider, ISyncQuarantineSink?>? quarantineFactory = null)
        where TTransform : class, ISyncTransform
        where TDestination : class, ISyncDestination
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceFactory);
        EnsureUniquePipeline(options.PipelineId);
        Services.TryAddSingleton<TTransform>();
        Services.TryAddSingleton<TDestination>();
        Services.AddSingleton(new HostedSyncPipelineRegistration(
            options,
            source,
            typeof(TTransform),
            typeof(TDestination),
            services => new ConsistentSnapshotSyncPipelineSource(
                sourceFactory(services) ?? throw new InvalidOperationException(
                    $"Source factory for BlueTusk Sync pipeline '{options.PipelineId}' returned null."),
                snapshotOptions),
            quarantineFactory));
        return this;
    }

    /// <summary>Registers one in-process worker with a restart-aware source lifecycle.</summary>
    public BlueTuskSyncBuilder AddHostedPipelineSource<TTransform, TDestination>(
        SyncPipelineOptions options,
        ChangeSourceIdentity source,
        Func<IServiceProvider, ISyncPipelineSource> sourceFactory,
        Func<IServiceProvider, ISyncQuarantineSink?>? quarantineFactory = null)
        where TTransform : class, ISyncTransform
        where TDestination : class, ISyncDestination
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceFactory);
        EnsureUniquePipeline(options.PipelineId);
        Services.TryAddSingleton<TTransform>();
        Services.TryAddSingleton<TDestination>();
        Services.AddSingleton(new HostedSyncPipelineRegistration(
            options,
            source,
            typeof(TTransform),
            typeof(TDestination),
            sourceFactory,
            quarantineFactory));
        return this;
    }

    private void EnsureUniquePipeline(string pipelineId)
    {
        if (Services.Any(descriptor =>
                descriptor.ImplementationInstance is HostedSyncPipelineRegistration registration &&
                string.Equals(registration.Options.PipelineId, pipelineId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"A BlueTusk Sync pipeline named '{pipelineId}' is already registered.");
        }
    }

    /// <summary>Registers the hosted-worker cutover barrier and its durable-head/handoff services.</summary>
    public BlueTuskSyncBuilder AddRebuildCutover<TPositionProvider, THandoffHandler>()
        where TPositionProvider : class, ISyncCutoverPositionProvider
        where THandoffHandler : class, ISyncWorkerHandoffHandler
    {
        Services.TryAddSingleton<TPositionProvider>();
        Services.TryAddSingleton<ISyncCutoverPositionProvider>(static services =>
            services.GetRequiredService<TPositionProvider>());
        Services.TryAddSingleton<THandoffHandler>();
        Services.TryAddSingleton<ISyncWorkerHandoffHandler>(static services =>
            services.GetRequiredService<THandoffHandler>());
        Services.TryAddSingleton<ISyncRebuildCutoverBarrier, HostedSyncRebuildCutoverBarrier>();
        return this;
    }

    /// <summary>Registers PostgreSQL durable-relay cutover boundaries and a worker handoff.</summary>
    public BlueTuskSyncBuilder AddPostgreSqlRelayRebuildCutover<THandoffHandler>()
        where THandoffHandler : class, ISyncWorkerHandoffHandler =>
        AddRebuildCutover<PostgreSqlRelaySyncCutoverPositionProvider, THandoffHandler>();
}

/// <summary>Registers the BlueTusk Sync in-process worker runtime.</summary>
public static class BlueTuskSyncServiceCollectionExtensions
{
    /// <summary>Adds hosted Sync pipelines, telemetry, and readiness health checks.</summary>
    public static BlueTuskSyncBuilder AddBlueTuskSync(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<BlueTuskSyncHealthRegistry>();
        services.TryAddSingleton<SyncWorkerRuntimeRegistry>();
        services.TryAddSingleton<BlueTuskSyncHealthCheck>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, BlueTuskSyncHostedService>());
        _ = services
            .AddHealthChecks()
            .AddCheck<BlueTuskSyncHealthCheck>(
                "bluetusk_sync",
                tags: ["bluetusk", "sync", "ready"]);
        return new BlueTuskSyncBuilder(services);
    }
}

internal sealed record HostedSyncPipelineRegistration(
    SyncPipelineOptions Options,
    ChangeSourceIdentity Source,
    Type TransformType,
    Type DestinationType,
    Func<IServiceProvider, ISyncPipelineSource> SourceFactory,
    Func<IServiceProvider, ISyncQuarantineSink?>? QuarantineFactory);

internal sealed class BlueTuskSyncHostedService : BackgroundService
{
    private static readonly Action<ILogger, string, Exception?> PipelineStopped =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(1, "SyncPipelineStopped"),
            "BlueTusk Sync pipeline {PipelineId} stopped and requires operator action.");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadOnlyList<HostedSyncPipelineRegistration> _registrations;
    private readonly BlueTuskSyncHealthRegistry _health;
    private readonly SyncWorkerRuntimeRegistry _runtimes;
    private readonly ILogger<BlueTuskSyncHostedService> _logger;

    public BlueTuskSyncHostedService(
        IServiceScopeFactory scopeFactory,
        IEnumerable<HostedSyncPipelineRegistration> registrations,
        BlueTuskSyncHealthRegistry health,
        SyncWorkerRuntimeRegistry runtimes,
        ILogger<BlueTuskSyncHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _registrations = registrations.ToArray();
        _health = health;
        _runtimes = runtimes;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(_registrations.Select(registration => RunAsync(registration, stoppingToken)));

    private async Task RunAsync(
        HostedSyncPipelineRegistration registration,
        CancellationToken stoppingToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var transform = (ISyncTransform)services.GetRequiredService(registration.TransformType);
        var destination = (ISyncDestination)services.GetRequiredService(registration.DestinationType);
        var quarantine = registration.QuarantineFactory?.Invoke(services) ??
            destination as ISyncQuarantineSink;
        var retryClassifier = services.GetService<ISyncRetryClassifier>();
        await using var pipeline = new SyncPipeline(
            registration.Options,
            registration.Source,
            transform,
            destination,
            quarantine,
            retryClassifier);
        using var workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var runtime = new SyncWorkerRuntime(pipeline, workerCancellation);
        _runtimes.Add(registration.Options.PipelineId, runtime);
        var observed = new ObservedSyncConsumer(
            registration.Options.PipelineId,
            pipeline,
            runtime,
            _health);
        try
        {
            _health.Update(
                registration.Options.PipelineId,
                pipeline.Status,
                0,
                BlueTuskLogSequenceNumber.Zero,
                null,
                handoffCommitted: false);
            await pipeline.ProvisionAsync(workerCancellation.Token).ConfigureAwait(false);
            observed.UpdateHealth();
            var source = registration.SourceFactory(services) ??
                throw new InvalidOperationException(
                    $"Source factory for BlueTusk Sync pipeline '{registration.Options.PipelineId}' returned null.");
            await source.RunAsync(observed, workerCancellation.Token).ConfigureAwait(false);
            await pipeline.StopAsync(CancellationToken.None).ConfigureAwait(false);
            observed.UpdateHealth();
        }
        catch (OperationCanceledException) when (workerCancellation.IsCancellationRequested)
        {
            await pipeline.StopAsync(CancellationToken.None).ConfigureAwait(false);
            observed.UpdateHealth();
        }
        catch (Exception exception)
        {
            SyncHostingDiagnostics.Errors.Add(1, SyncHostingDiagnostics.PipelineTag(
                registration.Options.PipelineId));
            _health.Update(
                registration.Options.PipelineId,
                pipeline.Status,
                observed.SnapshotRows,
                observed.LastCommitPosition,
                observed.SnapshotEpoch,
                runtime.HandoffCommitted,
                exception);
            PipelineStopped(_logger, registration.Options.PipelineId, exception);
        }
        finally
        {
            _runtimes.Remove(registration.Options.PipelineId, runtime);
            runtime.Dispose();
        }
    }
}

internal sealed class ObservedSyncConsumer : IChangeStreamConsumer
{
    private readonly string _pipelineId;
    private readonly SyncPipeline _pipeline;
    private readonly SyncWorkerRuntime _runtime;
    private readonly BlueTuskSyncHealthRegistry _health;
    private long _reportedRetries;
    private long _reportedThrottleTicks;

    public ObservedSyncConsumer(
        string pipelineId,
        SyncPipeline pipeline,
        SyncWorkerRuntime runtime,
        BlueTuskSyncHealthRegistry health)
    {
        _pipelineId = pipelineId;
        _pipeline = pipeline;
        _runtime = runtime;
        _health = health;
    }

    public Guid? SnapshotEpoch { get; private set; }

    public long SnapshotRows { get; private set; }

    public BlueTuskLogSequenceNumber LastCommitPosition { get; private set; }

    public async ValueTask ResetSnapshotAsync(
        SnapshotReset reset,
        CancellationToken cancellationToken = default)
    {
        await using var gate = await _runtime.EnterDeliveryAsync(cancellationToken).ConfigureAwait(false);
        SnapshotEpoch = reset.Epoch.Value;
        SnapshotRows = 0;
        await _pipeline.ResetSnapshotAsync(reset, cancellationToken).ConfigureAwait(false);
        UpdateHealth();
    }

    public async ValueTask StartSnapshotAsync(
        SnapshotStart start,
        CancellationToken cancellationToken = default)
    {
        await using var gate = await _runtime.EnterDeliveryAsync(cancellationToken).ConfigureAwait(false);
        await _pipeline.StartSnapshotAsync(start, cancellationToken).ConfigureAwait(false);
        UpdateHealth();
    }

    public async ValueTask ConsumeSnapshotBatchAsync(
        ChangeSnapshotBatch batch,
        CancellationToken cancellationToken = default)
    {
        await using var gate = await _runtime.EnterDeliveryAsync(cancellationToken).ConfigureAwait(false);
        using var activity = SyncHostingDiagnostics.ActivitySource.StartActivity(
            "sync.snapshot.consume",
            ActivityKind.Consumer);
        activity?.SetTag("sync.pipeline.id", _pipelineId);
        activity?.SetTag("sync.snapshot.epoch", batch.Epoch.Value);
        await _pipeline.ConsumeSnapshotBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        SnapshotRows = checked(SnapshotRows + batch.Rows.Count);
        SyncHostingDiagnostics.SnapshotRows.Add(
            batch.Rows.Count,
            SyncHostingDiagnostics.PipelineTag(_pipelineId));
        UpdateHealth();
    }

    public async ValueTask CompleteSnapshotAsync(
        SnapshotComplete complete,
        CancellationToken cancellationToken = default)
    {
        await using var gate = await _runtime.EnterDeliveryAsync(cancellationToken).ConfigureAwait(false);
        await _pipeline.CompleteSnapshotAsync(complete, cancellationToken).ConfigureAwait(false);
        UpdateHealth();
    }

    public async ValueTask ConsumeTransactionAsync(
        ChangeTransactionDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        await using var gate = await _runtime.EnterDeliveryAsync(cancellationToken).ConfigureAwait(false);
        using var activity = SyncHostingDiagnostics.ActivitySource.StartActivity(
            "sync.transaction.consume",
            ActivityKind.Consumer);
        activity?.SetTag("sync.pipeline.id", _pipelineId);
        activity?.SetTag("db.transaction.id", delivery.Transaction.TransactionId);
        activity?.SetTag("db.postgresql.commit_end_lsn", delivery.Transaction.CommitEndPosition.Value);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await _pipeline.ConsumeTransactionAsync(delivery, cancellationToken).ConfigureAwait(false);
            LastCommitPosition = delivery.Transaction.CommitEndPosition;
            SyncHostingDiagnostics.Transactions.Add(
                1,
                SyncHostingDiagnostics.PipelineTag(_pipelineId));
        }
        finally
        {
            SyncHostingDiagnostics.TransactionDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                SyncHostingDiagnostics.PipelineTag(_pipelineId));
            UpdateHealth();
        }
    }

    public void UpdateHealth()
    {
        var status = _pipeline.Status;
        var retries = status.RetryAttempts;
        var retryDelta = retries - Interlocked.Exchange(ref _reportedRetries, retries);
        if (retryDelta > 0)
        {
            SyncHostingDiagnostics.Retries.Add(
                retryDelta,
                SyncHostingDiagnostics.PipelineTag(_pipelineId));
        }

        var throttleTicks = status.ThrottleDelay.Ticks;
        var throttleDelta = throttleTicks -
                            Interlocked.Exchange(ref _reportedThrottleTicks, throttleTicks);
        if (throttleDelta > 0)
        {
            SyncHostingDiagnostics.ThrottleDuration.Record(
                TimeSpan.FromTicks(throttleDelta).TotalMilliseconds,
                SyncHostingDiagnostics.PipelineTag(_pipelineId));
        }

        _health.Update(
            _pipelineId,
            status,
            SnapshotRows,
            LastCommitPosition,
            SnapshotEpoch,
            _runtime.HandoffCommitted);
    }
}

internal static class SyncHostingDiagnostics
{
    public static readonly ActivitySource ActivitySource =
        new(BlueTuskSyncTelemetry.ActivitySourceName);
    private static readonly Meter Meter = new(BlueTuskSyncTelemetry.MeterName);

    public static readonly Counter<long> Transactions =
        Meter.CreateCounter<long>("bluetusk.sync.transactions", "{transaction}");

    public static readonly Counter<long> Retries =
        Meter.CreateCounter<long>("bluetusk.sync.retries", "{attempt}");

    public static readonly Histogram<double> ThrottleDuration =
        Meter.CreateHistogram<double>("bluetusk.sync.throttle.duration", "ms");
    public static readonly Counter<long> SnapshotRows =
        Meter.CreateCounter<long>("bluetusk.sync.snapshot.rows", "{row}");
    public static readonly Counter<long> Errors =
        Meter.CreateCounter<long>("bluetusk.sync.errors", "{error}");
    public static readonly Histogram<double> TransactionDuration =
        Meter.CreateHistogram<double>("bluetusk.sync.transaction.duration", "ms");

    public static KeyValuePair<string, object?> PipelineTag(string pipelineId) =>
        new("sync.pipeline.id", pipelineId);
}

internal sealed class SyncWorkerRuntimeRegistry
{
    private readonly ConcurrentDictionary<string, SyncWorkerRuntime> _runtimes =
        new(StringComparer.Ordinal);

    public void Add(string pipelineId, SyncWorkerRuntime runtime)
    {
        if (!_runtimes.TryAdd(pipelineId, runtime))
        {
            throw new InvalidOperationException(
                $"BlueTusk Sync pipeline '{pipelineId}' already has an active worker.");
        }
    }

    public SyncWorkerRuntime GetRequired(string pipelineId) =>
        _runtimes.TryGetValue(pipelineId, out var runtime)
            ? runtime
            : throw new InvalidOperationException(
                $"BlueTusk Sync pipeline '{pipelineId}' does not have an active hosted worker.");

    public void Remove(string pipelineId, SyncWorkerRuntime runtime) =>
        _ = _runtimes.TryRemove(new KeyValuePair<string, SyncWorkerRuntime>(pipelineId, runtime));
}

internal sealed class SyncWorkerRuntime(
    SyncPipeline pipeline,
    CancellationTokenSource workerCancellation) : IDisposable
{
    private readonly SemaphoreSlim _deliveryGate = new(1, 1);
    private readonly object _lifetimeLock = new();
    private int _handoffCommitted;
    private int _leases;
    private bool _disposeRequested;

    public SyncPipeline Pipeline => pipeline;

    public bool HandoffCommitted => Volatile.Read(ref _handoffCommitted) != 0;

    public async ValueTask<SyncWorkerGateLease> EnterDeliveryAsync(
        CancellationToken cancellationToken)
    {
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            _leases = checked(_leases + 1);
        }

        try
        {
            await _deliveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new SyncWorkerGateLease(this);
        }
        catch
        {
            ReleaseLease(gateAcquired: false);
            throw;
        }
    }

    public async ValueTask BeginHandoffAsync()
    {
        if (Interlocked.Exchange(ref _handoffCommitted, 1) == 0)
        {
            await workerCancellation.CancelAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        var disposeGate = false;
        lock (_lifetimeLock)
        {
            if (_disposeRequested)
            {
                return;
            }

            _disposeRequested = true;
            disposeGate = _leases == 0;
        }

        if (disposeGate)
        {
            _deliveryGate.Dispose();
        }
    }

    internal void ReleaseLease(bool gateAcquired = true)
    {
        if (gateAcquired)
        {
            _deliveryGate.Release();
        }

        var disposeGate = false;
        lock (_lifetimeLock)
        {
            _leases--;
            disposeGate = _disposeRequested && _leases == 0;
        }

        if (disposeGate)
        {
            _deliveryGate.Dispose();
        }
    }
}

internal sealed class SyncWorkerGateLease(SyncWorkerRuntime runtime) : IAsyncDisposable
{
    private int _disposed;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            runtime.ReleaseLease();
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class HostedSyncRebuildCutoverBarrier : ISyncRebuildCutoverBarrier
{
    private readonly SyncWorkerRuntimeRegistry _runtimes;
    private readonly ISyncCutoverPositionProvider _positions;
    private readonly ISyncWorkerHandoffHandler _handoff;

    public HostedSyncRebuildCutoverBarrier(
        SyncWorkerRuntimeRegistry runtimes,
        ISyncCutoverPositionProvider positions,
        ISyncWorkerHandoffHandler handoff)
    {
        _runtimes = runtimes;
        _positions = positions;
        _handoff = handoff;
    }

    public async ValueTask<ISyncRebuildCutoverLease> AcquireAsync(
        string pipelineId,
        SnapshotEpoch snapshotEpoch,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        var runtime = _runtimes.GetRequired(pipelineId);
        var gate = await runtime.EnterDeliveryAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (runtime.HandoffCommitted)
            {
                throw new InvalidOperationException(
                    $"BlueTusk Sync pipeline '{pipelineId}' has already committed a worker handoff.");
            }

            if (runtime.Pipeline.Status.State is not SyncPipelineState.Running)
            {
                throw new InvalidOperationException(
                    $"BlueTusk Sync pipeline '{pipelineId}' must be running before rebuild cutover; current state is '{runtime.Pipeline.Status.State}'.");
            }

            var target = await _positions.GetDurableHeadAsync(
                pipelineId,
                snapshotEpoch,
                cancellationToken).ConfigureAwait(false);
            if (target.Value == 0 || target < snapshotEpoch.ConsistentPosition)
            {
                throw new InvalidOperationException(
                    $"Cutover provider returned '{target}' before snapshot position '{snapshotEpoch.ConsistentPosition}'.");
            }

            return new HostedSyncRebuildCutoverLease(
                pipelineId,
                target,
                runtime,
                gate,
                _handoff);
        }
        catch
        {
            await gate.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

internal sealed class HostedSyncRebuildCutoverLease(
    string pipelineId,
    BlueTuskLogSequenceNumber targetPosition,
    SyncWorkerRuntime runtime,
    SyncWorkerGateLease gate,
    ISyncWorkerHandoffHandler handoff) : ISyncRebuildCutoverLease
{
    public BlueTuskLogSequenceNumber TargetPosition => targetPosition;

    public async ValueTask CompleteHandoffAsync(
        BlueTuskLogSequenceNumber activatedPosition,
        CancellationToken cancellationToken = default)
    {
        await runtime.BeginHandoffAsync().ConfigureAwait(false);
        if (activatedPosition != targetPosition)
        {
            throw new ArgumentException(
                $"Activated position '{activatedPosition}' does not match cutover target '{targetPosition}'.",
                nameof(activatedPosition));
        }

        await handoff.CompleteHandoffAsync(
            pipelineId,
            activatedPosition,
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => gate.DisposeAsync();
}
