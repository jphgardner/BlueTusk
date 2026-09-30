namespace BlueTusk.Jobs.Tests;

public sealed class JobDurableFormatTests
{
    private static readonly string[] ExpectedOutcomes = ["succeeded", "failed"];
    [Fact]
    public async Task SameFormatReopenAndOperationalTuningRollbackPreserveLeaseHistoryAndDeduplication()
    {
        await using var database = await JobDatabase.CreateAsync();
        var request = database.Request("retained-format-one");
        Guid id = await database.Store.EnqueueAsync(request);
        var first = Assert.Single(await database.Store.ClaimAsync(database.Scope, "original", 1, TimeSpan.FromMinutes(1)));
        Assert.True(await database.Store.FailAsync(first, "retry", TimeSpan.Zero));
        var before = await database.Store.ReadAsync(database.Scope, id);

        var reopened = new PostgreSqlJobStore(database.DataSource, database.Options with
        {
            MaximumClaimBatch = 1,
            MaximumClaimPayloadBytes = database.Options.MaximumPayloadBytes,
            CommandTimeoutSeconds = 40,
        });
        await reopened.InitializeAsync();
        Assert.Equal(before, await reopened.ReadAsync(database.Scope, id));
        Assert.Equal(id, await reopened.EnqueueAsync(request));
        var second = Assert.Single(await reopened.ClaimAsync(database.Scope, "replacement", 1, TimeSpan.FromMinutes(1)));
        Assert.Equal(2, second.Attempt);
        Assert.True(second.FencingToken > first.FencingToken);

        await database.Store.InitializeAsync();
        Assert.False(await database.Store.CompleteAsync(first));
        Assert.True(await database.Store.CompleteAsync(second));
        Assert.Equal(id, await database.Store.EnqueueAsync(request));
        var history = await database.Store.ReadHistoryAsync(database.Scope, id);
        Assert.Equal(ExpectedOutcomes, history.Select(entry => entry.Outcome));
        Assert.Equal("retry", history[1].FailureCode);
        Assert.Equal(JobStatus.Succeeded, (await database.Store.ReadAsync(database.Scope, id))!.Status);
    }

    [Fact]
    public async Task UnsupportedFormatAndDurableLimitMismatchRejectInitializationWithoutRewritingExistingState()
    {
        await using var database = await JobDatabase.CreateAsync();
        Guid id = await database.Store.EnqueueAsync(database.Request("preserve"));
        var before = await database.Store.ReadAsync(database.Scope, id);
        var mismatch = new PostgreSqlJobStore(database.DataSource, database.Options with { MaximumHistoryEntries = 16 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => mismatch.InitializeAsync().AsTask());
        Assert.Equal(before, await database.Store.ReadAsync(database.Scope, id));

        // Simulate an unsupported header only. This is fail-closed evidence, not a format-two migration.
        await database.ExecuteAsync("UPDATE {schema}.settings SET format_version = 2");
        var reopened = new PostgreSqlJobStore(database.DataSource, database.Options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.InitializeAsync().AsTask());
        Assert.Equal(before, await database.Store.ReadAsync(database.Scope, id));
        await database.ExecuteAsync("UPDATE {schema}.settings SET format_version = 1");
        await reopened.InitializeAsync();
        Assert.Equal(id, await reopened.EnqueueAsync(database.Request("preserve")));
        var lease = Assert.Single(await reopened.ClaimAsync(database.Scope, "after-header-restoration", 1, TimeSpan.FromMinutes(1)));
        Assert.True(await reopened.CompleteAsync(lease));
    }
}
