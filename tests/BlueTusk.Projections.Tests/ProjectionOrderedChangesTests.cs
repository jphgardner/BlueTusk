using BlueTusk.Streams;

namespace BlueTusk.Projections.Tests;

public sealed class ProjectionOrderedChangesTests
{
    [Fact]
    public async Task SameTransactionInsertThenDeleteAndRepeatedUpdateUseFinalSourceImageAndOneAggregateDelta()
    {
        await using var db = await ProjectionDatabase.CreateAsync();
        var (lease, definition) = await db.ReadyAsync();
        await db.Store.PromoteAsync(lease, new(100), null);
        await using var delivery = db.Delivery(101,
            id => new InsertChange(id, ProjectionDatabase.Order("2", "first", "customer", 20m)),
            id => new DeleteChange(id, ProjectionDatabase.Order("2", "first", "customer", 20m)),
            id => new UpdateChange(id, ProjectionDatabase.Order("1", "first", "customer", 10m), ProjectionDatabase.Order("1", "first", "customer", 30m), new ChangedColumnSet(true, [3])),
            id => new UpdateChange(id, ProjectionDatabase.Order("1", "first", "customer", 30m), ProjectionDatabase.Order("1", "first", "customer", 40m), new ChangedColumnSet(true, [3])));
        await db.Store.ApplyAsync(lease, definition, delivery.Transaction);
        Assert.Null(await db.ReadAsync(id: "2"));
        Assert.Equal(40m, (await db.ReadAsync())!.Amount);
        Assert.Equal(40m, await db.AggregateAsync());
    }
}
