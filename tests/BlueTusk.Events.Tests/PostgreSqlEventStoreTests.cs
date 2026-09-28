using System.Data.Common;
using System.Globalization;
using System.Text;
using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.Events.Tests;

public sealed class PostgreSqlEventStoreTests
{
    [Fact]
    public async Task BusinessWriteAndOutboxRollBackTogetherAndDoNotConsumeSequence()
    {
        await using var fixture = await EventDatabase.CreateAsync();
        var stream = new EventStreamKey("tenant", "orders");
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await fixture.EffectAsync(connection, transaction, 1);
            var receipt = Assert.Single(await fixture.Store.AppendAsync(connection, transaction, stream, [Write(1)]));
            Assert.Equal(1, receipt.Sequence);
            Assert.Empty(await fixture.Store.ReadAsync(stream));
            await transaction.RollbackAsync();
        }

        Assert.Empty(await fixture.Store.ReadAsync(stream));
        Assert.Equal(0, await fixture.EffectCountAsync());
        var committed = Assert.Single(await fixture.AppendAsync(stream, [Write(2)]));
        Assert.Equal(1, committed.Sequence);
    }

    [Fact]
    public async Task StableIdentityRetriesDeduplicateAndContentConflictsAreRejected()
    {
        await using var fixture = await EventDatabase.CreateAsync();
        var stream = new EventStreamKey("tenant", "orders");
        var first = Write(1);
        var second = Write(2);
        await fixture.AppendAsync(stream, [first]);
        var receipts = await fixture.AppendAsync(stream, [first, second]);
        Assert.True(receipts[0].WasAlreadyStored);
        Assert.False(receipts[1].WasAlreadyStored);
        Assert.Equal(1, receipts[0].Sequence);
        Assert.Equal(2, receipts[1].Sequence);
        Assert.Equal(2, (await fixture.Store.ReadAsync(stream)).Count);
        var changed = new EventWrite(first.EventId, first.EventType, first.Version, first.OccurredAt, "different"u8);
        await Assert.ThrowsAsync<EventIdentityConflictException>(async () => await fixture.AppendAsync(stream, [changed]));
        await Assert.ThrowsAsync<EventIdentityConflictException>(async () =>
            await fixture.AppendAsync(new EventStreamKey("tenant", "other"), [first]));
        Assert.Single(await fixture.AppendAsync(new EventStreamKey("other-tenant", "orders"), [first]));
    }

    [Fact]
    public async Task SameStreamConcurrentAppendsSerializeWhileIndependentStreamsContinue()
    {
        await using var fixture = await EventDatabase.CreateAsync();
        var stream = new EventStreamKey("tenant", "orders");
        await using var firstConnection = await fixture.DataSource.OpenConnectionAsync();
        await using var firstTransaction = await firstConnection.BeginTransactionAsync();
        var first = Assert.Single(await fixture.Store.AppendAsync(firstConnection, firstTransaction, stream, [Write(1)]));
        Assert.Equal(1, first.Sequence);
        await using var secondConnection = await fixture.DataSource.OpenConnectionAsync();
        await using var secondTransaction = await secondConnection.BeginTransactionAsync();
        var secondTask = fixture.Store.AppendAsync(secondConnection, secondTransaction, stream, [Write(2)]).AsTask();
        await AssertBlockedAsync(fixture, secondTask);
        var independent = await fixture.AppendAsync(new EventStreamKey("tenant", "independent"), [Write(3)]);
        Assert.Single(independent);
        Assert.Empty(await fixture.Store.ReadAsync(stream));
        await firstTransaction.CommitAsync();
        var second = Assert.Single(await secondTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, second.Sequence);
        Assert.Single(await fixture.Store.ReadAsync(stream));
        await secondTransaction.CommitAsync();
        Assert.Equal([1L, 2L], (await fixture.Store.ReadAsync(stream)).Select(static value => value.Sequence));
    }

    [Fact]
    public async Task ManyConcurrentWritersProduceContiguousStreamOrderingAndTenantIsolation()
    {
        await using var fixture = await EventDatabase.CreateAsync();
        var stream = new EventStreamKey("tenant", "orders");
        var tasks = Enumerable.Range(0, 16).Select(i => fixture.AppendAsync(stream, [Write(i)]).AsTask()).ToArray();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        var stored = await fixture.Store.ReadAsync(stream);
        Assert.Equal(Enumerable.Range(1, 16).Select(static value => (long)value), stored.Select(static value => value.Sequence));
        Assert.Equal(16, stored.Select(static value => value.EventId).Distinct().Count());
        Assert.Empty(await fixture.Store.ReadAsync(new EventStreamKey("other", "orders")));
        Assert.Empty(await fixture.Store.ReadAsync(new EventStreamKey("tenant", "other")));
    }

    [Fact]
    public async Task ConcurrentRetryOfSameIdentityPublishesOneEvent()
    {
        await using var fixture = await EventDatabase.CreateAsync();
        var stream = new EventStreamKey("tenant", "orders");
        var value = Write(1);
        var tasks = Enumerable.Range(0, 8).Select(_ => fixture.AppendAsync(stream, [value]).AsTask()).ToArray();
        var receipts = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(receipts, static batch => !batch[0].WasAlreadyStored);
        Assert.All(receipts, static batch => Assert.Equal(1, batch[0].Sequence));
        Assert.Single(await fixture.Store.ReadAsync(stream));
    }

    [Fact]
    public async Task InboxRollbackAndConcurrentDuplicateDeliveryAreAtomicWithBusinessEffects()
    {
        await using var fixture = await EventDatabase.CreateAsync();
        var stream = new EventStreamKey("tenant", "orders");
        await fixture.AppendAsync(stream, [Write(1)]);
        var value = Assert.Single(await fixture.Store.ReadAsync(stream));
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            Assert.True(await fixture.Store.ProcessInboxAsync(connection, transaction, "consumer", value, fixture.HandleAsync));
            await transaction.RollbackAsync();
        }

        Assert.Equal(0, await fixture.EffectCountAsync());
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var result = await fixture.Store.ProcessInboxAsync(connection, transaction, "consumer", value, fixture.HandleAsync);
            await transaction.CommitAsync();
            return result;
        })).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(results, static handled => handled);
        Assert.Equal(1, await fixture.EffectCountAsync());
    }

    [Fact]
    public async Task ReplayPersistsCheckpointAcrossOwnersAndDoesNotRepeatInboxEffects()
    {
        await using var fixture = await EventDatabase.CreateAsync();
        var stream = new EventStreamKey("tenant", "orders");
        await fixture.AppendAsync(stream, Enumerable.Range(0, 5).Select(Write).ToArray());
        var lease = Assert.IsType<EventReplayLease>(await fixture.Store.AcquireReplayAsync("consumer", stream, "worker", TimeSpan.FromMinutes(1)));
        Assert.Null(await fixture.Store.AcquireReplayAsync("consumer", stream, "other", TimeSpan.FromMinutes(1)));
        Assert.True(await fixture.Store.RenewReplayAsync(lease, TimeSpan.FromMinutes(1)));
        var first = await fixture.Store.ReplayAsync(lease, fixture.HandleAsync, maximumEvents: 2);
        Assert.Equal(2, first.HandledCount);
        Assert.Equal(2, first.Checkpoint);
        Assert.False(first.ReachedEnd);
        Assert.Equal(2, await fixture.Store.ReadCheckpointAsync("consumer", stream));
        Assert.True(await fixture.Store.ReleaseReplayAsync(lease));
        var secondStore = new PostgreSqlEventStore(fixture.DataSource, new PostgreSqlEventsOptions { Schema = fixture.Schema });
        var replacement = Assert.IsType<EventReplayLease>(await secondStore.AcquireReplayAsync("consumer", stream, "new-worker", TimeSpan.FromMinutes(1)));
        Assert.True(replacement.FencingToken > lease.FencingToken);
        Assert.False(await fixture.Store.RenewReplayAsync(lease, TimeSpan.FromMinutes(1)));
        Assert.False(await fixture.Store.ReleaseReplayAsync(lease));
        await Assert.ThrowsAsync<EventReplayFencedException>(async () => await fixture.Store.ReplayAsync(lease, fixture.HandleAsync));
        var second = await secondStore.ReplayAsync(replacement, fixture.HandleAsync);
        Assert.Equal(3, second.HandledCount);
        Assert.Equal(5, second.Checkpoint);
        Assert.True(second.ReachedEnd);
        Assert.Equal(5, await fixture.EffectCountAsync());
        Assert.Equal(0, (await secondStore.ReplayAsync(replacement, fixture.HandleAsync)).HandledCount);
        Assert.Equal(5, await fixture.EffectCountAsync());
    }

    [Fact]
    public async Task FailedReplayRollsBackAllEffectsAndCheckpointThenCanRetry()
    {
        await using var fixture = await EventDatabase.CreateAsync();
        var stream = new EventStreamKey("tenant", "orders");
        await fixture.AppendAsync(stream, [Write(1), Write(2)]);
        var lease = Assert.IsType<EventReplayLease>(await fixture.Store.AcquireReplayAsync("consumer", stream, "worker", TimeSpan.FromMinutes(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Store.ReplayAsync(lease,
            async (value, connection, transaction, cancellationToken) =>
            {
                await fixture.HandleAsync(value, connection, transaction, cancellationToken);
                if (value.Sequence == 2)
                {
                    throw new InvalidOperationException("Intentional handler failure.");
                }
            }));
        Assert.Equal(0, await fixture.EffectCountAsync());
        Assert.Equal(0, await fixture.Store.ReadCheckpointAsync("consumer", stream));
        Assert.Equal(2, (await fixture.Store.ReplayAsync(lease, fixture.HandleAsync)).HandledCount);
        Assert.Equal(2, await fixture.EffectCountAsync());
    }

    [Fact]
    public async Task ExpiryDuringHandlingFencesCheckpointAndRollsBackEffects()
    {
        await using var fixture = await EventDatabase.CreateAsync();
        var stream = new EventStreamKey("tenant", "orders");
        await fixture.AppendAsync(stream, [Write(1)]);
        var lease = Assert.IsType<EventReplayLease>(await fixture.Store.AcquireReplayAsync("consumer", stream, "worker", TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAsync<EventReplayFencedException>(async () => await fixture.Store.ReplayAsync(lease,
            async (value, connection, transaction, cancellationToken) =>
            {
                await fixture.HandleAsync(value, connection, transaction, cancellationToken);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"UPDATE \"{fixture.Schema}\".replay SET expires_at = clock_timestamp() - interval '1 second'";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }));
        Assert.Equal(0, await fixture.EffectCountAsync());
        Assert.Equal(0, await fixture.Store.ReadCheckpointAsync("consumer", stream));
    }

    [Fact]
    public async Task BoundedPayloadReadAndBatchLimitsAreEnforcedWithoutOffsetLoss()
    {
        await using var fixture = await EventDatabase.CreateAsync(new PostgreSqlEventsOptions
        {
            MaximumEventBytes = 10,
            MaximumAppendBytes = 25,
            MaximumAppendEvents = 4
        });
        var stream = new EventStreamKey("tenant", "orders");
        var writes = Enumerable.Range(0, 4).Select(_ => new EventWrite(Guid.NewGuid(), "event", 1, DateTimeOffset.UtcNow, "1234567890"u8)).ToArray();
        await Assert.ThrowsAsync<ArgumentException>(async () => await fixture.AppendAsync(stream, writes));
        Assert.Empty(await fixture.Store.ReadAsync(stream, 0, 4, 25));
        await fixture.AppendAsync(stream, writes[..2]);
        await fixture.AppendAsync(stream, writes[2..]);
        var first = await fixture.Store.ReadAsync(stream, 0, 4, 25);
        Assert.Equal(2, first.Count);
        Assert.Equal(20, first.Sum(static value => value.Payload.Length));
        Assert.Equal([3L, 4L], (await fixture.Store.ReadAsync(stream, 2, 4, 25)).Select(static value => value.Sequence));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await fixture.Store.ReadAsync(stream, 0, 4, 9));
    }

    [Fact]
    public async Task CancellationRollsBackReplayEffectsAndLeavesLeaseUsable()
    {
        await using var fixture = await EventDatabase.CreateAsync();
        var stream = new EventStreamKey("tenant", "orders");
        await fixture.AppendAsync(stream, [Write(1)]);
        var lease = Assert.IsType<EventReplayLease>(await fixture.Store.AcquireReplayAsync("consumer", stream, "worker", TimeSpan.FromMinutes(1)));
        using var source = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await fixture.Store.ReplayAsync(lease,
            async (value, connection, transaction, cancellationToken) =>
            {
                await fixture.HandleAsync(value, connection, transaction, cancellationToken);
                await source.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }, cancellationToken: source.Token));
        Assert.Equal(0, await fixture.EffectCountAsync());
        Assert.Equal(0, await fixture.Store.ReadCheckpointAsync("consumer", stream));
        Assert.Equal(1, (await fixture.Store.ReplayAsync(lease, fixture.HandleAsync)).HandledCount);
    }

    [Fact]
    public async Task IndependentTenantReplayCheckpointsAndInboxesCannotSuppressEachOther()
    {
        await using var fixture = await EventDatabase.CreateAsync();
        var first = new EventStreamKey("first", "orders");
        var second = new EventStreamKey("second", "orders");
        var value = Write(1);
        await fixture.AppendAsync(first, [value]);
        await fixture.AppendAsync(second, [value]);
        var firstLease = Assert.IsType<EventReplayLease>(await fixture.Store.AcquireReplayAsync("consumer", first, "worker", TimeSpan.FromMinutes(1)));
        var secondLease = Assert.IsType<EventReplayLease>(await fixture.Store.AcquireReplayAsync("consumer", second, "worker", TimeSpan.FromMinutes(1)));
        Assert.Equal(1, (await fixture.Store.ReplayAsync(firstLease, fixture.HandleAsync)).HandledCount);
        Assert.Equal(0, await fixture.Store.ReadCheckpointAsync("consumer", second));
        Assert.Equal(1, (await fixture.Store.ReplayAsync(secondLease, fixture.HandleAsync)).HandledCount);
        Assert.Equal(2, await fixture.EffectCountAsync());
    }

    [Fact]
    public async Task InboxDetectsReusedIdentityWithChangedPayload()
    {
        await using var fixture = await EventDatabase.CreateAsync();
        var stream = new EventStreamKey("tenant", "orders");
        await fixture.AppendAsync(stream, [Write(1)]);
        var value = Assert.Single(await fixture.Store.ReadAsync(stream));
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            Assert.True(await fixture.Store.ProcessInboxAsync(connection, transaction, "consumer", value, fixture.HandleAsync));
            await transaction.CommitAsync();
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var changed = value with { Payload = "changed"u8.ToArray() };
            await Assert.ThrowsAsync<EventIdentityConflictException>(async () =>
                await fixture.Store.ProcessInboxAsync(connection, transaction, "consumer", changed, fixture.HandleAsync));
            await transaction.RollbackAsync();
        }

        Assert.Equal(1, await fixture.EffectCountAsync());
    }

    [Fact]
    public async Task TypedVersionRouterDispatchesHistoricalContractsAndUnknownVersionRollsBack()
    {
        await using var fixture = await EventDatabase.CreateAsync();
        var stream = new EventStreamKey("tenant", "orders");
        var older = new EventContract<OrderPlaced>("orders.placed", 1, EventTestJson.Default.OrderPlaced);
        var newer = new EventContract<OrderPlaced>("orders.placed", 2, EventTestJson.Default.OrderPlaced);
        var builder = new EventRouterBuilder().Register(older,
            (body, _, connection, transaction, cancellationToken) => fixture.EffectAsync(connection, transaction, body.OrderId, cancellationToken));
        var oldRouter = builder.Build();
        builder.Register(newer,
            (body, _, connection, transaction, cancellationToken) => fixture.EffectAsync(connection, transaction, body.OrderId * 2, cancellationToken));
        Assert.Throws<InvalidOperationException>(() => builder.Register(newer,
            (_, envelope, connection, transaction, cancellationToken) => fixture.HandleAsync(envelope, connection, transaction, cancellationToken)));
        var newRouter = builder.Build();
        await fixture.AppendAsync(stream, [older.Create(Guid.NewGuid(), new OrderPlaced(1, 1m), DateTimeOffset.UtcNow),
            newer.Create(Guid.NewGuid(), new OrderPlaced(2, 2m), DateTimeOffset.UtcNow)]);
        var lease = Assert.IsType<EventReplayLease>(await fixture.Store.AcquireReplayAsync("consumer", stream, "worker", TimeSpan.FromMinutes(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Store.ReplayAsync(lease, oldRouter.HandleAsync));
        Assert.Equal(0, await fixture.EffectCountAsync());
        Assert.Equal(0, await fixture.Store.ReadCheckpointAsync("consumer", stream));
        Assert.Equal(2, (await fixture.Store.ReplayAsync(lease, newRouter.HandleAsync)).HandledCount);
        Assert.Equal(2, await fixture.EffectCountAsync());
    }

    private static EventWrite Write(int value) => new(Guid.NewGuid(), "test.event", 1,
        DateTimeOffset.UtcNow, Encoding.UTF8.GetBytes(value.ToString(CultureInfo.InvariantCulture)));

    private static async Task AssertBlockedAsync(EventDatabase fixture, Task operation)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            Assert.False(operation.IsCompleted, "The second stream append must wait for the first application transaction.");
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) > 0)
            {
                return;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Fail("The blocked PostgreSQL append was not observed within ten seconds.");
    }
}

