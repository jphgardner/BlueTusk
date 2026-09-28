namespace BlueTusk.Events.Tests;

public sealed class EventReplayStatusTests
{
    [Fact]
    public async Task MissingStreamAndConsumerRemainDistinctAndObservationNeverCreatesReplayOrReadsPayloadTables()
    {
        await using var db = await EventDatabase.CreateAsync();
        var first = new EventStreamKey("first", "orders");
        var missing = await db.Store.ReadReplayStatusAsync("consumer", first);
        Assert.False(missing.StreamExists); Assert.False(missing.ReplayExists);
        Assert.Equal(0, missing.StreamHead); Assert.Equal(0, missing.Checkpoint); Assert.Equal(0, missing.RemainingEvents);
        Assert.Null(missing.OwnerId); Assert.Null(missing.LeaseExpiresAt); Assert.False(missing.IsLeaseActive);
        await db.AppendAsync(first, Enumerable.Range(0, 16).Select(_ => new EventWrite(Guid.NewGuid(), "large.event", 1, DateTimeOffset.UtcNow, new byte[65_536])).ToArray());
        var other = new EventStreamKey("another", "orders");
        await db.AppendAsync(other, [new(Guid.NewGuid(), "private.event", 1, DateTimeOffset.UtcNow, "private"u8)]);
        var before = await db.Store.ReadReplayStatusAsync("consumer", first);
        Assert.True(before.StreamExists); Assert.False(before.ReplayExists);
        Assert.Equal(16, before.StreamHead); Assert.Equal(16, before.RemainingEvents);
        var lease = Assert.IsType<EventReplayLease>(await db.Store.AcquireReplayAsync("consumer", first, "worker", TimeSpan.FromMinutes(1)));
        Assert.Equal(5, (await db.Store.ReplayAsync(lease, db.HandleAsync, 5, 1_048_576)).Checkpoint);
        await using (var connection = await db.DataSource.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            // Removing both payload table names proves health does not touch outbox/inbox rows,
            // even when their retained payloads greatly exceed the metadata response size.
            command.CommandText = $"ALTER TABLE \"{db.Schema}\".outbox RENAME TO hidden_outbox; ALTER TABLE \"{db.Schema}\".inbox RENAME TO hidden_inbox";
            await command.ExecuteNonQueryAsync();
        }
        var status = await db.Store.ReadReplayStatusAsync("consumer", first);
        Assert.True(status.StreamExists); Assert.True(status.ReplayExists); Assert.True(status.IsLeaseActive);
        Assert.Equal("consumer", status.ConsumerId); Assert.Equal(first, status.Stream);
        Assert.Equal(16, status.StreamHead); Assert.Equal(5, status.Checkpoint); Assert.Equal(11, status.RemainingEvents);
        Assert.Equal(lease.OwnerId, status.OwnerId); Assert.Equal(lease.FencingToken, status.FencingToken);
        Assert.True(status.LeaseExpiresAt > status.ObservedAt);
        var isolated = await db.Store.ReadReplayStatusAsync("consumer", other);
        Assert.True(isolated.StreamExists); Assert.False(isolated.ReplayExists);
        Assert.Equal(1, isolated.StreamHead); Assert.Equal(0, isolated.Checkpoint); Assert.Equal(1, isolated.RemainingEvents);
    }

