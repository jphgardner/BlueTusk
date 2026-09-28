using BlueTusk.Events.Streams;
using BlueTusk.Replication;
using BlueTusk.Streams;

namespace BlueTusk.Events.Tests;

public sealed class EventOutboxWalIntegrationTests
{
    [Fact]
    public async Task ActualPgOutputOutboxTransactionIsDecodedAndHandledThroughAtomicInbox()
    {
        await using var fixture = await EventDatabase.CreateAsync();
        var publication = "events_pub_" + Guid.NewGuid().ToString("N");
        var slot = "events_slot_" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var administration = await fixture.DataSource.OpenConnectionAsync(token);
        await using (var command = administration.CreateCommand())
        {
            command.CommandText = $"CREATE PUBLICATION \"{publication}\" FOR TABLE \"{fixture.Schema}\".outbox";
            await command.ExecuteNonQueryAsync(token);
        }

        var slotCreated = false;
        try
        {
            ChangeSourceIdentity sourceIdentity;
            await using (var identityConnection = await BlueTuskLogicalReplicationConnection.OpenAsync(fixture.DataSource.CreateDedicatedSessionOptions(), token))
            {
                var system = await identityConnection.IdentifySystemAsync(token);
                sourceIdentity = new ChangeSourceIdentity(system.SystemIdentifier, system.DatabaseName!, slot, publication);
            }

            var decoder = new EventOutboxChangeDecoder(fixture.Schema);
            var processor = new PostgreSqlEventDeliveryProcessor(fixture.DataSource, fixture.Store, decoder, "wal-consumer", sourceIdentity);
            var source = new PostgreSqlConsistentSnapshotSource(fixture.DataSource, new PostgreSqlConsistentSnapshotOptions
            {
                Source = sourceIdentity,
                PublicationNames = [publication],
                Tables = [new PostgreSqlSnapshotTable(EventOutboxChangeDecoderTests.Table(fixture.Schema), [0, 1, 2])],
                MaximumBatchRows = 2
            });
            await using (var attempt = await source.BeginAttemptAsync(null, token))
            {
                slotCreated = true;
                var writes = new[]
                {
                    new EventWrite(Guid.NewGuid(), "order.placed", 1, DateTimeOffset.UtcNow, new byte[] { 0, 127, 128, 255, 92 }),
                    new EventWrite(Guid.NewGuid(), "order.placed", 2, DateTimeOffset.UtcNow, "{}"u8)
                };
                await fixture.AppendAsync(new EventStreamKey("tenant", "orders"), writes);
                await foreach (var batch in attempt.ReadSnapshotAsync(token))
                {
                    Assert.Empty(batch.Rows);
                }

                var decoded = new List<StoredEvent>();
                await using var changes = attempt.CreateChangeStream().ReadTransactionsAsync(token).GetAsyncEnumerator(token);
                for (var i = 0; i < 100 && decoded.Count == 0; i++)
                {
                    Assert.True(await changes.MoveNextAsync());
                    await using var delivery = changes.Current;
                    var result = await processor.ProcessAsync(delivery,
                        async (value, connection, transaction, cancellationToken) =>
                        {
                            decoded.Add(value);
                            await fixture.HandleAsync(value, connection, transaction, cancellationToken);
                        }, token);
                    Assert.Equal(ChangeDeliveryState.Acknowledged, delivery.State);
                    Assert.Equal(result.OutboxEvents, result.HandledEvents);
                }

                Assert.Equal(2, decoded.Count);
                Assert.Equal(writes[0].EventId, decoded[0].EventId);
                Assert.Equal(writes[1].EventId, decoded[1].EventId);
                Assert.Equal(writes[0].Payload.ToArray(), decoded[0].Payload.ToArray());
                Assert.Equal(writes[1].Payload.ToArray(), decoded[1].Payload.ToArray());
                Assert.Equal(1, decoded[0].Sequence);
                Assert.Equal(2, decoded[1].Sequence);
                Assert.Equal(2, await fixture.EffectCountAsync());
            }
        }
        finally
        {
            if (slotCreated)
            {
                await using var cleanup = await BlueTuskLogicalReplicationConnection.OpenAsync(fixture.DataSource.CreateDedicatedSessionOptions());
                await cleanup.DropReplicationSlotAsync(slot, wait: true);
            }

            await using var command = administration.CreateCommand();
            command.CommandText = $"DROP PUBLICATION IF EXISTS \"{publication}\"";
            await command.ExecuteNonQueryAsync();
        }
    }
}