internal sealed class EventDatabase : IAsyncDisposable
{
    private EventDatabase(BlueTuskDataSource dataSource, PostgreSqlEventsOptions options)
    {
        DataSource = dataSource;
        Store = new PostgreSqlEventStore(dataSource, options);
        Schema = options.Schema;
    }

    public BlueTuskDataSource DataSource { get; }
    public PostgreSqlEventStore Store { get; }
    public string Schema { get; }

    public static async ValueTask<EventDatabase> CreateAsync(PostgreSqlEventsOptions? configured = null)
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var options = new PostgreSqlEventsOptions
        {
            Schema = "events_test_" + Guid.NewGuid().ToString("N"),
            MaximumAppendEvents = configured?.MaximumAppendEvents ?? 1024,
            MaximumEventBytes = configured?.MaximumEventBytes ?? 1_048_576,
            MaximumAppendBytes = configured?.MaximumAppendBytes ?? 8_388_608
        };
        var fixture = new EventDatabase(BlueTuskDataSource.Create(connectionString), options);
        try
        {
            await fixture.Store.InitializeAsync();
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE \"{fixture.Schema}\".effects(value bigint NOT NULL)";
            await command.ExecuteNonQueryAsync();
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    public async ValueTask<IReadOnlyList<EventAppendReceipt>> AppendAsync(EventStreamKey stream, IReadOnlyList<EventWrite> values)
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var receipts = await Store.AppendAsync(connection, transaction, stream, values);
        await transaction.CommitAsync();
        return receipts;
    }

    public ValueTask HandleAsync(StoredEvent value, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken) =>
        EffectAsync(connection, transaction, value.Sequence, cancellationToken);

    public async ValueTask EffectAsync(DbConnection connection, DbTransaction transaction, long value, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO \"{Schema}\".effects VALUES (@value)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "value";
        parameter.Value = value;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<long> EffectCountAsync()
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM \"{Schema}\".effects";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var connection = await DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE";
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            await DataSource.DisposeAsync();
        }
    }
}
