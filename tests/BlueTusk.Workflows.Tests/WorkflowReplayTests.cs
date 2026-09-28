namespace BlueTusk.Workflows.Tests;

public sealed class WorkflowReplayTests
{
    [Fact]
    public async Task HistoryReplayMatchesQueuedRunningAndCompletedStateAndDetectsDrift()
    {
        await using var database = await WorkflowDatabase.CreateAsync();
        var key = await database.StartAsync([new() { Id = "a", Kind = WorkflowNodeKind.Activity, Activity = "a" }]);
        var scheduled = await database.Store.ReplayAsync(key);
        Assert.NotNull(scheduled);
        Assert.True(scheduled.MatchesPersistedState);
        Assert.Equal(WorkflowNodeStatus.Scheduled, scheduled.NodeStates["a"]);
        var lease = Assert.Single(await database.Store.Jobs.ClaimAsync(database.Scope, "replay", 1, TimeSpan.FromMinutes(1)));
        var dispatch = new WorkflowDispatch(key.Id, "a", false);
        Assert.NotNull(await database.Store.PrepareAsync(lease, dispatch, CancellationToken.None));
        var running = await database.Store.ReplayAsync(key);
        Assert.True(running!.MatchesPersistedState);
        Assert.Equal(WorkflowNodeStatus.Running, running.NodeStates["a"]);
        await database.Store.FinishAsync(lease, dispatch, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        var completed = await database.Store.ReplayAsync(key);
        Assert.True(completed!.MatchesPersistedState);
        Assert.Equal(WorkflowStatus.Succeeded, completed.Status);
        Assert.Equal(WorkflowNodeStatus.Completed, completed.NodeStates["a"]);
        await database.ExecuteAsync("UPDATE {schema}.nodes SET status = 0");
        Assert.False((await database.Store.ReplayAsync(key))!.MatchesPersistedState);
    }
}
