using BlueTusk.Jobs;

namespace BlueTusk.Workflows.Tests;

public sealed class WorkflowDurableFormatTests
{
    private static readonly string[] ExpectedDefinitionMigrationCodes = ["v2", "v1"];

    [Fact]
    public async Task QuiescentDefinitionUpgradeAndRollbackPreserveCompletedContractsAndStartIdentity()
    {
        await using var database = await WorkflowDatabase.CreateAsync();
        WorkflowNode[] original =
        [
            new() { Id = "committed", Kind = WorkflowNodeKind.Activity, Activity = "original" },
            new() { Id = "wait", Kind = WorkflowNodeKind.Signal, Signal = "continue", DependsOn = ["committed"] },
        ];
        var key = await database.StartAsync(original, "definition-upgrade-rollback");
        await database.Store.RegisterDefinitionAsync(database.Scope, new WorkflowDefinition
        {
            Name = "test",
            Version = 2,
            Nodes = [.. original, new() { Id = "new-node", Kind = WorkflowNodeKind.Activity, Activity = "new-version", DependsOn = ["wait"] }],
        });
        int committedCalls = 0;
        int removedCalls = 0;
        var registry = new WorkflowActivityRegistry()
            .Register("original", (_, _) => { Interlocked.Increment(ref committedCalls); return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1 }); })
            .Register("new-version", (_, _) => { Interlocked.Increment(ref removedCalls); return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty); });
        using var stop = new CancellationTokenSource();
        Task running = new WorkflowWorker(database.Store, database.Scope, "definition-rollback", registry, WorkflowDatabase.WorkerOptions).RunAsync(stop.Token);
        try
        {
            await WorkflowDatabase.WaitUntilAsync(async () => (await database.Store.ReadNodesAsync(key)).Single(node => node.Id == "committed").Status == WorkflowNodeStatus.Completed);
            long revision = (await database.Store.ReadAsync(key))!.Revision;
            Assert.True(await database.Store.MigrateAsync(key, 2, revision));
            Assert.Equal(2, (await database.Store.ReadAsync(key))!.Version);
            Assert.True((await database.Store.ReplayAsync(key))!.MatchesPersistedState);
            var reopened = new PostgreSqlWorkflowStore(database.Source, database.Options, database.JobOptions);
            await reopened.InitializeAsync();
            Assert.False(await reopened.MigrateAsync(key, 1, revision));
            Assert.True(await reopened.MigrateAsync(key, 1, (await reopened.ReadAsync(key))!.Revision));
            Assert.Equal(key, await reopened.StartAsync(database.Request("definition-upgrade-rollback")));
            var rolledBackNodes = await reopened.ReadNodesAsync(key);
            Assert.DoesNotContain(rolledBackNodes, node => node.Id == "new-node");
            var committedNode = rolledBackNodes.Single(node => node.Id == "committed");
            Assert.Equal(WorkflowNodeStatus.Completed, committedNode.Status);
            Assert.Equal(1, committedNode.Result.Span[0]);
            Assert.True(await reopened.SignalAsync(key, "continue", "finish-original-version", ReadOnlyMemory<byte>.Empty));
            Assert.Equal(WorkflowStatus.Succeeded, (await reopened.ReadAsync(key))!.Status);
            Assert.Equal(1, committedCalls);
            Assert.Equal(0, removedCalls);
            Assert.True((await reopened.ReplayAsync(key))!.MatchesPersistedState);
            var history = await reopened.ReadHistoryAsync(key, 0, 128);
            Assert.Equal(ExpectedDefinitionMigrationCodes, history.Where(row => row.Event == "migrated").Select(row => row.Code));
        }
        finally
        {
            await stop.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task SameFormatOperationalTuningAndRollbackResumeSignalsTimersWithoutRepeatingCompletedActivity()
    {
        await using var database = await WorkflowDatabase.CreateAsync();
        var key = await database.StartAsync(
        [
            new() { Id = "a", Kind = WorkflowNodeKind.Activity, Activity = "a" },
            new() { Id = "wait", Kind = WorkflowNodeKind.Signal, Signal = "continue", DependsOn = ["a"] },
            new() { Id = "timer", Kind = WorkflowNodeKind.Timer, Delay = TimeSpan.FromMilliseconds(30), DependsOn = ["wait"] },
            new() { Id = "finish", Kind = WorkflowNodeKind.Activity, Activity = "finish", DependsOn = ["timer"] },
        ], "same-format");
        int firstCalls = 0;
        int finalCalls = 0;
        var registry = new WorkflowActivityRegistry()
            .Register("a", (_, _) => { Interlocked.Increment(ref firstCalls); return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 7 }); })
            .Register("finish", (_, _) => { Interlocked.Increment(ref finalCalls); return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty); });
        using (var initialStop = new CancellationTokenSource())
        {
            Task initial = new WorkflowWorker(database.Store, database.Scope, "original", registry, WorkflowDatabase.WorkerOptions).RunAsync(initialStop.Token);
            try
            {
                await WorkflowDatabase.WaitUntilAsync(async () => (await database.Store.ReadNodesAsync(key)).Single(node => node.Id == "a").Status == WorkflowNodeStatus.Completed);
            }
            finally
            {
                await initialStop.CancelAsync();
                await initial.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        var before = await database.Store.ReadAsync(key);
        var reopened = new PostgreSqlWorkflowStore(database.Source, database.Options with
        {
            MaximumBatchSize = 1,
            MaximumNodeReadBytes = 2_097_152,
            CommandTimeoutSeconds = 40,
        }, database.JobOptions with { MaximumClaimBatch = 1 });
        await reopened.InitializeAsync();
        Assert.Equal(before, await reopened.ReadAsync(key));
        Assert.Equal(key, await reopened.StartAsync(database.Request("same-format")));
        Assert.True((await reopened.ReplayAsync(key))!.MatchesPersistedState);
        Assert.True(await reopened.SignalAsync(key, "continue", "buffered-during-reopen", ReadOnlyMemory<byte>.Empty));

        // Restore the initial process configuration against the unchanged format-one schema.
        await database.Store.InitializeAsync();
        using var stop = new CancellationTokenSource();
        Task resumed = new WorkflowWorker(database.Store, database.Scope, "rolled-back-configuration", registry, WorkflowDatabase.WorkerOptions).RunAsync(stop.Token);
        try
        {
            await WorkflowDatabase.WaitUntilAsync(async () => (await database.Store.ReadAsync(key))!.Status == WorkflowStatus.Succeeded);
            Assert.Equal(1, firstCalls);
            Assert.Equal(1, finalCalls);
            Assert.True((await database.Store.ReplayAsync(key))!.MatchesPersistedState);
            var history = await database.Store.ReadHistoryAsync(key, 0, 128);
            Assert.Single(history, row => row.Event == "activity_completed" && row.NodeId == "a");
            Assert.Single(history, row => row.Event == "timer_fired");
        }
        finally
        {
            await stop.CancelAsync();
            await resumed.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task UnsupportedFormatOrDurableFingerprintRejectsInitializationWithoutChangingInstancesAndDispatch()
    {
        await using var database = await WorkflowDatabase.CreateAsync();
        var key = await database.StartAsync([new() { Id = "wait", Kind = WorkflowNodeKind.Signal, Signal = "continue" }], "preserve");
        var before = await database.Store.ReadAsync(key);
        var mismatch = new PostgreSqlWorkflowStore(database.Source, database.Options with { MaximumNodes = 64 }, database.JobOptions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mismatch.InitializeAsync().AsTask());
        Assert.Equal(before, await database.Store.ReadAsync(key));

        // A future-format marker is intentionally unsupported; no schema migration is attempted.
        await database.ExecuteAsync("UPDATE {schema}.settings SET format = 2");
        var reopened = new PostgreSqlWorkflowStore(database.Source, database.Options, database.JobOptions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.InitializeAsync().AsTask());
        Assert.Equal(before, await database.Store.ReadAsync(key));
        Assert.Empty(await database.Store.Jobs.ClaimAsync(database.Scope, "no-new-dispatch", 1, TimeSpan.FromMinutes(1)));
        await database.ExecuteAsync("UPDATE {schema}.settings SET format = 1");
        await reopened.InitializeAsync();
        Assert.Equal(key, await reopened.StartAsync(database.Request("preserve")));
        Assert.True(await reopened.SignalAsync(key, "continue", "after-header-restoration", new byte[] { 9 }));
        Assert.Equal(WorkflowStatus.Succeeded, (await reopened.ReadAsync(key))!.Status);
        Assert.True((await reopened.ReplayAsync(key))!.MatchesPersistedState);
    }
}
