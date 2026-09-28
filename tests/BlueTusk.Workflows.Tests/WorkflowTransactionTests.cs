using BlueTusk.Data;
using BlueTusk.Jobs;

namespace BlueTusk.Workflows.Tests;

public sealed class WorkflowTransactionTests
{
    [Fact]
    public async Task TransactionalActivityRollsBackFailedAttemptThenCommitsEffectWithOutcome()
    {
        await using var database = await WorkflowDatabase.CreateAsync();
        await database.ExecuteAsync("CREATE TABLE {schema}.effects (workflow uuid PRIMARY KEY)");
        var key = await database.StartAsync(
        [
            new() { Id = "effect", Kind = WorkflowNodeKind.Activity, Activity = "effect", MaximumAttempts = 2 },
            new() { Id = "child", Kind = WorkflowNodeKind.Activity, Activity = "child", DependsOn = ["effect"] },
        ]);
        int calls = 0;
        var registry = new WorkflowActivityRegistry()
            .RegisterTransactional("effect", async (connection, transaction, context, token) =>
            {
                Interlocked.Increment(ref calls);
                await using var command = new BlueTuskCommand($"INSERT INTO \"{database.Options.Schema}\".effects VALUES (@id)", connection) { Transaction = transaction };
                command.Parameters.Add(new BlueTuskParameter<Guid>(context.Workflow.Id) { ParameterName = "id" });
                _ = await command.ExecuteNonQueryAsync(token);
                if (context.Attempt == 1)
                {
                    throw new JobHandlerException("transaction_retry");
                }

                return new byte[] { 9 };
            })
            .Register("child", (context, _) =>
            {
                Assert.Equal(9, context.DependencyResults["effect"].Span[0]);
                return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
            });
        using var stop = new CancellationTokenSource();
        Task running = new WorkflowWorker(database.Store, database.Scope, "transaction", registry, WorkflowDatabase.WorkerOptions).RunAsync(stop.Token);
        try
        {
            await WorkflowDatabase.WaitUntilAsync(async () => (await database.Store.ReadAsync(key))!.Status == WorkflowStatus.Succeeded);
            Assert.Equal(2, calls);
            await using var connection = await database.Source.OpenConnectionAsync();
            await using var count = new BlueTuskCommand($"SELECT count(*) FROM \"{database.Options.Schema}\".effects", connection);
            Assert.Equal(1L, await count.ExecuteScalarAsync<long>(CancellationToken.None));
            var history = await database.Store.ReadHistoryAsync(key, 0, 128);
            Assert.Single(history, entry => entry.Event == "activity_completed" && entry.NodeId == "effect");
            Assert.Single(history, entry => entry.Event == "activity_scheduled" && entry.NodeId == "child");
        }
        finally
        {
            await stop.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task TransactionalActivityDeadlineRollsBackBusinessEffectAndWorkflowCompletion()
    {
        await using var database = await WorkflowDatabase.CreateAsync();
        await database.ExecuteAsync("CREATE TABLE {schema}.effects (value integer PRIMARY KEY)");
        var key = await database.StartAsync([new() { Id = "effect", Kind = WorkflowNodeKind.Activity, Activity = "effect" }]);
        var lease = Assert.Single(await database.Store.Jobs.ClaimAsync(database.Scope, "deadline", 1, TimeSpan.FromMilliseconds(600)));
        var dispatch = new WorkflowDispatch(key.Id, "effect", false);
        var context = await database.Store.PrepareAsync(lease, dispatch, CancellationToken.None);
        Assert.NotNull(context);
        await database.Store.ExecuteTransactionalActivityAsync(lease, dispatch, context, async (connection, transaction, ignoredContext, token) =>
        {
            await using var command = new BlueTuskCommand($"INSERT INTO \"{database.Options.Schema}\".effects VALUES (42)", connection) { Transaction = transaction };
            _ = await command.ExecuteNonQueryAsync(token);
            await Task.Delay(800, token);
            return new byte[] { 1 };
        }, CancellationToken.None);
        Assert.Equal(WorkflowStatus.Running, (await database.Store.ReadAsync(key))!.Status);
        Assert.Equal(WorkflowNodeStatus.Running, Assert.Single(await database.Store.ReadNodesAsync(key)).Status);
        await using var verify = await database.Source.OpenConnectionAsync();
        await using var count = new BlueTuskCommand($"SELECT count(*) FROM \"{database.Options.Schema}\".effects", verify);
        Assert.Equal(0L, await count.ExecuteScalarAsync<long>(CancellationToken.None));
    }
}
