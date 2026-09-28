using BlueTusk.Jobs;

namespace BlueTusk.Workflows.Tests;

public sealed class WorkflowWorkerBoundaryTests
{
    [Fact]
    public async Task OlderWorkerLeavesUnsupportedActivityVersionForNewWorker()
    {
        await using var database = await WorkflowDatabase.CreateAsync();
        var key = await database.StartAsync([new() { Id = "a", Kind = WorkflowNodeKind.Activity, Activity = "activity.v2" }]);
        int oldCalls = 0;
        var oldRegistry = new WorkflowActivityRegistry().Register("activity.v1", (_, _) =>
        {
            Interlocked.Increment(ref oldCalls);
            return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
        });
        using var oldStop = new CancellationTokenSource();
        Task old = new WorkflowWorker(database.Store, database.Scope, "old", oldRegistry, WorkflowDatabase.WorkerOptions).RunAsync(oldStop.Token);
        using var newStop = new CancellationTokenSource();
        Task? newer = null;
        try
        {
            await Task.Delay(100);
            Assert.Equal(WorkflowNodeStatus.Scheduled, Assert.Single(await database.Store.ReadNodesAsync(key)).Status);
            var registry = new WorkflowActivityRegistry().Register("activity.v2", (_, _) => ValueTask.FromResult(ReadOnlyMemory<byte>.Empty));
            newer = new WorkflowWorker(database.Store, database.Scope, "new", registry, WorkflowDatabase.WorkerOptions).RunAsync(newStop.Token);
            await WorkflowDatabase.WaitUntilAsync(async () => (await database.Store.ReadAsync(key))!.Status == WorkflowStatus.Succeeded);
            Assert.Equal(0, oldCalls);
        }
        finally
        {
            await oldStop.CancelAsync();
            await newStop.CancelAsync();
            await old.WaitAsync(TimeSpan.FromSeconds(10));
            if (newer is not null)
            {
                await newer.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    [Fact]
    public async Task ActiveCancellationStopsHandlerAndRecoveryClosesWorkflow()
    {
        await using var database = await WorkflowDatabase.CreateAsync();
        var key = await database.StartAsync([new() { Id = "a", Kind = WorkflowNodeKind.Activity, Activity = "blocked" }]);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new WorkflowActivityRegistry().Register("blocked", async (_, token) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return ReadOnlyMemory<byte>.Empty;
            }
            finally
            {
                stopped.TrySetResult();
            }
        });
        using var stop = new CancellationTokenSource();
        Task running = new WorkflowWorker(database.Store, database.Scope, "active-cancel", registry, WorkflowDatabase.WorkerOptions).RunAsync(stop.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(await database.Store.CancelAsync(key));
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WorkflowDatabase.WaitUntilAsync(async () => (await database.Store.ReadAsync(key))!.Status == WorkflowStatus.Canceled);
            Assert.Equal(WorkflowNodeStatus.Skipped, Assert.Single(await database.Store.ReadNodesAsync(key)).Status);
        }
        finally
        {
            await stop.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task PermanentCompensationFailureRemainsVisible()
    {
        await using var database = await WorkflowDatabase.CreateAsync();
        var key = await database.StartAsync(
        [
            new() { Id = "a", Kind = WorkflowNodeKind.Activity, Activity = "a", Compensation = "undo" },
            new() { Id = "fail", Kind = WorkflowNodeKind.Activity, Activity = "fail", DependsOn = ["a"] },
        ]);
        var registry = new WorkflowActivityRegistry()
            .Register("a", (_, _) => ValueTask.FromResult(ReadOnlyMemory<byte>.Empty))
            .Register("fail", (_, _) => ValueTask.FromException<ReadOnlyMemory<byte>>(new JobHandlerException("declined", retryable: false)))
            .Register("undo", (_, _) => ValueTask.FromException<ReadOnlyMemory<byte>>(new JobHandlerException("undo_failed", retryable: false)));
        using var stop = new CancellationTokenSource();
        Task running = new WorkflowWorker(database.Store, database.Scope, "failed-compensation", registry, WorkflowDatabase.WorkerOptions).RunAsync(stop.Token);
        try
        {
            await WorkflowDatabase.WaitUntilAsync(async () => (await database.Store.ReadAsync(key))!.Status == WorkflowStatus.Failed);
            Assert.Equal("compensation_failed", (await database.Store.ReadAsync(key))!.FailureCode);
            Assert.Equal("undo_failed", (await database.Store.ReadNodesAsync(key)).Single(node => node.Id == "a").FailureCode);
        }
        finally
        {
            await stop.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task NodeResultPagesHonorAggregateByteBudgetAndKeyset()
    {
        await using var database = await WorkflowDatabase.CreateAsync(new WorkflowOptions
        {
            MaximumResultBytes = 4,
            MaximumSignalBytes = 4,
            MaximumNodeReadBytes = 4,
        });
        var key = await database.StartAsync(
        [
            new() { Id = "a", Kind = WorkflowNodeKind.Activity, Activity = "produce" },
            new() { Id = "b", Kind = WorkflowNodeKind.Activity, Activity = "produce" },
        ]);
        var registry = new WorkflowActivityRegistry().Register("produce", (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[4]));
        using var stop = new CancellationTokenSource();
        Task running = new WorkflowWorker(database.Store, database.Scope, "read-budget", registry, WorkflowDatabase.WorkerOptions).RunAsync(stop.Token);
        try
        {
            await WorkflowDatabase.WaitUntilAsync(async () => (await database.Store.ReadAsync(key))!.Status == WorkflowStatus.Succeeded);
            var first = Assert.Single(await database.Store.ReadNodesPageAsync(key, null, 128));
            Assert.Equal("a", first.Id);
            var second = Assert.Single(await database.Store.ReadNodesPageAsync(key, first.Id, 128));
            Assert.Equal("b", second.Id);
            Assert.Empty(await database.Store.ReadNodesPageAsync(key, second.Id, 128));
        }
        finally
        {
            await stop.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