    [Fact]
    public async Task UncommittedBusinessAppendAndReplayEffectsNeverAppearInCommittedHealthSnapshot()
    {
        await using var db = await EventDatabase.CreateAsync();
        var stream = new EventStreamKey("tenant", "orders");
        var writes = Enumerable.Range(0, 3).Select(_ => new EventWrite(Guid.NewGuid(), "test.event", 1, DateTimeOffset.UtcNow, "data"u8)).ToArray();
        await using (var connection = await db.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await db.Store.AppendAsync(connection, transaction, stream, writes);
            Assert.False((await db.Store.ReadReplayStatusAsync("consumer", stream)).StreamExists);
            await transaction.RollbackAsync();
        }
        await db.AppendAsync(stream, writes);
        var lease = Assert.IsType<EventReplayLease>(await db.Store.AcquireReplayAsync("consumer", stream, "worker", TimeSpan.FromMinutes(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Store.ReplayAsync(lease,
            async (value, connection, transaction, token) =>
            {
                await db.HandleAsync(value, connection, transaction, token);
                var uncommitted = await db.Store.ReadReplayStatusAsync("consumer", stream, token);
                Assert.Equal(0, uncommitted.Checkpoint); Assert.Equal(3, uncommitted.RemainingEvents);
                throw new InvalidOperationException("Intentional rollback after metadata observation.");
            }));
        Assert.Equal(0, (await db.Store.ReadReplayStatusAsync("consumer", stream)).Checkpoint);
        Assert.Equal(0, await db.EffectCountAsync());
        await db.Store.ReplayAsync(lease, db.HandleAsync, 2);
        var committed = await db.Store.ReadReplayStatusAsync("consumer", stream);
        Assert.Equal(3, committed.StreamHead); Assert.Equal(2, committed.Checkpoint); Assert.Equal(1, committed.RemainingEvents);
        Assert.Equal(2, await db.EffectCountAsync());
    }

    [Fact]
    public async Task DatabaseClockExpiryReplacementAndReleaseRemainVisibleWithoutLosingFenceOrCheckpoint()
    {
        using var listener = new System.Diagnostics.Metrics.MeterListener
        { InstrumentPublished = static (instrument, l) => { if (instrument.Name == "bluetusk.events.replay.observed_remaining") { l.EnableMeasurementEvents(instrument); } } };
        listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => throw new InvalidOperationException("Faulty health observer."));
        listener.Start();
        await using var db = await EventDatabase.CreateAsync();
        var stream = new EventStreamKey("tenant", "orders");
        await db.AppendAsync(stream, [new(Guid.NewGuid(), "test.event", 1, DateTimeOffset.UtcNow, "data"u8)]);
        var first = Assert.IsType<EventReplayLease>(await db.Store.AcquireReplayAsync("consumer", stream, "first", TimeSpan.FromMinutes(1)));
        await db.Store.ReplayAsync(first, db.HandleAsync);
        await using (var connection = await db.DataSource.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        { command.CommandText = $"UPDATE \"{db.Schema}\".replay SET expires_at=clock_timestamp()-interval '1 second'"; await command.ExecuteNonQueryAsync(); }
        var expired = await db.Store.ReadReplayStatusAsync("consumer", stream);
        Assert.True(expired.ReplayExists); Assert.False(expired.IsLeaseActive);
        Assert.Equal("first", expired.OwnerId); Assert.True(expired.LeaseExpiresAt <= expired.ObservedAt);
        var second = Assert.IsType<EventReplayLease>(await db.Store.AcquireReplayAsync("consumer", stream, "second", TimeSpan.FromMinutes(1)));
        var replaced = await db.Store.ReadReplayStatusAsync("consumer", stream);
        Assert.True(replaced.IsLeaseActive); Assert.Equal("second", replaced.OwnerId);
        Assert.True(replaced.FencingToken > expired.FencingToken); Assert.Equal(1, replaced.Checkpoint);
        Assert.True(await db.Store.ReleaseReplayAsync(second));
        var released = await db.Store.ReadReplayStatusAsync("consumer", stream);
        Assert.True(released.ReplayExists); Assert.False(released.IsLeaseActive);
        Assert.Null(released.OwnerId); Assert.Null(released.LeaseExpiresAt);
        Assert.Equal(second.FencingToken, released.FencingToken); Assert.Equal(0, released.RemainingEvents);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await db.Store.ReadReplayStatusAsync("consumer", stream, canceled.Token));
        await Assert.ThrowsAsync<ArgumentException>(async () => await db.Store.ReadReplayStatusAsync(new string('x', 201), stream));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await db.Store.ReadReplayStatusAsync("consumer", null!));
    }

    [Fact]
    public async Task CorruptMetadataCannotReportHealthyLagOrFetchOversizedOwnerIdentity()
    {
        await using var db = await EventDatabase.CreateAsync();
        var stream = new EventStreamKey("tenant", "orders");
        await db.AppendAsync(stream, [new(Guid.NewGuid(), "test.event", 1, DateTimeOffset.UtcNow, "data"u8)]);
        await db.Store.AcquireReplayAsync("consumer", stream, "worker", TimeSpan.FromMinutes(1));
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE \"{db.Schema}\".replay SET checkpoint=2"; await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Store.ReadReplayStatusAsync("consumer", stream));
        command.CommandText = $"UPDATE \"{db.Schema}\".replay SET checkpoint=0,owner_id=repeat('x',1048576)"; await command.ExecuteNonQueryAsync();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Store.ReadReplayStatusAsync("consumer", stream));
        Assert.Contains("bounded identity contract", failure.Message, StringComparison.Ordinal);
    }
}
