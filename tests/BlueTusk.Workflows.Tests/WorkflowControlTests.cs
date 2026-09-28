using BlueTusk.Jobs;

namespace BlueTusk.Workflows.Tests;

public sealed class WorkflowControlTests
{
    [Fact]
    public async Task StartRollbackIsAtomicWithDispatchAndConcurrentDeduplication()
    {
        await using var database = await WorkflowDatabase.CreateAsync();
        await database.Store.RegisterDefinitionAsync(database.Scope, new WorkflowDefinition
        {
            Name = "test", Version = 1, Nodes = [new() { Id = "a", Kind = WorkflowNodeKind.Activity, Activity = "a" }],
        });
        WorkflowKey rolledBack;
        await using (var connection = await database.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            rolledBack = await database.Store.StartAsync(database.Request("same"), transaction, CancellationToken.None);
            Assert.Null(await database.Store.ReadAsync(rolledBack));
            Assert.Empty(await database.Store.Jobs.ClaimAsync(database.Scope, "before-commit", 128, TimeSpan.FromMinutes(1)));
            await transaction.RollbackAsync();
        }

        Assert.Null(await database.Store.ReadAsync(rolledBack));
        var starts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => database.Store.StartAsync(database.Request("same")).AsTask()));
        Assert.Single(starts.Select(key => key.Id).Distinct());
        Assert.NotEqual(rolledBack.Id, starts[0].Id);
        Assert.Single(await database.Store.Jobs.ClaimAsync(database.Scope, "after-commit", 128, TimeSpan.FromMinutes(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Store.StartAsync(database.Request("same") with { Input = new byte[] { 99 } }).AsTask());
    }

    [Fact]
    public async Task SignalsAreIdempotentBoundedAndTenantScoped()
    {
        await using var database = await WorkflowDatabase.CreateAsync(new WorkflowOptions { MaximumSignalsPerWorkflow = 1 });
        var key = await database.StartAsync(
        [
            new() { Id = "timer", Kind = WorkflowNodeKind.Timer, Delay = TimeSpan.FromHours(1) },
            new() { Id = "signal", Kind = WorkflowNodeKind.Signal, Signal = "approve", DependsOn = ["timer"] },
        ]);
        var other = new WorkflowKey(new JobScope("tenant-b", database.Scope.Queue), key.Id);
        Assert.Null(await database.Store.ReadAsync(other));
        Assert.False(await database.Store.SignalAsync(other, "approve", "same", new byte[] { 1 }));
        Assert.False(await database.Store.CancelAsync(other));
        var duplicates = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => database.Store.SignalAsync(key, "approve", "same", new byte[] { 1 }).AsTask()));
        Assert.Equal(1, duplicates.Count(inserted => inserted));
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Store.SignalAsync(key, "approve", "same", new byte[] { 2 }).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Store.SignalAsync(key, "approve", "second", ReadOnlyMemory<byte>.Empty).AsTask());
    }

    [Fact]
    public async Task CancellationCompensatesCompletedWorkAndStopsFutureNodes()
    {
        await using var database = await WorkflowDatabase.CreateAsync();
        var key = await database.StartAsync(
        [
            new() { Id = "a", Kind = WorkflowNodeKind.Activity, Activity = "a", Compensation = "undo" },
            new() { Id = "wait", Kind = WorkflowNodeKind.Signal, Signal = "never", DependsOn = ["a"] },
        ]);
        int compensated = 0;
        var registry = new WorkflowActivityRegistry()
            .Register("a", (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1 }))
            .Register("undo", (_, _) => { Interlocked.Increment(ref compensated); return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty); });
        using var stop = new CancellationTokenSource();
        Task running = new WorkflowWorker(database.Store, database.Scope, "cancel", registry, WorkflowDatabase.WorkerOptions).RunAsync(stop.Token);
        try
        {
            await WorkflowDatabase.WaitUntilAsync(async () => (await database.Store.ReadNodesAsync(key)).Single(node => node.Id == "a").Status == WorkflowNodeStatus.Completed);
            Assert.True(await database.Store.CancelAsync(key));
            await WorkflowDatabase.WaitUntilAsync(async () => (await database.Store.ReadAsync(key))!.Status == WorkflowStatus.Canceled);
            Assert.Equal(1, compensated);
            Assert.False(await database.Store.SignalAsync(key, "never", "late", ReadOnlyMemory<byte>.Empty));
            Assert.Equal(WorkflowNodeStatus.Compensated, (await database.Store.ReadNodesAsync(key)).Single(node => node.Id == "a").Status);
            Assert.Equal(WorkflowNodeStatus.Skipped, (await database.Store.ReadNodesAsync(key)).Single(node => node.Id == "wait").Status);
            Assert.True((await database.Store.ReplayAsync(key))!.MatchesPersistedState);
            Assert.Equal(1, await database.Store.PruneAsync(database.Scope, TimeSpan.Zero, 1));
            Assert.Null(await database.Store.ReadAsync(key));
            Assert.Empty(await database.Store.ReadHistoryAsync(key, 0, 128));
        }
        finally
        {
            await stop.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task QuiescentMigrationPreservesCompletedContractsAndStartIdentity()
    {
        await using var database = await WorkflowDatabase.CreateAsync();
        WorkflowNode[] original =
        [
            new() { Id = "a", Kind = WorkflowNodeKind.Activity, Activity = "a" },
            new() { Id = "wait", Kind = WorkflowNodeKind.Signal, Signal = "resume", DependsOn = ["a"] },
        ];
        var key = await database.StartAsync(original, "migration-start");
        WorkflowNode[] target = [.. original, new() { Id = "b", Kind = WorkflowNodeKind.Activity, Activity = "b", DependsOn = ["wait"] }];
        await database.Store.RegisterDefinitionAsync(database.Scope, new WorkflowDefinition { Name = "test", Version = 2, Nodes = target });
        Assert.False(await database.Store.MigrateAsync(key, 2, (await database.Store.ReadAsync(key))!.Revision));
        int calls = 0;
        var registry = new WorkflowActivityRegistry()
            .Register("a", (_, _) => { Interlocked.Increment(ref calls); return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty); })
            .Register("b", (_, _) => ValueTask.FromResult(ReadOnlyMemory<byte>.Empty));
        using var stop = new CancellationTokenSource();
        Task running = new WorkflowWorker(database.Store, database.Scope, "migrate", registry, WorkflowDatabase.WorkerOptions).RunAsync(stop.Token);
        try
        {
            await WorkflowDatabase.WaitUntilAsync(async () => (await database.Store.ReadNodesAsync(key)).Single(node => node.Id == "a").Status == WorkflowNodeStatus.Completed);
            long revision = (await database.Store.ReadAsync(key))!.Revision;
            Assert.False(await database.Store.MigrateAsync(key, 2, revision - 1));
            Assert.True(await database.Store.MigrateAsync(key, 2, revision));
            Assert.Equal(2, (await database.Store.ReadAsync(key))!.Version);
            Assert.Equal(key, await database.Store.StartAsync(database.Request("migration-start")));
            await database.Store.RegisterDefinitionAsync(database.Scope, new WorkflowDefinition
            {
                Name = "test", Version = 3, Nodes = [original[0] with { Activity = "changed" }, original[1]],
            });
            long migratedRevision = (await database.Store.ReadAsync(key))!.Revision;
            await Assert.ThrowsAsync<InvalidOperationException>(() => database.Store.MigrateAsync(key, 3, migratedRevision).AsTask());
            Assert.True(await database.Store.SignalAsync(key, "resume", "signal", ReadOnlyMemory<byte>.Empty));
            await WorkflowDatabase.WaitUntilAsync(async () => (await database.Store.ReadAsync(key))!.Status == WorkflowStatus.Succeeded);
            Assert.Equal(1, calls);
        }
        finally
        {
            await stop.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
