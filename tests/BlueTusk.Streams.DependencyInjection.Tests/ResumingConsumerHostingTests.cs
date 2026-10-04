using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace BlueTusk.Streams.DependencyInjection.Tests;

public sealed class ResumingConsumerHostingTests
{
    private static readonly ChangeSourceIdentity Source =
        new("system", "database", "slot", "publication");

    [Fact]
    public async Task Resuming_consumer_receives_stream_transactions_without_a_snapshot()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<RecordingConsumer>();
        var stream = new FakeResumedStream(
            [new BlueTuskLogSequenceNumber(100), new BlueTuskLogSequenceNumber(200)]);
        services.AddSingleton(stream);
        services
            .AddBlueTuskStreams()
            .AddHostedConsumer<RecordingConsumer>(
                "orders",
                (provider, cancellationToken) =>
                    provider.GetRequiredService<FakeResumedStream>().ReadAsync(cancellationToken));
        await using var provider = services.BuildServiceProvider();
        var hosted = GetHostedService(provider);
        var registry = provider.GetRequiredService<BlueTuskStreamHealthRegistry>();

        await hosted.StartAsync(CancellationToken.None);
        var running = await WaitForStatusAsync(
            registry,
            status => status.State == BlueTuskStreamWorkerState.Running && status.Transactions == 2);
        var healthWhileRunning = await new BlueTuskStreamsHealthCheck(registry)
            .CheckHealthAsync(new HealthCheckContext());
        await hosted.StopAsync(CancellationToken.None);

