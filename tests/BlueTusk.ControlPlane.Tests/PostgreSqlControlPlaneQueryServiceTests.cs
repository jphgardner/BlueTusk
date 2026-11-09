using System.Collections.Concurrent;
using System.Globalization;
using BlueTusk.Data;

namespace BlueTusk.ControlPlane.Tests;

public sealed class PostgreSqlControlPlaneQueryServiceTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(1000)]
    public async Task Unreachable_instance_is_isolated_with_bounded_concurrency_and_shared_readers(int count)
    {
        await using var dataSource = BlueTuskDataSource.Create("Host=localhost;Database=test;Username=test");
        var instances = Enumerable.Range(0, count).Select(index =>
            new ControlPlanePostgreSqlSource(index.ToString("D4", CultureInfo.InvariantCulture), dataSource, dataSource)).ToArray();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new ConcurrentDictionary<string, int>();
        var active = 0;
        var maximumActive = 0;
        var service = new PostgreSqlControlPlaneQueryService(instances, Options(), null, async (instance, token) =>
        {
            calls.AddOrUpdate(instance.InstanceName, 1, (_, previous) => previous + 1);
            var current = Interlocked.Increment(ref active);
            int previous;
            do { previous = maximumActive; }
            while (current > previous && Interlocked.CompareExchange(ref maximumActive, current, previous) != previous);
            try
            {
                await gate.Task.WaitAsync(token);
                if (instance.InstanceName == instances[^1].InstanceName)
                {
                    throw new IOException("Provider diagnostic containing Password=must-not-escape");
                }
                return [Snapshot(instance.InstanceName)];
            }
            finally { Interlocked.Decrement(ref active); }
        });
        var readers = Enumerable.Range(0, 32).Select(_ => service.GetOverviewAsync().AsTask()).ToArray();
        gate.SetResult();

        var results = await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(results, result => Assert.Same(results[0], result));
        Assert.Equal(count, calls.Count);
        Assert.All(calls.Values, value => Assert.Equal(1, value));
        Assert.InRange(maximumActive, 1, 8);
        Assert.Equal(count - 1, results[0].Sources.Count);
        Assert.Equal(count, results[0].Instances.Count);
        var failed = results[0].Instances[^1];
        Assert.Equal(ControlPlaneInstanceAvailability.Unavailable, failed.Availability);
        Assert.Equal("instance_unavailable", failed.DiagnosticCode);
        Assert.Null(failed.LastSuccessfulObservationAt);
        Assert.All(results[0].Instances.Take(count - 1), status =>
            Assert.Equal(ControlPlaneInstanceAvailability.Current, status.Availability));
    }

    [Fact]
    public async Task Stale_snapshots_keep_the_actual_observation_time_and_recover_after_synchronous_failure()
    {
        await using var dataSource = BlueTuskDataSource.Create("Host=localhost;Database=test;Username=test");
        var clock = new MutableTimeProvider();
        var fail = false;
        var calls = 0;
        var service = new PostgreSqlControlPlaneQueryService(
            [new("primary", dataSource, dataSource)], Options(), clock, (instance, _) =>
            {
                calls++;
                if (fail) throw new InvalidOperationException("private provider error");
                return ValueTask.FromResult<IReadOnlyList<ControlPlaneSourceSnapshot>>([Snapshot(instance.InstanceName)]);
            });

        var initial = await service.GetOverviewAsync();
        clock.Now += TimeSpan.FromMinutes(1);
        fail = true;
        var stale = await service.GetOverviewAsync();

        Assert.Equal(Assert.Single(initial.Sources).SourceKey, Assert.Single(stale.Sources).SourceKey);
        Assert.False(initial.Sources[0].IsStale);
        Assert.True(stale.Sources[0].IsStale);
        Assert.Equal(initial.ObservedAt, stale.Sources[0].ObservedAt);
        Assert.Equal(initial.ObservedAt, stale.Instances[0].LastSuccessfulObservationAt);
        Assert.Equal(clock.Now, stale.Instances[0].AttemptedAt);
        Assert.Equal(ControlPlaneInstanceAvailability.Stale, stale.Instances[0].Availability);
        fail = false;
        var recovered = await service.GetOverviewAsync();
        Assert.Equal(3, calls);
        Assert.Equal(ControlPlaneInstanceAvailability.Current, recovered.Instances[0].Availability);
        Assert.Equal(clock.Now, recovered.Sources[0].ObservedAt);
        Assert.Null(recovered.Instances[0].DiagnosticCode);
    }

    [Fact]
    public async Task Ignored_provider_cancellation_cannot_hold_the_refresh_or_start_duplicate_reads()
    {
        await using var dataSource = BlueTuskDataSource.Create("Host=localhost;Database=test;Username=test");
        var hung = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var service = new PostgreSqlControlPlaneQueryService(
            [new("healthy", dataSource, dataSource), new("hung", dataSource, dataSource)],
            Options(instanceTimeout: TimeSpan.FromMilliseconds(30)), null, async (instance, _) =>
            {
                if (instance.InstanceName == "hung")
                {
                    Interlocked.Increment(ref calls);
                    await hung.Task; // Deliberately ignores cancellation like a broken provider.
                }
                return [Snapshot(instance.InstanceName)];
            });
        try
        {
            for (var index = 0; index < 3; index++)
            {
                var overview = await service.GetOverviewAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal("healthy", Assert.Single(overview.Sources).InstanceName);
                Assert.Equal("instance_timeout", overview.Instances[1].DiagnosticCode);
            }
            Assert.Equal(1, calls);
        }
        finally { hung.TrySetResult(); }
    }

    [Fact]
    public async Task Overall_budget_bounds_queued_instances_and_the_next_refresh_can_retry()
    {
        await using var dataSource = BlueTuskDataSource.Create("Host=localhost;Database=test;Username=test");
        var instances = Enumerable.Range(0, 100).Select(index =>
            new ControlPlanePostgreSqlSource(index.ToString("D3", CultureInfo.InvariantCulture), dataSource, dataSource)).ToArray();
        var slow = true;
        var service = new PostgreSqlControlPlaneQueryService(instances,
            Options(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(40)), null, async (instance, token) =>
            {
                if (slow) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return [Snapshot(instance.InstanceName)];
            });

        var timedOut = await service.GetOverviewAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(timedOut.Sources);
        Assert.All(timedOut.Instances, status => Assert.Equal("refresh_budget_exceeded", status.DiagnosticCode));
        slow = false;
        // Let canceled reads release their concurrency slots before recovery.
        await Task.Delay(20);
        var recovered = await service.GetOverviewAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(100, recovered.Sources.Count);
        Assert.All(recovered.Instances, status => Assert.Equal(ControlPlaneInstanceAvailability.Current, status.Availability));
    }

    [Fact]
    public async Task Canceling_one_reader_does_not_cancel_the_shared_refresh()
    {
        await using var dataSource = BlueTuskDataSource.Create("Host=localhost;Database=test;Username=test");
        using var caller = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var service = new PostgreSqlControlPlaneQueryService([new("primary", dataSource, dataSource)],
            Options(), null, async (instance, token) =>
            {
                calls++;
                await gate.Task.WaitAsync(token);
                return [Snapshot(instance.InstanceName)];
            });
        var canceled = service.GetOverviewAsync(caller.Token).AsTask();
        var retained = service.GetOverviewAsync().AsTask();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        gate.SetResult();
        Assert.Single((await retained).Sources);
        Assert.Equal(1, calls);
    }

    private static PostgreSqlControlPlaneQueryOptions Options(TimeSpan? instanceTimeout = null, TimeSpan? budget = null) => new()
    {
        MaximumParallelInstances = 8,
        SnapshotCacheDuration = TimeSpan.Zero,
        InstanceTimeout = instanceTimeout ?? TimeSpan.FromSeconds(2),
        RefreshBudget = budget ?? TimeSpan.FromSeconds(3),
    };

    private static ControlPlaneSourceSnapshot Snapshot(string instance) => new(
        instance + ":source", instance, "source", "system", "app", "slot", "publication", 1, 1, "0/1",
        new(true, true, true, "pgoutput", "0/1", "0/1", "reserved", 0, null),
        new(1, 1, 1, 1, 1, TimeSpan.Zero), [], [], []);

    private sealed class MutableTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
