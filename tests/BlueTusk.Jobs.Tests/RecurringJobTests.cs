namespace BlueTusk.Jobs.Tests;

public sealed class RecurringJobTests
{
    [Fact]
    public async Task ConcurrentDispatchersEnqueueOneOccurrenceAndAdvanceScheduleAtomically()
    {
        await using var database = await JobDatabase.CreateAsync();
        var schedule = new RecurringJobSchedule
        {
            Name = "nightly",
            Job = database.Request(),
            Interval = TimeSpan.FromDays(1),
            // Keep the next boundary well in the future even when host/database clocks have small offsets.
            FirstOccurrence = DateTimeOffset.UtcNow.AddDays(-10).AddHours(-6),
        };
        Assert.True(await database.Store.CreateScheduleAsync(schedule));
        Assert.False(await database.Store.CreateScheduleAsync(schedule));
        int[] counts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => database.Store.DispatchSchedulesAsync(database.Scope, 1).AsTask()));
        Assert.Equal(1, counts.Sum());
        Assert.Equal(0, await database.Store.DispatchSchedulesAsync(database.Scope, 1));
        Assert.Single(await database.Store.ClaimAsync(database.Scope, "worker", 10, TimeSpan.FromMinutes(1)));
        Assert.True(await database.Store.DeleteScheduleAsync(database.Scope, schedule.Name));
        Assert.False(await database.Store.DeleteScheduleAsync(database.Scope, schedule.Name));
    }

    [Fact]
    public async Task SkipMisfireAdvancesPastMissedIntervalsWithoutBacklog()
    {
        await using var database = await JobDatabase.CreateAsync();
        Assert.True(await database.Store.CreateScheduleAsync(new RecurringJobSchedule
        {
            Name = "skip",
            Job = database.Request(),
            Interval = TimeSpan.FromHours(1),
            FirstOccurrence = DateTimeOffset.UtcNow.AddHours(-10).AddMinutes(-30),
            MisfirePolicy = JobMisfirePolicy.Skip,
        }));
        Assert.Equal(0, await database.Store.DispatchSchedulesAsync(database.Scope, 1));
        Assert.Equal(0, await database.Store.DispatchSchedulesAsync(database.Scope, 1));
        Assert.Empty(await database.Store.ClaimAsync(database.Scope, "worker", 10, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task DispatchHonorsBatchAndTenantBoundaries()
    {
        await using var database = await JobDatabase.CreateAsync();
        var other = new JobScope("other-tenant", database.Scope.Queue);
        for (int index = 0; index < 5; index++)
        {
            Assert.True(await database.Store.CreateScheduleAsync(new RecurringJobSchedule
            {
                Name = "schedule-" + index,
                Job = database.Request(),
                Interval = TimeSpan.FromDays(1),
                FirstOccurrence = DateTimeOffset.UtcNow.AddMinutes(-1),
            }));
        }

        Assert.Equal(0, await database.Store.DispatchSchedulesAsync(other, 2));
        Assert.Equal(2, await database.Store.DispatchSchedulesAsync(database.Scope, 2));
        Assert.Equal(2, await database.Store.DispatchSchedulesAsync(database.Scope, 2));
        Assert.Equal(1, await database.Store.DispatchSchedulesAsync(database.Scope, 2));
        Assert.Equal(0, await database.Store.DispatchSchedulesAsync(database.Scope, 2));
    }
}
