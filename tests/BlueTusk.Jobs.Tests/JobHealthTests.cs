namespace BlueTusk.Jobs.Tests;

public sealed class JobHealthTests
{
    [Fact]
    public async Task BoundedInspectionReportsSaturationExpiryAndTenantIsolation()
    {
        await using var database = await JobDatabase.CreateAsync();
        for (int index = 0; index < 4; index++)
        {
            _ = await database.Store.EnqueueAsync(database.Request());
        }

        var pending = await database.Store.InspectAsync(database.Scope, maximumObserved: 2);
        Assert.Equal(2, pending.PendingObserved);
        Assert.True(pending.PendingCountCapped);
        Assert.NotNull(pending.OldestReadyAge);
        _ = await database.Store.ClaimAsync(database.Scope, "worker", 3, TimeSpan.FromMinutes(1));
        await database.ExecuteAsync("UPDATE {schema}.jobs SET lease_expires = clock_timestamp() - interval '1 second' WHERE status = 1");
        var expired = await database.Store.InspectAsync(database.Scope, maximumObserved: 2);
        Assert.Equal(1, expired.PendingObserved);
        Assert.False(expired.PendingCountCapped);
        Assert.Equal(2, expired.RunningObserved);
        Assert.True(expired.RunningCountCapped);
        Assert.Equal(2, expired.ExpiredLeasesObserved);
        Assert.True(expired.ExpiredLeaseCountCapped);
        var other = await database.Store.InspectAsync(new JobScope("other", database.Scope.Queue));
        Assert.Equal(0, other.PendingObserved);
        Assert.Equal(0, other.RunningObserved);
        Assert.Null(other.OldestReadyAge);
    }
}
