using System.Globalization;
using System.Text;
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

    [Fact]
    public async Task OrderedRetentionAckCommitsWithTargetCheckpointAndRejectsConflictingRedelivery()
    {
        await using var fixture = await EventDatabase.CreateAsync();
        var source = new ChangeSourceIdentity("system", "database", "slot", "publication");
        var expected = new EventPublishedSourceIdentity("system", "database", 1, "slot", "publication");
        var incarnation = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        var registration = new EventPublishedConsumerRegistration(new EventStreamKey("tenant", "orders"),
            "consumer", incarnation, 1, expected, 123, 456);
        await fixture.Store.RegisterPublishedRetentionTargetAsync(registration);
        await using var delivery = MarkerDelivery(source, fixture.Schema, epoch, through: 2);

        var unprotected = new PostgreSqlEventDeliveryProcessor(fixture.DataSource, fixture.Store,
            new EventOutboxChangeDecoder(fixture.Schema), "consumer", source);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await unprotected.ProcessAsync(delivery, fixture.HandleAsync));
        Assert.Equal(ChangeDeliveryState.Active, delivery.State);

        var protectedProcessor = PostgreSqlEventDeliveryProcessor.CreateProtected(fixture.DataSource, fixture.Store,
            new EventOutboxChangeDecoder(fixture.Schema), "consumer", source, null,
            new EventPublishedRetentionTargetOptions(incarnation, expected));
        await using (var unbound = new ChangeTransactionDelivery(delivery.Transaction,
            new FailingAcknowledgmentObserver()))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await protectedProcessor.ProcessAsync(unbound, fixture.HandleAsync));
            Assert.Equal(ChangeDeliveryState.Active, unbound.State);
            Assert.Equal((0L, 0L), await RetentionCountsAsync(fixture));
        }
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                CREATE FUNCTION "{fixture.Schema}".reject_ack() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'reject test ack'; END $$;
                CREATE TRIGGER reject_test_ack BEFORE INSERT ON "{fixture.Schema}".published_retention_acknowledgements
                    FOR EACH ROW EXECUTE FUNCTION "{fixture.Schema}".reject_ack()
                """;
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(async () =>
            await protectedProcessor.ProcessAsync(delivery, fixture.HandleAsync));
        Assert.Equal(ChangeDeliveryState.Active, delivery.State);
        Assert.Equal((0L, 0L), await RetentionCountsAsync(fixture));
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"DROP TRIGGER reject_test_ack ON \"{fixture.Schema}\".published_retention_acknowledgements";
            await command.ExecuteNonQueryAsync();
        }

        Assert.Equal(new EventStreamsDeliveryResult(0, 0),
            await protectedProcessor.ProcessAsync(delivery, fixture.HandleAsync));
        Assert.Equal(ChangeDeliveryState.Acknowledged, delivery.State);
        Assert.Equal((1L, 1L), await RetentionCountsAsync(fixture));
        await using (var later = ChangeDeliveryTestFactory.CreateCommitted(source, 43,
            new BlueTuskLogSequenceNumber(300), Array.Empty<Change>()))
        {
            _ = await protectedProcessor.ProcessAsync(later, fixture.HandleAsync);
        }
        await using var retry = MarkerDelivery(source, fixture.Schema, epoch, through: 2);
        _ = await protectedProcessor.ProcessAsync(retry, fixture.HandleAsync);
        Assert.Equal((1L, 1L), await RetentionCountsAsync(fixture));
        await using var conflict = MarkerDelivery(source, fixture.Schema, epoch, through: 3);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await protectedProcessor.ProcessAsync(conflict, fixture.HandleAsync));
        Assert.Equal(ChangeDeliveryState.Active, conflict.State);
        Assert.Equal((1L, 1L), await RetentionCountsAsync(fixture));
    }

    [Fact]
    public async Task RetentionMarkerWithUnrelatedRawChangeCannotAdvanceTargetCheckpointOrAck()
    {
        await using var fixture = await EventDatabase.CreateAsync();
        var source = new ChangeSourceIdentity("system", "database", "slot", "publication");
        var expected = new EventPublishedSourceIdentity("system", "database", 1, "slot", "publication");
        var incarnation = Guid.NewGuid();
        await fixture.Store.RegisterPublishedRetentionTargetAsync(new EventPublishedConsumerRegistration(
            new EventStreamKey("tenant", "orders"), "consumer", incarnation, 1, expected, 123, 456));
        var processor = PostgreSqlEventDeliveryProcessor.CreateProtected(fixture.DataSource, fixture.Store,
            new EventOutboxChangeDecoder(fixture.Schema), "consumer", source, null,
            new EventPublishedRetentionTargetOptions(incarnation, expected));
        await using var delivery = MarkerDelivery(source, fixture.Schema, Guid.NewGuid(), through: 2,
            unrelatedChange: true);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await processor.ProcessAsync(delivery, fixture.HandleAsync));
        Assert.Equal(ChangeDeliveryState.Active, delivery.State);
        Assert.Equal((0L, 0L), await RetentionCountsAsync(fixture));
    }

    private static ChangeTransactionDelivery MarkerDelivery(ChangeSourceIdentity source, string schema, Guid epoch,
        long through, bool unrelatedChange = false)
    {
        var position = new BlueTuskLogSequenceNumber(200);
        var names = new[]
        {
            "retention_epoch", "tenant_id", "stream_id", "first_sequence", "through_sequence",
            "archive_manifest_sha256", "source_system_identifier", "source_database", "source_database_oid",
            "source_timeline", "source_slot", "source_publication", "source_publication_oid",
            "membership_revision"
        };
        var values = new[]
        {
            epoch.ToString("D"), "tenant", "orders", "1", through.ToString(CultureInfo.InvariantCulture),
            "\\x" + new string('a', 64), "system", "database", "123", "1", "slot", "publication", "456", "1"
        };
        var table = new ChangeTable(2, schema, "published_retention_intents", 'd',
            names.Select((name, ordinal) => new ChangeColumn(ordinal, name, 25, -1, ordinal == 0)));
        var row = new ChangeRow(table, values.Select(static value =>
            ChangeColumnValue.FromValue(Encoding.UTF8.GetBytes(value), ChangeValueEncoding.Text)));
        var changes = new List<Change> { new InsertChange(new ChangeId(source, position, 42, 0), row) };
        if (unrelatedChange)
        {
            var unrelated = new ChangeRow(new ChangeTable(3, schema, "other_published_table", 'd',
                [new ChangeColumn(0, "id", 25, -1, true)]),
                [ChangeColumnValue.FromValue(Encoding.UTF8.GetBytes("unrelated"), ChangeValueEncoding.Text)]);
            changes.Add(new InsertChange(new ChangeId(source, position, 42, 1), unrelated));
        }
        return ChangeDeliveryTestFactory.CreateCommitted(source, 42, position, changes);
    }

    private static async Task<(long Checkpoints, long Acknowledgements)> RetentionCountsAsync(EventDatabase fixture)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT (SELECT count(*) FROM \"{fixture.Schema}\".published_retention_target_checkpoints), (SELECT count(*) FROM \"{fixture.Schema}\".published_retention_acknowledgements)";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1));
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
