using BlueTusk.Jobs;

namespace BlueTusk.Workflows.Tests;

public sealed class WorkflowRecoveryTests
{
    [Fact]
    public async Task ExpiredActivityCannotPersistResultBeforeOrAfterReclaim()
    {
        await using var database = await WorkflowDatabase.CreateAsync();
        var key = await database.StartAsync([new() { Id = "a", Kind = WorkflowNodeKind.Activity, Activity = "a" }]);
        var dispatch = new WorkflowDispatch(key.Id, "a", Compensation: false);
        var first = Assert.Single(await database.Store.Jobs.ClaimAsync(database.Scope, "first", 1, TimeSpan.FromMinutes(1)));
        var initial = await database.Store.PrepareAsync(first, dispatch, CancellationToken.None);
        Assert.NotNull(initial);
        await database.ExecuteAsync("UPDATE {jobs}.jobs SET lease_expires = clock_timestamp() - interval '1 second' WHERE status = 1");
        await database.Store.FinishAsync(first, dispatch, new byte[] { 1 }, CancellationToken.None);
        Assert.Equal(WorkflowNodeStatus.Running, Assert.Single(await database.Store.ReadNodesAsync(key)).Status);
        var second = Assert.Single(await database.Store.Jobs.ClaimAsync(database.Scope, "second", 1, TimeSpan.FromMinutes(1)));
        var replacement = await database.Store.PrepareAsync(second, dispatch, CancellationToken.None);
        Assert.NotNull(replacement);
        Assert.Equal(initial.IdempotencyKey, replacement.IdempotencyKey);
        await database.Store.FinishAsync(first, dispatch, new byte[] { 1 }, CancellationToken.None);
        await database.Store.FinishAsync(second, dispatch, new byte[] { 2 }, CancellationToken.None);
        Assert.Equal(2, Assert.Single(await database.Store.ReadNodesAsync(key)).Result.Span[0]);
        Assert.Equal(WorkflowStatus.Succeeded, (await database.Store.ReadAsync(key))!.Status);
        Assert.Single(await database.Store.ReadHistoryAsync(key, 0, 128), entry => entry.Event == "activity_completed");
    }

    [Fact]
    public async Task CrashedFinalAttemptIsReconciledToTerminalFailure()
    {
        await using var database = await WorkflowDatabase.CreateAsync();
        var key = await database.StartAsync([new() { Id = "a", Kind = WorkflowNodeKind.Activity, Activity = "a", MaximumAttempts = 1 }]);
        var lease = Assert.Single(await database.Store.Jobs.ClaimAsync(database.Scope, "crashed", 1, TimeSpan.FromMinutes(1)));
        Assert.NotNull(await database.Store.PrepareAsync(lease, new WorkflowDispatch(key.Id, "a", false), CancellationToken.None));
        await database.ExecuteAsync("UPDATE {jobs}.jobs SET lease_expires = clock_timestamp() - interval '1 second' WHERE status = 1");
        Assert.Empty(await database.Store.Jobs.ClaimAsync(database.Scope, "recovery", 1, TimeSpan.FromMinutes(1)));
        var reconciled = await database.Store.ReconcileAsync(database.Scope, 128);
        Assert.Equal(1, reconciled.Recovered);
        Assert.Equal(WorkflowStatus.Failed, (await database.Store.ReadAsync(key))!.Status);
        Assert.Equal("lease_expired", (await database.Store.ReadAsync(key))!.FailureCode);
        Assert.Equal(0, (await database.Store.ReconcileAsync(database.Scope, 128)).Recovered);
    }

    [Fact]
    public async Task RecoveryCursorCanReachTerminalDispatchBehindLiveCandidates()
    {
        await using var database = await WorkflowDatabase.CreateAsync();
        await database.Store.RegisterDefinitionAsync(database.Scope, new WorkflowDefinition
        {
            Name = "test", Version = 1, Nodes = [new() { Id = "a", Kind = WorkflowNodeKind.Activity, Activity = "a" }],
        });
        var keys = new List<WorkflowKey>();
        for (int index = 0; index < 8; index++)
        {
            keys.Add(await database.Store.StartAsync(database.Request()));
        }

        var ordered = new List<WorkflowKey>();
        WorkflowRecoveryCursor? cursor = null;
        do
        {
            var page = await database.Store.ReconcileAsync(database.Scope, 1, cursor);
            if (page.NextCursor is not null)
            {
                ordered.Add(keys.Single(key => key.Id == page.NextCursor.WorkflowId));
            }

            cursor = page.NextCursor;
        }
        while (cursor is not null);
        Assert.Equal(8, ordered.Count);
        var last = ordered[^1];
        var node = Assert.Single(await database.Store.ReadNodesAsync(last));
        Assert.True(await database.Store.Jobs.CancelAsync(database.Scope, node.JobId!.Value));
        cursor = null;
        int recovered = 0;
        do
        {
            var page = await database.Store.ReconcileAsync(database.Scope, 1, cursor);
            recovered += page.Recovered;
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        Assert.Equal(1, recovered);
        Assert.Equal(WorkflowStatus.Failed, (await database.Store.ReadAsync(last))!.Status);
    }
}
