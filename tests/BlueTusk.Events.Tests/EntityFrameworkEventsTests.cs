using BlueTusk.Events.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BlueTusk.Events.Tests;

public sealed class EntityFrameworkEventsTests
{
    [Fact]
    public async Task ExplicitEfTransactionRollsBackAndCommitsBusinessChangesWithOutbox()
    {
        await using var fixture = await EventDatabase.CreateAsync();
        var options = new DbContextOptionsBuilder<DbContext>().UseBlueTusk(fixture.DataSource).Options;
        await using var context = new DbContext(options);
        var stream = new EventStreamKey("tenant", "orders");
        var value = new EventWrite(Guid.NewGuid(), "order.placed", 1, DateTimeOffset.UtcNow, "{}"u8);
        // The fixture generates this identifier from a fixed prefix and Guid.ToString("N").
        var insertEffectSql = $"INSERT INTO \"{fixture.Schema}\".effects VALUES (1)";
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await context.AppendEventsAsync(fixture.Store, stream, [value]));
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            await context.Database.ExecuteSqlRawAsync(insertEffectSql);
            await context.AppendEventsAsync(fixture.Store, stream, [value]);
            await transaction.RollbackAsync();
        }

        Assert.Equal(0, await fixture.EffectCountAsync());
        Assert.Empty(await fixture.Store.ReadAsync(stream));
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            await context.Database.ExecuteSqlRawAsync(insertEffectSql);
            var receipt = Assert.Single(await context.AppendEventsAsync(fixture.Store, stream, [value]));
            Assert.Equal(1, receipt.Sequence);
            await transaction.CommitAsync();
        }

        Assert.Equal(1, await fixture.EffectCountAsync());
        Assert.Single(await fixture.Store.ReadAsync(stream));
    }
}
