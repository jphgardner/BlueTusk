using System.Runtime.CompilerServices;
using BlueTusk.TypeSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace BlueTusk.Streams.DependencyInjection.Tests;

public sealed class StreamsHostingTests
{
    [Fact]
    public async Task Hosted_consumer_runs_snapshot_lifecycle_and_exposes_health_state()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<RecordingConsumer>();
        services
            .AddBlueTuskStreams()
            .AddHostedConsumer<RecordingConsumer>("orders", _ => new EmptySnapshotSource());
        await using var provider = services.BuildServiceProvider();
        var hosted = Assert.Single(
            provider.GetServices<IHostedService>(),
            service => service.GetType().Name == "BlueTuskStreamsHostedService");

        await hosted.StartAsync(CancellationToken.None);
        var registry = provider.GetRequiredService<BlueTuskStreamHealthRegistry>();
        var timeout = DateTimeOffset.UtcNow.AddSeconds(5);
        while (registry.GetStatuses().SingleOrDefault()?.State != BlueTuskStreamWorkerState.Stopped &&
            DateTimeOffset.UtcNow < timeout)
        {
            await Task.Delay(10);
        }

        await hosted.StopAsync(CancellationToken.None);
        var status = Assert.Single(registry.GetStatuses());
        var consumer = provider.GetRequiredService<RecordingConsumer>();
        Assert.Equal(BlueTuskStreamWorkerState.Stopped, status.State);
        Assert.Equal(["reset", "start", "batch", "complete"], consumer.Events);
        Assert.Equal(0, status.SnapshotRows);

        var health = await new BlueTuskStreamsHealthCheck(registry)
            .CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Degraded, health.Status);
    }

    [Fact]
    public void Duplicate_worker_names_are_rejected_at_registration()
    {
        var services = new ServiceCollection();
        var builder = services.AddBlueTuskStreams();
        builder.AddHostedConsumer<RecordingConsumer>("orders", _ => new EmptySnapshotSource());

        var error = Assert.Throws<InvalidOperationException>(() =>
            builder.AddHostedConsumer<RecordingConsumer>("orders", _ => new EmptySnapshotSource()));

        Assert.Contains("already registered", error.Message, StringComparison.Ordinal);
    }

    private sealed class EmptySnapshotSource : IConsistentSnapshotSource
    {
        public ValueTask<IConsistentSnapshotAttempt> BeginAttemptAsync(
            Guid? abandonedEpoch,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IConsistentSnapshotAttempt>(new EmptySnapshotAttempt());
    }

    private sealed class EmptySnapshotAttempt : IConsistentSnapshotAttempt
    {
        private readonly ChangeTable _table = new(
            1,
            "public",
            "orders",
            'd',
            [new ChangeColumn(0, "id", 23, -1, true)]);

        public EmptySnapshotAttempt()
        {
            Epoch = SnapshotEpoch.Create(
                new ChangeSourceIdentity("system", "database", "slot", "publication"),
                new BlueTuskLogSequenceNumber(100));
            Tables = [_table];
        }

        public SnapshotEpoch Epoch { get; }

        public IReadOnlyList<ChangeTable> Tables { get; }

        public async IAsyncEnumerable<ChangeSnapshotBatch> ReadSnapshotAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield return new ChangeSnapshotBatch(Epoch, _table, 0, [], isLastForTable: true);
        }

        public IChangeStream CreateChangeStream() => new EmptyChangeStream();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyChangeStream : IChangeStream
    {
        public async IAsyncEnumerable<ChangeTransactionDelivery> ReadTransactionsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class RecordingConsumer : IChangeStreamConsumer
    {
        public RecordingConsumer()
        {
        }

        public List<string> Events { get; } = [];

        public ValueTask ResetSnapshotAsync(
            SnapshotReset reset,
            CancellationToken cancellationToken = default)
        {
            Events.Add("reset");
            return ValueTask.CompletedTask;
        }

        public ValueTask StartSnapshotAsync(
            SnapshotStart start,
            CancellationToken cancellationToken = default)
        {
            Events.Add("start");
            return ValueTask.CompletedTask;
        }

        public ValueTask ConsumeSnapshotBatchAsync(
            ChangeSnapshotBatch batch,
            CancellationToken cancellationToken = default)
        {
            Events.Add("batch");
            return ValueTask.CompletedTask;
        }

        public ValueTask CompleteSnapshotAsync(
            SnapshotComplete complete,
            CancellationToken cancellationToken = default)
        {
            Events.Add("complete");
            return ValueTask.CompletedTask;
        }

        public ValueTask ConsumeTransactionAsync(
            ChangeTransactionDelivery delivery,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
