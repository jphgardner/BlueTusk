using System.Runtime.CompilerServices;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace BlueTusk.Sync.DependencyInjection.Tests;

public sealed class BlueTuskSyncHostingTests
{
    private static readonly ChangeSourceIdentity Source =
        new("sync-host-system", "sync-host-db", "sync-host-slot", "public:orders");

    [Fact]
    public void Registration_is_named_and_rejects_duplicates()
    {
        var services = new ServiceCollection();
        var builder = services.AddBlueTuskSync();
        builder.AddHostedPipeline<TestTransform, RecordingDestination>(
            new SyncPipelineOptions { PipelineId = "orders" },
            Source,
            _ => new FiniteSnapshotSource([]));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.AddHostedPipeline<TestTransform, RecordingDestination>(
                new SyncPipelineOptions { PipelineId = "orders" },
                Source,
                _ => new FiniteSnapshotSource([])));

        Assert.Contains("already registered", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hosted_pipeline_provisions_snapshots_applies_and_acknowledges()
    {
        var events = new List<string>();
        var observer = new RecordingObserver(events);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new TestTransform());
        services.AddSingleton(new RecordingDestination(events));
        services.AddBlueTuskSync()
            .AddHostedPipeline<TestTransform, RecordingDestination>(
                new SyncPipelineOptions { PipelineId = "orders" },
                Source,
                _ => new FiniteSnapshotSource(
                    [ChangeDeliveryTestFactory.CreateCommitted(
                        Source,
                        transactionId: 7,
                        commitEndPosition: Lsn(105),
                        observer: observer)]));
        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().Single(service =>
            service.GetType().Name.Contains("SyncHostedService", StringComparison.Ordinal));

        await hosted.StartAsync(TestContext.Current.CancellationToken);
        var registry = provider.GetRequiredService<BlueTuskSyncHealthRegistry>();
        await WaitUntilAsync(
            () => registry.GetStatuses().SingleOrDefault()?.State is SyncPipelineState.Stopped,
            TestContext.Current.CancellationToken);
        await hosted.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["provision", "reset", "start", "complete", "apply:105", "ack:105"],
            events);
        var status = Assert.Single(registry.GetStatuses());
        Assert.Equal(1, status.AppliedTransactions);
        Assert.Equal(Lsn(105), status.LastCommitPosition);
        Assert.False(status.HandoffCommitted);
        var health = await provider.GetRequiredService<BlueTuskSyncHealthCheck>()
            .CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Degraded, health.Status);
    }

    [Fact]
    public void Rebuild_cutover_registration_resolves_the_shared_barrier()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new PositionProvider());
        services.AddSingleton(new HandoffHandler());
        services.AddBlueTuskSync().AddRebuildCutover<PositionProvider, HandoffHandler>();
        using var provider = services.BuildServiceProvider();

        Assert.IsAssignableFrom<ISyncRebuildCutoverBarrier>(
            provider.GetRequiredService<ISyncRebuildCutoverBarrier>());
        Assert.IsType<PositionProvider>(
            provider.GetRequiredService<ISyncCutoverPositionProvider>());
        Assert.IsType<HandoffHandler>(
            provider.GetRequiredService<ISyncWorkerHandoffHandler>());
    }

    [Fact]
    public async Task Cutover_barrier_commits_handoff_and_permanently_stops_the_old_worker()
    {
        var events = new List<string>();
        var observer = new RecordingObserver(events);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new TestTransform());
        services.AddSingleton(new RecordingDestination(events));
        services.AddSingleton(new PositionProvider());
        services.AddSingleton(new HandoffHandler(events));
        services.AddBlueTuskSync()
            .AddHostedPipeline<TestTransform, RecordingDestination>(
                new SyncPipelineOptions { PipelineId = "orders" },
                Source,
                _ => new FiniteSnapshotSource(
                    [ChangeDeliveryTestFactory.CreateCommitted(
                        Source,
                        transactionId: 7,
                        commitEndPosition: Lsn(101),
                        observer: observer)],
                    blockAfterDeliveries: true))
            .AddRebuildCutover<PositionProvider, HandoffHandler>();
        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().Single(service =>
            service.GetType().Name.Contains("SyncHostedService", StringComparison.Ordinal));
        var registry = provider.GetRequiredService<BlueTuskSyncHealthRegistry>();
        await hosted.StartAsync(TestContext.Current.CancellationToken);
        await observer.Acknowledged.WaitAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => registry.GetStatuses().SingleOrDefault()?.State is SyncPipelineState.Running,
            TestContext.Current.CancellationToken);

        var barrier = provider.GetRequiredService<ISyncRebuildCutoverBarrier>();
        await using (var lease = await barrier.AcquireAsync(
                         "orders",
                         SnapshotEpoch.Create(Source, Lsn(100)),
                         TestContext.Current.CancellationToken))
        {
            Assert.Equal(Lsn(105), lease.TargetPosition);
            await lease.CompleteHandoffAsync(
                Lsn(105),
                TestContext.Current.CancellationToken);
        }

        await WaitUntilAsync(
            () => registry.GetStatuses().SingleOrDefault() is
            { State: SyncPipelineState.Stopped, HandoffCommitted: true },
            TestContext.Current.CancellationToken);
        await hosted.StopAsync(TestContext.Current.CancellationToken);

        Assert.Contains("ack:101", events);
        Assert.Contains("handoff:105", events);
        Assert.DoesNotContain("nack:101", events);
    }

    [Fact]
    public async Task Hosted_source_failure_is_isolated_and_readiness_is_unhealthy()
    {
        var events = new List<string>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new TestTransform());
        services.AddSingleton(new RecordingDestination(events));
        services.AddBlueTuskSync()
            .AddHostedPipeline<TestTransform, RecordingDestination>(
                new SyncPipelineOptions { PipelineId = "orders" },
                Source,
                _ => throw new InvalidOperationException("source unavailable"));
        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().Single(service =>
            service.GetType().Name.Contains("SyncHostedService", StringComparison.Ordinal));
        var registry = provider.GetRequiredService<BlueTuskSyncHealthRegistry>();

        await hosted.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => registry.GetStatuses().SingleOrDefault()?.Error is not null,
            TestContext.Current.CancellationToken);
        var health = await provider.GetRequiredService<BlueTuskSyncHealthCheck>()
            .CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
        await hosted.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, health.Status);
        Assert.Contains(
            "source unavailable",
            Assert.Single(registry.GetStatuses()).Error,
            StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(10);
        while (!predicate())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TimeProvider.System.GetUtcNow() >= deadline)
            {
                throw new TimeoutException("Hosted Sync worker did not reach the expected state.");
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    private static BlueTuskLogSequenceNumber Lsn(ulong value) => new(value);

    private sealed class TestTransform : ISyncTransform
    {
        public SyncTransformVersion Version { get; } =
            SyncTransformVersion.Create("hosted-orders", "v1");

        public ValueTask<IReadOnlyList<SyncMutation>> TransformTransactionAsync(
            ChangeTransaction transaction,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<SyncMutation>>([]);

        public ValueTask<IReadOnlyList<SyncSnapshotMutation>> TransformSnapshotBatchAsync(
            ChangeSnapshotBatch batch,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<SyncSnapshotMutation>>([]);
    }

    private sealed class RecordingDestination(List<string> events) : ISyncDestination
    {
        public string Name => "recording";

        public SyncDestinationCapabilities Capabilities =>
            SyncDestinationCapabilities.IdempotentUpserts;

        public ValueTask<SyncProvisionResult> ProvisionAsync(
            SyncProvisionRequest request,
            CancellationToken cancellationToken = default)
        {
            events.Add("provision");
            return ValueTask.FromResult(new SyncProvisionResult(SyncProvisionStatus.Ready));
        }

        public ValueTask ResetSnapshotAsync(
            string pipelineId,
            SnapshotReset reset,
            CancellationToken cancellationToken = default)
        {
            events.Add("reset");
            return ValueTask.CompletedTask;
        }

        public ValueTask StartSnapshotAsync(
            string pipelineId,
            SnapshotStart start,
            SyncTransformVersion transform,
            CancellationToken cancellationToken = default)
        {
            events.Add("start");
            return ValueTask.CompletedTask;
        }

        public ValueTask ApplySnapshotBatchAsync(
            SyncSnapshotBatch batch,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask CompleteSnapshotAsync(
            string pipelineId,
            SnapshotComplete complete,
            SyncTransformVersion transform,
            CancellationToken cancellationToken = default)
        {
            events.Add("complete");
            return ValueTask.CompletedTask;
        }

        public ValueTask<SyncApplyResult> ApplyTransactionAsync(
            SyncTransactionBatch batch,
            CancellationToken cancellationToken = default)
        {
            events.Add("apply:" + batch.Transaction.CommitEndPosition.Value);
            return ValueTask.FromResult(
                SyncApplyResult.Applied(batch.Transaction.CommitEndPosition));
        }
    }

    private sealed class FiniteSnapshotSource(
        IReadOnlyList<ChangeTransactionDelivery> deliveries,
        bool blockAfterDeliveries = false) : IConsistentSnapshotSource
    {
        public ValueTask<IConsistentSnapshotAttempt> BeginAttemptAsync(
            Guid? abandonedEpoch,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IConsistentSnapshotAttempt>(
                new Attempt(deliveries, blockAfterDeliveries));

        private sealed class Attempt(
            IReadOnlyList<ChangeTransactionDelivery> deliveries,
            bool blockAfterDeliveries) : IConsistentSnapshotAttempt
        {
            private static readonly ChangeTable Table = new(
                1,
                "public",
                "orders",
                'd',
                [new ChangeColumn(0, "id", 23, -1, true)]);

            public SnapshotEpoch Epoch { get; } = SnapshotEpoch.Create(Source, Lsn(100));

            public IReadOnlyList<ChangeTable> Tables => [Table];

            public async IAsyncEnumerable<ChangeSnapshotBatch> ReadSnapshotAsync(
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask.ConfigureAwait(false);
                yield break;
            }

            public IChangeStream CreateChangeStream() =>
                new FiniteStream(deliveries, blockAfterDeliveries);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class FiniteStream(
            IReadOnlyList<ChangeTransactionDelivery> deliveries,
            bool blockAfterDeliveries) : IChangeStream
        {
            public async IAsyncEnumerable<ChangeTransactionDelivery> ReadTransactionsAsync(
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                foreach (var delivery in deliveries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return delivery;
                }

                if (blockAfterDeliveries)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
            }
        }
    }

    private sealed class RecordingObserver(List<string> events) : IChangeDeliveryObserver
    {
        private readonly TaskCompletionSource _acknowledged =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Acknowledged => _acknowledged.Task;

        public ValueTask AcknowledgeAsync(
            ChangeTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            events.Add("ack:" + transaction.CommitEndPosition.Value);
            _acknowledged.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask NackAsync(
            ChangeTransaction transaction,
            Exception? failure,
            CancellationToken cancellationToken = default)
        {
            events.Add("nack:" + transaction.CommitEndPosition.Value);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PositionProvider : ISyncCutoverPositionProvider
    {
        public ValueTask<BlueTuskLogSequenceNumber> GetDurableHeadAsync(
            string pipelineId,
            SnapshotEpoch snapshotEpoch,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Lsn(105));
    }

    private sealed class HandoffHandler(List<string>? events = null) : ISyncWorkerHandoffHandler
    {
        public ValueTask CompleteHandoffAsync(
            string pipelineId,
            BlueTuskLogSequenceNumber activatedPosition,
            CancellationToken cancellationToken = default)
        {
            events?.Add("handoff:" + activatedPosition.Value);
            return ValueTask.CompletedTask;
        }
    }
}
