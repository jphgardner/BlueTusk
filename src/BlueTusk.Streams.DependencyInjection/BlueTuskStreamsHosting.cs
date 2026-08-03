using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace BlueTusk.Streams.DependencyInjection;

public enum BlueTuskStreamWorkerState
{
    Starting,
    Snapshotting,
    CatchingUp,
    Running,
    Stopped,
    Faulted,
}

public sealed record BlueTuskStreamWorkerStatus(
    string Name,
    BlueTuskStreamWorkerState State,
    DateTimeOffset ChangedAt,
    Guid? SnapshotEpoch,
    long SnapshotRows,
    long Transactions,
    string? Error);

public sealed class BlueTuskStreamHealthRegistry
{
    private readonly ConcurrentDictionary<string, BlueTuskStreamWorkerStatus> _workers =
        new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public BlueTuskStreamHealthRegistry(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IReadOnlyList<BlueTuskStreamWorkerStatus> GetStatuses() =>
        _workers.Values.OrderBy(worker => worker.Name, StringComparer.Ordinal).ToArray();

    internal void Update(
        string name,
        BlueTuskStreamWorkerState state,
        Guid? snapshotEpoch = null,
        long snapshotRows = 0,
        long transactions = 0,
        Exception? error = null) =>
        _workers[name] = new BlueTuskStreamWorkerStatus(
            name,
            state,
            _timeProvider.GetUtcNow(),
            snapshotEpoch,
            snapshotRows,
            transactions,
            error?.Message);
}

public sealed class BlueTuskStreamsHealthCheck : IHealthCheck
{
    private readonly BlueTuskStreamHealthRegistry _registry;

    public BlueTuskStreamsHealthCheck(BlueTuskStreamHealthRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var statuses = _registry.GetStatuses();
        var data = statuses.ToDictionary(
            worker => worker.Name,
            worker => (object)worker,
            StringComparer.Ordinal);
        if (statuses.Any(worker => worker.State == BlueTuskStreamWorkerState.Faulted))
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "One or more BlueTusk Streams workers are faulted.",
                data: data));
        }

        if (statuses.Count == 0 || statuses.All(worker =>
                worker.State is BlueTuskStreamWorkerState.Starting or BlueTuskStreamWorkerState.Stopped))
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                "No BlueTusk Streams worker is currently running.",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            "BlueTusk Streams workers are healthy.",
            data));
    }
}

public sealed class BlueTuskStreamsBuilder
{
    internal BlueTuskStreamsBuilder(IServiceCollection services)
    {
        Services = services;
    }

    public IServiceCollection Services { get; }

    public BlueTuskStreamsBuilder AddHostedConsumer<TConsumer>(
        string name,
        Func<IServiceProvider, IConsistentSnapshotSource> sourceFactory,
        SnapshotThenStreamOptions? options = null)
        where TConsumer : class, IChangeStreamConsumer
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(sourceFactory);
        if (Services.Any(descriptor =>
                descriptor.ImplementationInstance is HostedConsumerRegistration registration &&
                string.Equals(registration.Name, name, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"A BlueTusk Streams worker named {name} is already registered.");
        }

        Services.AddSingleton(new HostedConsumerRegistration(
            name,
            typeof(TConsumer),
            sourceFactory,
            options ?? new SnapshotThenStreamOptions()));
        return this;
    }
}

public static class BlueTuskStreamsServiceCollectionExtensions
{
    public static BlueTuskStreamsBuilder AddBlueTuskStreams(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<BlueTuskStreamHealthRegistry>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, BlueTuskStreamsHostedService>());
        _ = services
            .AddHealthChecks()
            .AddCheck<BlueTuskStreamsHealthCheck>(
                "bluetusk_streams",
                tags: ["bluetusk", "streams", "ready"]);
        return new BlueTuskStreamsBuilder(services);
    }
}

internal sealed record HostedConsumerRegistration(
    string Name,
    Type ConsumerType,
    Func<IServiceProvider, IConsistentSnapshotSource> SourceFactory,
    SnapshotThenStreamOptions Options);

