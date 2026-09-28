using System.Collections.Concurrent;
using BlueTusk.Jobs;

namespace BlueTusk.Workflows.Tests;

public sealed class WorkflowTests
{
    [Fact]
    public async Task ParallelActivitiesJoinBufferedSignalAndDurableTimerComplete()
    {
        await using var database = await WorkflowDatabase.CreateAsync();
        WorkflowNode[] nodes =
        [
            new() { Id = "a", Kind = WorkflowNodeKind.Activity, Activity = "a.v1" },
            new() { Id = "b", Kind = WorkflowNodeKind.Activity, Activity = "b.v1" },
            new() { Id = "join", Kind = WorkflowNodeKind.Join, DependsOn = ["a", "b"] },
            new() { Id = "approval", Kind = WorkflowNodeKind.Signal, Signal = "approved", DependsOn = ["join"] },
            new() { Id = "timer", Kind = WorkflowNodeKind.Timer, Delay = TimeSpan.FromMilliseconds(150), DependsOn = ["approval"] },
            new() { Id = "finish", Kind = WorkflowNodeKind.Activity, Activity = "finish.v1", DependsOn = ["timer"] },
        ];
        var key = await database.StartAsync(nodes);
        Assert.True(await database.Store.SignalAsync(key, "approved", "approval-1", new byte[] { 7 }));
        int parallel = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new WorkflowActivityRegistry();
        foreach (string name in new[] { "a.v1", "b.v1" })
        {
            registry.Register(name, async (context, token) =>
            {
                Assert.Equal(42, context.InitialInput.Span[0]);
                if (Interlocked.Increment(ref parallel) == 2)
                {
                    both.TrySetResult();
                }

                await both.Task.WaitAsync(token);
                return new byte[] { context.NodeId == "a" ? (byte)1 : (byte)2 };
            });
        }

        registry.Register("finish.v1", (context, _) =>
        {
            Assert.True(context.DependencyResults.ContainsKey("timer"));
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 9 });
        });
        var worker = new WorkflowWorker(database.Store, database.Scope, "parallel", registry, WorkflowDatabase.WorkerOptions);
        using var stop = new CancellationTokenSource();
        Task running = worker.RunAsync(stop.Token);
        try
        {
            await WorkflowDatabase.WaitUntilAsync(async () => (await database.Store.ReadAsync(key))!.Status == WorkflowStatus.Succeeded);
            var completed = await database.Store.ReadNodesAsync(key);
            Assert.All(completed, node => Assert.Equal(WorkflowNodeStatus.Completed, node.Status));
            Assert.Equal(7, completed.Single(node => node.Id == "approval").Result.Span[0]);
            Assert.Equal(9, completed.Single(node => node.Id == "finish").Result.Span[0]);
            var history = await database.Store.ReadHistoryAsync(key, 0, 128);
            Assert.Contains(history, entry => entry.Event == "joined");
            var scheduled = history.Single(entry => entry.Event == "timer_scheduled");
            var fired = history.Single(entry => entry.Event == "timer_fired");
            Assert.True(fired.Timestamp - scheduled.Timestamp >= TimeSpan.FromMilliseconds(140));
            Assert.Equal(Enumerable.Range(1, history.Count).Select(value => (long)value), history.Select(entry => entry.Sequence));
            Assert.True((await database.Store.ReplayAsync(key))!.MatchesPersistedState);
        }
        finally
        {
            await stop.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task RetryKeepsStableExternalIdentityAndCompletesOnlyOnce()
    {
        await using var database = await WorkflowDatabase.CreateAsync();
        var key = await database.StartAsync([new() { Id = "activity", Kind = WorkflowNodeKind.Activity, Activity = "retry.v1", MaximumAttempts = 3 }]);
        var identities = new ConcurrentBag<string>();
        int calls = 0;
        var registry = new WorkflowActivityRegistry().Register("retry.v1", (context, _) =>
        {
            identities.Add(context.IdempotencyKey);
            Interlocked.Increment(ref calls);
            return context.Attempt < 3 ? ValueTask.FromException<ReadOnlyMemory<byte>>(new JobHandlerException("temporary"))
                : ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 3 });
        });
        var worker = new WorkflowWorker(database.Store, database.Scope, "retry", registry, WorkflowDatabase.WorkerOptions);
        using var stop = new CancellationTokenSource();
        Task running = worker.RunAsync(stop.Token);
        try
        {
            await WorkflowDatabase.WaitUntilAsync(async () => (await database.Store.ReadAsync(key))!.Status == WorkflowStatus.Succeeded);
            Assert.Equal(3, calls);
            Assert.Single(identities.Distinct(StringComparer.Ordinal));
            var node = Assert.Single(await database.Store.ReadNodesAsync(key));
            Assert.Equal(3, node.FencingToken);
            var history = await database.Store.ReadHistoryAsync(key, 0, 128);
            Assert.Equal(2, history.Count(entry => entry.Event == "activity_retry"));
            Assert.Single(history, entry => entry.Event == "activity_completed");
        }
        finally
        {
            await stop.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task PermanentFailureCompensatesInReverseCompletionOrder()
    {
        await using var database = await WorkflowDatabase.CreateAsync();
        var key = await database.StartAsync(
        [
            new() { Id = "a", Kind = WorkflowNodeKind.Activity, Activity = "a", Compensation = "undo-a" },
            new() { Id = "b", Kind = WorkflowNodeKind.Activity, Activity = "b", Compensation = "undo-b", DependsOn = ["a"] },
            new() { Id = "c", Kind = WorkflowNodeKind.Activity, Activity = "fail", DependsOn = ["b"] },
        ]);
        var order = new ConcurrentQueue<string>();
        var registry = new WorkflowActivityRegistry()
            .Register("a", (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1 }))
            .Register("b", (context, _) =>
            {
                Assert.Equal(1, context.DependencyResults["a"].Span[0]);
                return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 2 });
            })
            .Register("fail", (_, _) => ValueTask.FromException<ReadOnlyMemory<byte>>(new JobHandlerException("declined", retryable: false)));
        foreach (string compensation in new[] { "undo-a", "undo-b" })
        {
            registry.Register(compensation, (context, _) =>
            {
                Assert.True(context.IsCompensation);
                Assert.NotEmpty(context.ActivityResult.ToArray());
                order.Enqueue(context.NodeId);
                return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
            });
        }

        var worker = new WorkflowWorker(database.Store, database.Scope, "compensate", registry, WorkflowDatabase.WorkerOptions);
        using var stop = new CancellationTokenSource();
        Task running = worker.RunAsync(stop.Token);
        try
        {
            await WorkflowDatabase.WaitUntilAsync(async () => (await database.Store.ReadAsync(key))!.Status == WorkflowStatus.Failed);
            Assert.Equal("b,a", string.Join(',', order));
            Assert.Equal("declined", (await database.Store.ReadAsync(key))!.FailureCode);
            var nodes = await database.Store.ReadNodesAsync(key);
            Assert.Equal(WorkflowNodeStatus.Compensated, nodes.Single(node => node.Id == "a").Status);
            Assert.Equal(WorkflowNodeStatus.Compensated, nodes.Single(node => node.Id == "b").Status);
            Assert.Equal(WorkflowNodeStatus.Failed, nodes.Single(node => node.Id == "c").Status);
            Assert.True((await database.Store.ReplayAsync(key))!.MatchesPersistedState);
        }
        finally
        {
            await stop.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task RestartResumesSignalWaitWithoutReexecutingCommittedActivity()
    {
        await using var database = await WorkflowDatabase.CreateAsync();
        var key = await database.StartAsync(
        [
            new() { Id = "a", Kind = WorkflowNodeKind.Activity, Activity = "a" },
            new() { Id = "wait", Kind = WorkflowNodeKind.Signal, Signal = "resume", DependsOn = ["a"] },
            new() { Id = "b", Kind = WorkflowNodeKind.Activity, Activity = "b", DependsOn = ["wait"] },
        ]);
        int calls = 0;
        var registry = new WorkflowActivityRegistry()
            .Register("a", (_, _) => { Interlocked.Increment(ref calls); return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty); })
            .Register("b", (_, _) => ValueTask.FromResult(ReadOnlyMemory<byte>.Empty));
        using (var stop = new CancellationTokenSource())
        {
            Task running = new WorkflowWorker(database.Store, database.Scope, "first", registry, WorkflowDatabase.WorkerOptions).RunAsync(stop.Token);
            try
            {
                await WorkflowDatabase.WaitUntilAsync(async () => (await database.Store.ReadNodesAsync(key)).Single(node => node.Id == "a").Status == WorkflowNodeStatus.Completed);
            }
            finally
            {
                await stop.CancelAsync();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        await database.ExecuteAsync("UPDATE {jobs}.jobs SET lease_expires = clock_timestamp() - interval '1 second' WHERE status = 1");
        var restarted = new PostgreSqlWorkflowStore(database.Source, database.Options, database.JobOptions);
        await restarted.InitializeAsync();
        Assert.True(await restarted.SignalAsync(key, "resume", "signal", ReadOnlyMemory<byte>.Empty));
        using var resumedStop = new CancellationTokenSource();
        Task resumed = new WorkflowWorker(restarted, database.Scope, "second", registry, WorkflowDatabase.WorkerOptions).RunAsync(resumedStop.Token);
        try
        {
            await WorkflowDatabase.WaitUntilAsync(async () => (await restarted.ReadAsync(key))!.Status == WorkflowStatus.Succeeded);
            Assert.Equal(1, calls);
        }
        finally
        {
            await resumedStop.CancelAsync();
            await resumed.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
