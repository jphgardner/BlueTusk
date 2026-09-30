using BlueTusk.Data;
using BlueTusk.Jobs;

namespace BlueTusk.Workflows.Tests;

public sealed class WorkflowValidationTests
{
    [Fact]
    public async Task CyclesAndUnknownDependenciesAreRejectedBeforeOpeningDatabase()
    {
        await using var source = BlueTuskDataSource.Create("Host=127.0.0.1;Port=1;Username=postgres;Database=test;SSL Mode=Disable");
        var store = new PostgreSqlWorkflowStore(source);
        var scope = new JobScope("tenant", "queue");
        var cyclic = new WorkflowDefinition
        {
            Name = "cycle",
            Version = 1,
            Nodes =
            [
                new() { Id = "a", Kind = WorkflowNodeKind.Activity, Activity = "a", DependsOn = ["b"] },
                new() { Id = "b", Kind = WorkflowNodeKind.Activity, Activity = "b", DependsOn = ["a"] },
            ],
        };
        await Assert.ThrowsAsync<ArgumentException>(() => store.RegisterDefinitionAsync(scope, cyclic).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.RegisterDefinitionAsync(scope, cyclic with
        {
            Nodes = [new() { Id = "a", Kind = WorkflowNodeKind.Activity, Activity = "a", DependsOn = ["missing"] }],
        }).AsTask());
    }

    [Fact]
    public async Task DefinitionVersionsAreCanonicalAndImmutable()
    {
        await using var database = await WorkflowDatabase.CreateAsync();
        var definition = new WorkflowDefinition
        {
            Name = "test",
            Version = 1,
            Nodes =
            [
                new() { Id = "a", Kind = WorkflowNodeKind.Activity, Activity = "a" },
                new() { Id = "b", Kind = WorkflowNodeKind.Activity, Activity = "b" },
                new() { Id = "join", Kind = WorkflowNodeKind.Join, DependsOn = ["a", "b"] },
            ],
        };
        await database.Store.RegisterDefinitionAsync(database.Scope, definition);
        await database.Store.RegisterDefinitionAsync(database.Scope, definition with
        {
            Nodes = [definition.Nodes[2] with { DependsOn = ["b", "a"] }, definition.Nodes[1], definition.Nodes[0]],
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Store.RegisterDefinitionAsync(database.Scope, definition with
        {
            Nodes = [definition.Nodes[0] with { Activity = "changed" }, definition.Nodes[1], definition.Nodes[2]],
        }).AsTask());
        await database.ExecuteAsync("UPDATE {schema}.definitions SET fingerprint = decode('ff', 'hex')");
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Store.StartAsync(database.Request()).AsTask());
    }

    [Fact]
    public async Task ActivityFanInIsRejectedBeforeMaterializingOversizeDependencyPayloads()
    {
        await using var database = await WorkflowDatabase.CreateAsync(new WorkflowOptions
        {
            MaximumInputBytes = 1,
            MaximumResultBytes = 4,
            MaximumSignalBytes = 4,
            MaximumActivityInputBytes = 5,
        });
        var key = await database.StartAsync(
        [
            new() { Id = "a", Kind = WorkflowNodeKind.Activity, Activity = "produce" },
            new() { Id = "b", Kind = WorkflowNodeKind.Activity, Activity = "produce" },
            new() { Id = "fanin", Kind = WorkflowNodeKind.Activity, Activity = "fanin", DependsOn = ["a", "b"] },
        ]);
        int fanin = 0;
        var registry = new WorkflowActivityRegistry()
            .Register("produce", (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[4]))
            .Register("fanin", (_, _) => { Interlocked.Increment(ref fanin); return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty); });
        using var stop = new CancellationTokenSource();
        Task running = new WorkflowWorker(database.Store, database.Scope, "fanin", registry, WorkflowDatabase.WorkerOptions).RunAsync(stop.Token);
        try
        {
            await WorkflowDatabase.WaitUntilAsync(async () => (await database.Store.ReadAsync(key))!.Status == WorkflowStatus.Failed);
            Assert.Equal("activity_input_limit", (await database.Store.ReadAsync(key))!.FailureCode);
            Assert.Equal(0, fanin);
        }
        finally
        {
            await stop.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task LifetimeHistoryLimitFailsExplicitlyInsteadOfGrowingWithoutBound()
    {
        await using var database = await WorkflowDatabase.CreateAsync(new WorkflowOptions { MaximumNodes = 1, MaximumHistoryEntries = 12 });
        var key = await database.StartAsync([new() { Id = "a", Kind = WorkflowNodeKind.Activity, Activity = "retry", MaximumAttempts = 100 }]);
        var registry = new WorkflowActivityRegistry().Register("retry", (_, _) => ValueTask.FromException<ReadOnlyMemory<byte>>(new JobHandlerException("busy")));
        using var stop = new CancellationTokenSource();
        Task running = new WorkflowWorker(database.Store, database.Scope, "history", registry, WorkflowDatabase.WorkerOptions).RunAsync(stop.Token);
        try
        {
            await WorkflowDatabase.WaitUntilAsync(async () => (await database.Store.ReadAsync(key))!.Status == WorkflowStatus.Failed);
            Assert.Equal("history_limit", (await database.Store.ReadAsync(key))!.FailureCode);
            var history = await database.Store.ReadHistoryAsync(key, 0, 128);
            Assert.InRange(history.Count, 1, 12);
            Assert.Contains(history, entry => entry.Code == "history_limit");
        }
        finally
        {
            await stop.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
