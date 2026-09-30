using System.Globalization;
using System.Text;

namespace BlueTusk.Events.Tests;

public sealed class EventsBoundedWorkloadTests
{
    [Fact]
    public async Task IndependentStreamBacklogsRecoverFencedReplayWithoutDuplicateEffectsOrOffsetGaps()
    {
        await using var db = await EventDatabase.CreateAsync(new PostgreSqlEventsOptions { MaximumEventBytes = 128, MaximumAppendBytes = 16384 });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        const int streams = 8;
        const int batches = 4;
        const int batchSize = 128;
        var identities = Enumerable.Range(0, batchSize * batches).Select(static _ => Guid.NewGuid()).ToArray();
        var occurrence = DateTimeOffset.UtcNow;
        await Task.WhenAll(Enumerable.Range(0, streams).Select(async partition =>
        {
            // Reuse IDs across tenants deliberately. Deduplication must remain tenant-scoped.
            var stream = new EventStreamKey("tenant-" + partition.ToString(CultureInfo.InvariantCulture), "orders");
            for (var batch = 0; batch < batches; batch++)
            {
                var writes = Enumerable.Range(batch * batchSize, batchSize).Select(index =>
                    new EventWrite(identities[index], "load.order", 1, occurrence,
                        Encoding.UTF8.GetBytes(index.ToString("D6", CultureInfo.InvariantCulture) + new string('x', 122)))).ToArray();
                await using (var connection = await db.DataSource.OpenConnectionAsync(token))
                await using (var transaction = await connection.BeginTransactionAsync(token))
                {
                    await db.Store.AppendAsync(connection, transaction, stream, writes, token);
                    await transaction.RollbackAsync(token);
                }
                var committed = await db.AppendAsync(stream, writes);
                Assert.All(committed, receipt => Assert.False(receipt.WasAlreadyStored));
                Assert.Equal((batch * batchSize) + 1L, committed[0].Sequence);
                Assert.All(await db.AppendAsync(stream, writes), receipt => Assert.True(receipt.WasAlreadyStored));
            }

            var owner = Assert.IsType<EventReplayLease>(await db.Store.AcquireReplayAsync("load", stream, "before-restart", TimeSpan.FromMinutes(2), token));
            var first = await db.Store.ReplayAsync(owner, db.HandleAsync, 47, 128 * 47, token);
            Assert.Equal(47, first.HandledCount);
            Assert.Equal(47, first.Checkpoint);
            await db.Store.ReleaseReplayAsync(owner, token);
            var recovered = Assert.IsType<EventReplayLease>(await db.Store.AcquireReplayAsync("load", stream, "after-restart", TimeSpan.FromMinutes(2), token));
            Assert.True(recovered.FencingToken > owner.FencingToken);
            await Assert.ThrowsAsync<EventReplayFencedException>(async () => await db.Store.ReplayAsync(owner, db.HandleAsync, maximumPayloadBytes: 16384, cancellationToken: token));
            EventReplayResult replay;
            do
            {
                replay = await db.Store.ReplayAsync(recovered, db.HandleAsync, 47, 128 * 47, token);
                Assert.InRange(replay.HandledCount, 0, 47);
            } while (!replay.ReachedEnd);
            Assert.Equal(batchSize * batches, replay.Checkpoint);
            Assert.Equal(0, (await db.Store.ReplayAsync(recovered, db.HandleAsync, maximumPayloadBytes: 16384, cancellationToken: token)).HandledCount);
            var retained = new List<StoredEvent>();
            while (retained.Count < batchSize * batches)
            {
                var page = await db.Store.ReadAsync(stream, retained.Count, batchSize, 128 * batchSize, token);
                Assert.NotEmpty(page);
                Assert.InRange(page.Sum(static value => value.Payload.Length), 1, 128 * batchSize);
                retained.AddRange(page);
            }
            Assert.Equal(Enumerable.Range(1, batchSize * batches).Select(static sequence => (long)sequence), retained.Select(static value => value.Sequence));
        }));
        Assert.Equal(streams * batches * batchSize, await db.EffectCountAsync());
        await using var verify = await db.DataSource.OpenConnectionAsync(token);
        await using var command = verify.CreateCommand();
        command.CommandText = $"SELECT sum(value),count(*) FROM \"{db.Schema}\".effects";
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        Assert.Equal(streams * ((long)batchSize * batches * ((batchSize * batches) + 1) / 2), reader.GetDecimal(0));
        Assert.Equal(streams * batches * batchSize, reader.GetInt64(1));
    }
}