internal sealed class BlueTuskStreamsHostedService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IReadOnlyList<HostedConsumerRegistration> _registrations;
    private readonly BlueTuskStreamHealthRegistry _health;

    public BlueTuskStreamsHostedService(
        IServiceProvider services,
        IEnumerable<HostedConsumerRegistration> registrations,
        BlueTuskStreamHealthRegistry health)
    {
        _services = services;
        _registrations = registrations.ToArray();
        _health = health;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(_registrations.Select(registration => RunAsync(registration, stoppingToken)));

    private async Task RunAsync(
        HostedConsumerRegistration registration,
        CancellationToken cancellationToken)
    {
        _health.Update(registration.Name, BlueTuskStreamWorkerState.Starting);
        try
        {
            var consumer = (IChangeStreamConsumer)_services.GetRequiredService(registration.ConsumerType);
            var observed = new HealthTrackingConsumer(registration.Name, consumer, _health);
            var source = registration.SourceFactory(_services) ??
                throw new InvalidOperationException(
                    $"Source factory for BlueTusk Streams worker {registration.Name} returned null.");
            await new SnapshotThenStreamCoordinator(source, registration.Options)
                .RunAsync(observed, cancellationToken)
                .ConfigureAwait(false);
            _health.Update(
                registration.Name,
                BlueTuskStreamWorkerState.Stopped,
                observed.SnapshotEpoch,
                observed.SnapshotRows,
                observed.Transactions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _health.Update(registration.Name, BlueTuskStreamWorkerState.Stopped);
        }
        catch (Exception exception)
        {
            _health.Update(registration.Name, BlueTuskStreamWorkerState.Faulted, error: exception);
            throw;
        }
    }

    private sealed class HealthTrackingConsumer : IChangeStreamConsumer
    {
        private readonly string _name;
        private readonly IChangeStreamConsumer _inner;
        private readonly BlueTuskStreamHealthRegistry _health;

        public HealthTrackingConsumer(
            string name,
            IChangeStreamConsumer inner,
            BlueTuskStreamHealthRegistry health)
        {
            _name = name;
            _inner = inner;
            _health = health;
        }

        public Guid? SnapshotEpoch { get; private set; }

        public long SnapshotRows { get; private set; }

        public long Transactions { get; private set; }

        public async ValueTask ResetSnapshotAsync(
            SnapshotReset reset,
            CancellationToken cancellationToken = default)
        {
            SnapshotEpoch = reset.Epoch.Value;
            SnapshotRows = 0;
            _health.Update(_name, BlueTuskStreamWorkerState.Snapshotting, SnapshotEpoch);
            await _inner.ResetSnapshotAsync(reset, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask StartSnapshotAsync(
            SnapshotStart start,
            CancellationToken cancellationToken = default) =>
            _inner.StartSnapshotAsync(start, cancellationToken);

        public async ValueTask ConsumeSnapshotBatchAsync(
            ChangeSnapshotBatch batch,
            CancellationToken cancellationToken = default)
        {
            await _inner.ConsumeSnapshotBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            SnapshotRows = checked(SnapshotRows + batch.Rows.Count);
            _health.Update(
                _name,
                BlueTuskStreamWorkerState.Snapshotting,
                SnapshotEpoch,
                SnapshotRows,
                Transactions);
        }

        public async ValueTask CompleteSnapshotAsync(
            SnapshotComplete complete,
            CancellationToken cancellationToken = default)
        {
            await _inner.CompleteSnapshotAsync(complete, cancellationToken).ConfigureAwait(false);
            _health.Update(
                _name,
                BlueTuskStreamWorkerState.CatchingUp,
                SnapshotEpoch,
                SnapshotRows,
                Transactions);
        }

        public async ValueTask ConsumeTransactionAsync(
            ChangeTransactionDelivery delivery,
            CancellationToken cancellationToken = default)
        {
            await _inner.ConsumeTransactionAsync(delivery, cancellationToken).ConfigureAwait(false);
            Transactions = checked(Transactions + 1);
            _health.Update(
                _name,
                BlueTuskStreamWorkerState.Running,
                SnapshotEpoch,
                SnapshotRows,
                Transactions);
        }
    }
}
