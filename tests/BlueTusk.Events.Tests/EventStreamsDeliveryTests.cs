using System.Globalization;
using BlueTusk.Events.Streams;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;

namespace BlueTusk.Events.Tests;

public sealed class EventStreamsDeliveryTests
{
    [Fact]
    public async Task TransactionalHandlerFailureRollsBackAllInboxEffectsAndLeavesDeliveryActive()
    {
        await using var fixture = await EventDatabase.CreateAsync();
        var source = new ChangeSourceIdentity("system", "database", "slot", "publication");
        var processor = new PostgreSqlEventDeliveryProcessor(fixture.DataSource, fixture.Store,
            new EventOutboxChangeDecoder(fixture.Schema), "consumer", source);
        await using var delivery = Delivery(source, fixture.Schema, 1, 2);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await processor.ProcessAsync(delivery,
            async (value, connection, transaction, cancellationToken) =>
            {
                await fixture.HandleAsync(value, connection, transaction, cancellationToken);
                if (value.Sequence == 2)
                {
                    throw new InvalidOperationException("Intentional business handler failure.");
                }
            }));
        Assert.Equal(ChangeDeliveryState.Active, delivery.State);
        Assert.Equal(0, await fixture.EffectCountAsync());
        var result = await processor.ProcessAsync(delivery, fixture.HandleAsync);
        Assert.Equal(new EventStreamsDeliveryResult(2, 2), result);
        Assert.Equal(ChangeDeliveryState.Acknowledged, delivery.State);
        Assert.Equal(2, await fixture.EffectCountAsync());
    }

    [Fact]
    public async Task AcknowledgmentFailureAfterTargetCommitRedeliversWithoutRepeatingBusinessEffects()
    {
        await using var fixture = await EventDatabase.CreateAsync();
        var source = new ChangeSourceIdentity("system", "database", "slot", "publication");
        var observer = new FailingAcknowledgmentObserver();
        var processor = new PostgreSqlEventDeliveryProcessor(fixture.DataSource, fixture.Store,
            new EventOutboxChangeDecoder(fixture.Schema), "consumer", source);
        await using var delivery = Delivery(source, fixture.Schema, 1, 2, observer);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await processor.ProcessAsync(delivery, fixture.HandleAsync));
        Assert.Equal(ChangeDeliveryState.Active, delivery.State);
        Assert.Equal(2, await fixture.EffectCountAsync());
        Assert.Equal(new EventStreamsDeliveryResult(2, 0), await processor.ProcessAsync(delivery, fixture.HandleAsync));
        Assert.Equal(ChangeDeliveryState.Acknowledged, delivery.State);
        Assert.Equal(2, await fixture.EffectCountAsync());
    }

    [Fact]
    public async Task TargetCommitFailureLeavesSourceUnacknowledgedAndEffectsRolledBack()
    {
        await using var fixture = await EventDatabase.CreateAsync();
        // Deferred constraints deliberately permit the handler INSERT and reject only COMMIT.
        await using (var setup = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = setup.CreateCommand())
        {
            command.CommandText = $"CREATE TABLE \"{fixture.Schema}\".commit_guard(id bigint PRIMARY KEY); ALTER TABLE \"{fixture.Schema}\".effects ADD CONSTRAINT commit_failure FOREIGN KEY(value) REFERENCES \"{fixture.Schema}\".commit_guard(id) DEFERRABLE INITIALLY DEFERRED";
            await command.ExecuteNonQueryAsync();
        }

        var source = new ChangeSourceIdentity("system", "database", "slot", "publication");
        var processor = new PostgreSqlEventDeliveryProcessor(fixture.DataSource, fixture.Store,
            new EventOutboxChangeDecoder(fixture.Schema), "consumer", source);
        await using var delivery = Delivery(source, fixture.Schema, 1, 2);
        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(async () => await processor.ProcessAsync(delivery, fixture.HandleAsync));
        Assert.Equal(ChangeDeliveryState.Active, delivery.State);
        Assert.Equal(0, await fixture.EffectCountAsync());
        await using (var setup = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = setup.CreateCommand())
        {
            command.CommandText = $"INSERT INTO \"{fixture.Schema}\".commit_guard VALUES(1),(2)";
            await command.ExecuteNonQueryAsync();
        }

        Assert.Equal(new EventStreamsDeliveryResult(2, 2), await processor.ProcessAsync(delivery, fixture.HandleAsync));
        Assert.Equal(2, await fixture.EffectCountAsync());
    }

    [Fact]
    public async Task EventTransactionBoundFailsBeforeAnyTargetEffectsOrAcknowledgment()
    {
        await using var fixture = await EventDatabase.CreateAsync();
        var source = new ChangeSourceIdentity("system", "database", "slot", "publication");
        var processor = new PostgreSqlEventDeliveryProcessor(fixture.DataSource, fixture.Store,
            new EventOutboxChangeDecoder(fixture.Schema), "consumer", source,
            new EventStreamsDeliveryOptions { MaximumEvents = 1 });
        await using var delivery = Delivery(source, fixture.Schema, 1, 2);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await processor.ProcessAsync(delivery, fixture.HandleAsync));
        Assert.Equal(ChangeDeliveryState.Active, delivery.State);
        Assert.Equal(0, await fixture.EffectCountAsync());
    }

    private static ChangeTransactionDelivery Delivery(ChangeSourceIdentity source, string schema, long first, long second,
        IChangeDeliveryObserver? observer = null)
    {
        var position = new BlueTuskLogSequenceNumber(100);
        return ChangeDeliveryTestFactory.CreateCommitted(source, 42, position, new[] { first, second }.Select((sequence, ordinal) =>
            new InsertChange(new ChangeId(source, position, 42, ordinal), EventOutboxChangeDecoderTests.Row(schema,
                "tenant", "orders", sequence.ToString(CultureInfo.InvariantCulture), Guid.NewGuid().ToString("D"),
                "event", "1", "2026-09-27 12:00:00+00", "\\x7b7d"))), observer);
    }

    private sealed class FailingAcknowledgmentObserver : IChangeDeliveryObserver
    {
        private bool _first = true;

        public ValueTask AcknowledgeAsync(ChangeTransaction transaction, CancellationToken cancellationToken = default)
        {
            if (_first)
            {
                _first = false;
                throw new InvalidOperationException("Intentional failure after target commit.");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask NackAsync(ChangeTransaction transaction, Exception? failure, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
