using BlueTusk.Data;

namespace BlueTusk.Jobs.Tests;

public sealed class FencedJobTransactionTests
{
    [Fact]
    public async Task FencedTransactionCommitsEffectAndChildJobTogether()
    {
        await using var database = await JobDatabase.CreateAsync();
        await database.ExecuteAsync("CREATE TABLE {schema}.effects (id integer PRIMARY KEY)");
        _ = await database.Store.EnqueueAsync(database.Request());
        var lease = Assert.Single(await database.Store.ClaimAsync(database.Scope, "worker", 1, TimeSpan.FromMinutes(1)));
        var result = await database.Store.ExecuteFencedAsync(lease, async (connection, transaction, token) =>
        {
            await using var command = new BlueTuskCommand($"INSERT INTO \"{database.Options.Schema}\".effects VALUES (42)", connection) { Transaction = transaction };
            _ = await command.ExecuteNonQueryAsync(token);
            return await database.Store.EnqueueAsync(database.Request("child"), transaction, token);
        });
        Assert.True(result.Executed);
        Assert.NotNull(await database.Store.ReadAsync(database.Scope, result.Value));
        await using var verify = await database.DataSource.OpenConnectionAsync();
        await using var count = new BlueTuskCommand($"SELECT count(*) FROM \"{database.Options.Schema}\".effects", verify);
        Assert.Equal(1L, await count.ExecuteScalarAsync<long>(CancellationToken.None));
    }

    [Fact]
    public async Task ExpiryDuringCallbackRollsBackEffectAndChildEnqueue()
    {
        await using var database = await JobDatabase.CreateAsync();
        await database.ExecuteAsync("CREATE TABLE {schema}.effects (id integer PRIMARY KEY)");
        _ = await database.Store.EnqueueAsync(database.Request());
        var lease = Assert.Single(await database.Store.ClaimAsync(database.Scope, "worker", 1, TimeSpan.FromMilliseconds(400)));
        Guid child = Guid.Empty;
        var result = await database.Store.ExecuteFencedAsync(lease, async (connection, transaction, token) =>
        {
            await using var command = new BlueTuskCommand($"INSERT INTO \"{database.Options.Schema}\".effects VALUES (42)", connection) { Transaction = transaction };
            _ = await command.ExecuteNonQueryAsync(token);
            child = await database.Store.EnqueueAsync(database.Request("child"), transaction, token);
            await Task.Delay(600, token);
            return 42;
        });
        Assert.False(result.Executed);
        Assert.Null(await database.Store.ReadAsync(database.Scope, child));
        await using var verify = await database.DataSource.OpenConnectionAsync();
        await using var count = new BlueTuskCommand($"SELECT count(*) FROM \"{database.Options.Schema}\".effects", verify);
        Assert.Equal(0L, await count.ExecuteScalarAsync<long>(CancellationToken.None));
    }

    [Fact]
    public async Task RevokedLeaseNeverEntersFencedCallback()
    {
        await using var database = await JobDatabase.CreateAsync();
        Guid id = await database.Store.EnqueueAsync(database.Request());
        var lease = Assert.Single(await database.Store.ClaimAsync(database.Scope, "worker", 1, TimeSpan.FromMinutes(1)));
        Assert.True(await database.Store.CancelAsync(database.Scope, id));
        bool called = false;
        var result = await database.Store.ExecuteFencedAsync(lease, (_, _, _) =>
        {
            called = true;
            return ValueTask.FromResult(42);
        });
        Assert.False(result.Executed);
        Assert.False(called);
    }
}