        var consumer = provider.GetRequiredService<RecordingConsumer>();
        Assert.Equal(["tx:100", "tx:200"], consumer.Events);
        Assert.Equal(
            [new BlueTuskLogSequenceNumber(100), new BlueTuskLogSequenceNumber(200)],
            stream.Acknowledged);
        Assert.Null(running.SnapshotEpoch);
        Assert.Equal(0, running.SnapshotRows);
        Assert.Equal(HealthStatus.Healthy, healthWhileRunning.Status);
        Assert.Equal(1, stream.Opened);
        Assert.Equal(1, stream.Released);
        Assert.Equal(BlueTuskStreamWorkerState.Stopped, Assert.Single(registry.GetStatuses()).State);
    }

    [Fact]
    public async Task Resuming_consumer_is_catching_up_until_its_first_transaction()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<RecordingConsumer>();
        var stream = new FakeResumedStream([]);
        services
            .AddBlueTuskStreams()
            .AddHostedConsumer<RecordingConsumer>("orders", (_, cancellationToken) => stream.ReadAsync(cancellationToken));
        await using var provider = services.BuildServiceProvider();
        var hosted = GetHostedService(provider);
        var registry = provider.GetRequiredService<BlueTuskStreamHealthRegistry>();

        await hosted.StartAsync(CancellationToken.None);
        await stream.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var status = Assert.Single(registry.GetStatuses());
        var health = await new BlueTuskStreamsHealthCheck(registry).CheckHealthAsync(new HealthCheckContext());
        await hosted.StopAsync(CancellationToken.None);

        Assert.Equal(BlueTuskStreamWorkerState.CatchingUp, status.State);
        Assert.Equal(HealthStatus.Healthy, health.Status);
        Assert.Empty(provider.GetRequiredService<RecordingConsumer>().Events);
        Assert.Equal(1, stream.Released);
    }

    [Fact]
    public async Task A_consumer_failure_faults_the_worker_and_releases_the_stream_resources()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new RecordingConsumer { FailOnTransaction = true });
        var stream = new FakeResumedStream([new BlueTuskLogSequenceNumber(100)]);
        services
            .AddBlueTuskStreams()
            .AddHostedConsumer<RecordingConsumer>("orders", (_, cancellationToken) => stream.ReadAsync(cancellationToken));
        await using var provider = services.BuildServiceProvider();
        var hosted = GetHostedService(provider);
        var registry = provider.GetRequiredService<BlueTuskStreamHealthRegistry>();

        await hosted.StartAsync(CancellationToken.None);
        var faulted = await WaitForStatusAsync(
            registry,
            status => status.State == BlueTuskStreamWorkerState.Faulted);
        await hosted.StopAsync(CancellationToken.None);

        Assert.Equal("Destination unavailable.", faulted.Error);
        Assert.Empty(stream.Acknowledged);
        Assert.Equal(1, stream.Released);
        var health = await new BlueTuskStreamsHealthCheck(registry).CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, health.Status);
    }

    [Fact]
    public async Task An_unregistered_consumer_faults_the_worker_before_the_stream_is_opened()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var stream = new FakeResumedStream([new BlueTuskLogSequenceNumber(100)]);
        services
            .AddBlueTuskStreams()
            .AddHostedConsumer<RecordingConsumer>("orders", (_, cancellationToken) => stream.ReadAsync(cancellationToken));
        await using var provider = services.BuildServiceProvider();
        var hosted = GetHostedService(provider);
        var registry = provider.GetRequiredService<BlueTuskStreamHealthRegistry>();

        await hosted.StartAsync(CancellationToken.None);
        var faulted = await WaitForStatusAsync(
            registry,
            status => status.State == BlueTuskStreamWorkerState.Faulted);
        await hosted.StopAsync(CancellationToken.None);

        Assert.Contains(nameof(RecordingConsumer), faulted.Error, StringComparison.Ordinal);
        Assert.Equal(0, stream.Opened);
    }

    [Fact]
    public async Task A_stream_factory_that_returns_null_faults_the_worker()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<RecordingConsumer>();
        services
            .AddBlueTuskStreams()
            .AddHostedConsumer<RecordingConsumer>("orders", (_, _) => null!);
        await using var provider = services.BuildServiceProvider();
        var hosted = GetHostedService(provider);
        var registry = provider.GetRequiredService<BlueTuskStreamHealthRegistry>();

        await hosted.StartAsync(CancellationToken.None);
        var faulted = await WaitForStatusAsync(
            registry,
            status => status.State == BlueTuskStreamWorkerState.Faulted);
        await hosted.StopAsync(CancellationToken.None);

        Assert.Equal("Stream factory for BlueTusk Streams worker orders returned null.", faulted.Error);
    }

    [Fact]
    public void Resuming_consumer_registration_validates_its_arguments()
    {
        var builder = new ServiceCollection().AddBlueTuskStreams();
        Func<IServiceProvider, CancellationToken, IAsyncEnumerable<ChangeTransactionDelivery>> factory =
            (_, _) => AsyncEnumerable.Empty<ChangeTransactionDelivery>();

        Assert.Throws<ArgumentNullException>(() => builder.AddHostedConsumer<RecordingConsumer>(null!, factory));
        Assert.Throws<ArgumentException>(() => builder.AddHostedConsumer<RecordingConsumer>(" ", factory));
        Assert.Throws<ArgumentNullException>(() => builder.AddHostedConsumer<RecordingConsumer>(
            "orders",
            (Func<IServiceProvider, CancellationToken, IAsyncEnumerable<ChangeTransactionDelivery>>)null!));
        Assert.DoesNotContain(
            builder.Services,
            descriptor => descriptor.ServiceType.Name == "HostedConsumerRegistration");
    }

    [Fact]
    public void Worker_names_are_unique_across_snapshot_and_resuming_consumers()
    {
        var builder = new ServiceCollection().AddBlueTuskStreams();
        builder.AddHostedConsumer<RecordingConsumer>(
            "orders",
            (_, _) => AsyncEnumerable.Empty<ChangeTransactionDelivery>());

        var snapshotDuplicate = Assert.Throws<InvalidOperationException>(() =>
            builder.AddHostedConsumer<RecordingConsumer>("orders", _ => new UnusedSnapshotSource()));
        var resumingDuplicate = Assert.Throws<InvalidOperationException>(() =>
            builder.AddHostedConsumer<RecordingConsumer>(
                "orders",
                (_, _) => AsyncEnumerable.Empty<ChangeTransactionDelivery>()));
        builder.AddHostedConsumer<RecordingConsumer>("customers", _ => new UnusedSnapshotSource());

        Assert.Contains("already registered", snapshotDuplicate.Message, StringComparison.Ordinal);
        Assert.Contains("already registered", resumingDuplicate.Message, StringComparison.Ordinal);
        Assert.Equal(
            2,
            builder.Services.Count(descriptor => descriptor.ServiceType.Name == "HostedConsumerRegistration"));
    }

    [Fact]
    public async Task Resuming_and_snapshot_consumers_share_one_hosted_service()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<RecordingConsumer>();
        var stream = new FakeResumedStream([new BlueTuskLogSequenceNumber(100)]);
        services
            .AddBlueTuskStreams()
            .AddHostedConsumer<RecordingConsumer>("resumed", (_, cancellationToken) => stream.ReadAsync(cancellationToken))
            .AddHostedConsumer<RecordingConsumer>("bootstrap", _ => new UnusedSnapshotSource());
        await using var provider = services.BuildServiceProvider();

        var hosted = GetHostedService(provider);
        var registry = provider.GetRequiredService<BlueTuskStreamHealthRegistry>();
        await hosted.StartAsync(CancellationToken.None);
        await WaitForStatusAsync(
            registry,
            status => status.Name == "resumed" && status.State == BlueTuskStreamWorkerState.Running);
        await hosted.StopAsync(CancellationToken.None);

        Assert.Equal(["bootstrap", "resumed"], registry.GetStatuses().Select(status => status.Name));
    }

    private static IHostedService GetHostedService(IServiceProvider provider) =>
        Assert.Single(
            provider.GetServices<IHostedService>(),
            service => service.GetType().Name == "BlueTuskStreamsHostedService");

    private static async Task<BlueTuskStreamWorkerStatus> WaitForStatusAsync(
        BlueTuskStreamHealthRegistry registry,
        Func<BlueTuskStreamWorkerStatus, bool> predicate)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < timeout)
        {
            var match = registry.GetStatuses().FirstOrDefault(predicate);
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException(
            "Worker status was not reached: " + string.Join(", ", registry.GetStatuses()));
    }

    /// <summary>
    /// Models a factory that opens resources, resumes after its checkpoint, and then waits for
    /// more WAL until the worker stops. The finally block stands in for <c>await using</c>.
    /// </summary>
    private sealed class FakeResumedStream : IChangeDeliveryObserver
    {
        private readonly IReadOnlyList<BlueTuskLogSequenceNumber> _positions;
        private int _opened;
        private int _released;

        public FakeResumedStream(IReadOnlyList<BlueTuskLogSequenceNumber> positions)
        {
            _positions = positions;
        }

        public int Opened => Volatile.Read(ref _opened);

        public int Released => Volatile.Read(ref _released);

        public ConcurrentQueue<BlueTuskLogSequenceNumber> Acknowledged { get; } = new();

        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ChangeTransactionDelivery> ReadAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _opened);
            try
            {
                uint transactionId = 1;
                foreach (var position in _positions)
                {
                    yield return ChangeDeliveryTestFactory.CreateCommitted(
                        Source,
                        transactionId++,
                        position,
                        observer: this);
                }

                Waiting.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                Interlocked.Increment(ref _released);
            }
        }

        public ValueTask AcknowledgeAsync(
            ChangeTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            Acknowledged.Enqueue(transaction.CommitEndPosition);
            return ValueTask.CompletedTask;
        }

        public ValueTask NackAsync(
            ChangeTransaction transaction,
            Exception? failure,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class UnusedSnapshotSource : IConsistentSnapshotSource
    {
        public ValueTask<IConsistentSnapshotAttempt> BeginAttemptAsync(
            Guid? abandonedEpoch,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IConsistentSnapshotAttempt>(
                new InvalidOperationException("Snapshot source is not used by this test."));
    }

    private sealed class RecordingConsumer : IChangeStreamConsumer
    {
        public bool FailOnTransaction { get; init; }

        public ConcurrentQueue<string> Events { get; } = new();

        public ValueTask ResetSnapshotAsync(SnapshotReset reset, CancellationToken cancellationToken = default)
        {
            Events.Enqueue("reset");
            return ValueTask.CompletedTask;
        }

        public ValueTask StartSnapshotAsync(SnapshotStart start, CancellationToken cancellationToken = default)
        {
            Events.Enqueue("start");
            return ValueTask.CompletedTask;
        }

        public ValueTask ConsumeSnapshotBatchAsync(
            ChangeSnapshotBatch batch,
            CancellationToken cancellationToken = default)
        {
            Events.Enqueue("batch");
            return ValueTask.CompletedTask;
        }

        public ValueTask CompleteSnapshotAsync(
            SnapshotComplete complete,
            CancellationToken cancellationToken = default)
        {
            Events.Enqueue("complete");
            return ValueTask.CompletedTask;
        }

        public async ValueTask ConsumeTransactionAsync(
            ChangeTransactionDelivery delivery,
            CancellationToken cancellationToken = default)
        {
            if (FailOnTransaction)
            {
                throw new InvalidOperationException("Destination unavailable.");
            }

            Events.Enqueue("tx:" + delivery.Transaction.CommitEndPosition.Value);
            await delivery.AcknowledgeAsync(cancellationToken);
        }
    }
}
