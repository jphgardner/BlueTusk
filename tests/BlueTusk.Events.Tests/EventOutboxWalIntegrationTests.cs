using System.Globalization;
using System.Data.Common;
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
            command.CommandText = $"CREATE PUBLICATION \"{publication}\" FOR TABLE \"{fixture.Schema}\".outbox, \"{fixture.Schema}\".published_retention_intents";
            await command.ExecuteNonQueryAsync(token);
        }
        await using (var transport = fixture.DataSource.CreateCommand($"""
            CREATE TABLE "{fixture.Schema}".stream_state (
                source_fingerprint text NOT NULL,consumer_group text NOT NULL,
                checkpoint_format integer NOT NULL,system_identifier text NOT NULL,
                database_name text NOT NULL,slot_name text NOT NULL,
                publication_fingerprint text NOT NULL,database_identity text NOT NULL,
                output_plugin text NOT NULL,mapping_fingerprint text NOT NULL,
                acknowledged_position numeric(20,0) NOT NULL,store_generation bigint NOT NULL,
                PRIMARY KEY(source_fingerprint,consumer_group))
            """))
        { await transport.ExecuteNonQueryAsync(token); }

        var slotCreated = false;
        try
        {
            ChangeSourceIdentity sourceIdentity;
            EventPublishedSourceIdentity publishedSource;
            await using (var identityConnection = await BlueTuskLogicalReplicationConnection.OpenAsync(fixture.DataSource.CreateDedicatedSessionOptions(), token))
            {
                var system = await identityConnection.IdentifySystemAsync(token);
                sourceIdentity = new ChangeSourceIdentity(system.SystemIdentifier, system.DatabaseName!, slot, publication);
                publishedSource = new EventPublishedSourceIdentity(system.SystemIdentifier, system.DatabaseName!,
                    system.Timeline, slot, publication);
            }

            var decoder = new EventOutboxChangeDecoder(fixture.Schema);
            var incarnation = Guid.NewGuid();
            var processor = PostgreSqlEventDeliveryProcessor.CreateProtected(fixture.DataSource, fixture.Store, decoder,
                "wal-consumer", sourceIdentity, null,
                new EventPublishedRetentionTargetOptions(incarnation, publishedSource));
            var source = new PostgreSqlConsistentSnapshotSource(fixture.DataSource, new PostgreSqlConsistentSnapshotOptions
            {
                Source = sourceIdentity,
                PublicationNames = [publication],
                Tables = [new PostgreSqlSnapshotTable(EventOutboxChangeDecoderTests.Table(fixture.Schema), [0, 1, 2])],
                MaximumBatchRows = 2
            }, connection => new FeedbackObserver(connection, fixture.DataSource, fixture.Schema,
                sourceIdentity, "wal-consumer"));
            await using (var attempt = await source.BeginAttemptAsync(null, token))
            {
                slotCreated = true;
                var stream = new EventStreamKey("tenant", "orders");
                var registration = await fixture.Store.RegisterPublishedRetentionConsumerAsync(stream,
                    "wal-consumer", incarnation, publishedSource, token);
                await fixture.Store.RegisterPublishedRetentionTargetAsync(registration, token);
                var writes = new[]
                {
                    new EventWrite(Guid.NewGuid(), "order.placed", 1, DateTimeOffset.UtcNow, new byte[] { 0, 127, 128, 255, 92 }),
                    new EventWrite(Guid.NewGuid(), "order.placed", 2, DateTimeOffset.UtcNow, "{}"u8)
                };
                await fixture.AppendAsync(stream, writes);
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

                var archive = new Archive();
                _ = await fixture.Store.ArchiveNextAsync(stream, archive, cancellationToken: token);
                var intent = await fixture.Store.PublishRetentionIntentAsync(stream, archive, publishedSource,
                    throughSequence: 2, cancellationToken: token);
                var markerSeen = false;
                ulong markerPosition = 0;
                for (var i = 0; i < 100 && !markerSeen; i++)
                {
                    Assert.True(await changes.MoveNextAsync());
                    await using var delivery = changes.Current;
                    var result = await processor.ProcessAsync(delivery, fixture.HandleAsync, token);
                    Assert.Equal(ChangeDeliveryState.Acknowledged, delivery.State);
                    Assert.Equal(0, result.OutboxEvents);
                    await using var check = await fixture.DataSource.OpenConnectionAsync(token);
                    await using var command = check.CreateCommand();
                    command.CommandText = $"SELECT count(*) FROM \"{fixture.Schema}\".published_retention_acknowledgements WHERE retention_epoch=@epoch AND target_incarnation=@incarnation";
                    var epoch = command.CreateParameter(); epoch.ParameterName = "epoch"; epoch.Value = intent.Epoch; command.Parameters.Add(epoch);
                    var target = command.CreateParameter(); target.ParameterName = "incarnation"; target.Value = incarnation; command.Parameters.Add(target);
                    markerSeen = Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1;
                    if (markerSeen) { markerPosition = delivery.Transaction.CommitEndPosition.Value; }
                }
                Assert.True(markerSeen);
                for (var i = 0; i < 100; i++)
                {
                    await using var position = fixture.DataSource.CreateCommand(
                        "SELECT pg_catalog.pg_wal_lsn_diff(confirmed_flush_lsn,'0/0'::pg_lsn) " +
                        "FROM pg_catalog.pg_replication_slots WHERE slot_name=@slot");
                    var slotParameter = position.CreateParameter();
                    slotParameter.ParameterName = "slot"; slotParameter.Value = slot;
                    position.Parameters.Add(slotParameter);
                    var flush = await position.ExecuteScalarAsync(token);
                    if (flush is decimal value && value >= markerPosition) { break; }
                    await Task.Delay(20, token);
                }
                var observation = await fixture.Store.ObservePublishedRetentionIntentAsync(intent.Epoch,
                    [new EventPublishedRetentionRemoteTarget("wal-consumer", incarnation, "events-target",
                        fixture.DataSource, fixture.Schema, fixture.Schema,
                        EventPublishedRetentionRemoteKind.Events)],
                    cancellationToken: token);
                Assert.Equal(intent.Epoch, observation.RetentionEpoch);
                Assert.Equal(markerPosition, observation.MarkerCommitEndPosition);
                await using (var observed = fixture.DataSource.CreateCommand($"""
                    SELECT authorization_status,target_count FROM "{fixture.Schema}".published_retention_observations
                    WHERE observation_id=@observation
                    """))
                {
                    var id = observed.CreateParameter(); id.ParameterName = "observation";
                    id.Value = observation.ObservationId; observed.Parameters.Add(id);
                    await using var reader = await observed.ExecuteReaderAsync(token);
                    Assert.True(await reader.ReadAsync(token));
                    Assert.Equal("observation_only", reader.GetString(0));
                    Assert.Equal(1, reader.GetInt32(1));
                }
                await using (var mutation = fixture.DataSource.CreateCommand($"""
                    UPDATE "{fixture.Schema}".published_retention_observations SET target_count=2
                    WHERE observation_id=@observation
                    """))
                {
                    var id = mutation.CreateParameter(); id.ParameterName = "observation";
                    id.Value = observation.ObservationId; mutation.Parameters.Add(id);
                    await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() =>
                        mutation.ExecuteNonQueryAsync(token));
                }
                await using (var eraseTransport = fixture.DataSource.CreateCommand($"""
                    DELETE FROM "{fixture.Schema}".stream_state WHERE consumer_group='wal-consumer'
                    """))
                { await eraseTransport.ExecuteNonQueryAsync(token); }
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await fixture.Store.ObservePublishedRetentionIntentAsync(intent.Epoch,
                        [new EventPublishedRetentionRemoteTarget("wal-consumer", incarnation,
                            "events-target", fixture.DataSource, fixture.Schema, fixture.Schema,
                            EventPublishedRetentionRemoteKind.Events)], cancellationToken: token));
                Assert.Equal(0, (await fixture.Store.ReadRetentionStatusAsync(stream, token)).RetainedThrough);
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await fixture.Store.PruneRetainedAsync(stream, cancellationToken: token));
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

    [Fact]
    public async Task ControlOnlyPublicationCannotAcknowledgeAnotherPublicationsOutboxHistory()
    {
        await using var fixture = await EventDatabase.CreateAsync();
        var protectedPublication = "events_pub_" + Guid.NewGuid().ToString("N");
        var controlOnlyPublication = "events_control_" + Guid.NewGuid().ToString("N");
        var slot = "events_slot_" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var administration = await fixture.DataSource.OpenConnectionAsync(token);
        await using (var command = administration.CreateCommand())
        {
            command.CommandText = $"""
                CREATE PUBLICATION "{protectedPublication}" FOR TABLE
                    "{fixture.Schema}".outbox, "{fixture.Schema}".published_retention_intents;
                CREATE PUBLICATION "{controlOnlyPublication}" FOR TABLE
                    "{fixture.Schema}".published_retention_intents
                """;
            await command.ExecuteNonQueryAsync(token);
        }

        var slotCreated = false;
        try
        {
            ChangeSourceIdentity sourceIdentity;
            EventPublishedSourceIdentity protectedSource;
            await using (var identityConnection = await BlueTuskLogicalReplicationConnection.OpenAsync(
                fixture.DataSource.CreateDedicatedSessionOptions(), token))
            {
                var system = await identityConnection.IdentifySystemAsync(token);
                sourceIdentity = new ChangeSourceIdentity(system.SystemIdentifier, system.DatabaseName!,
                    slot, protectedPublication);
                protectedSource = new EventPublishedSourceIdentity(system.SystemIdentifier,
                    system.DatabaseName!, system.Timeline, slot, protectedPublication);
            }
            var incarnation = Guid.NewGuid();
            var processor = PostgreSqlEventDeliveryProcessor.CreateProtected(fixture.DataSource, fixture.Store,
                new EventOutboxChangeDecoder(fixture.Schema), "wal-consumer", sourceIdentity, null,
                new EventPublishedRetentionTargetOptions(incarnation, protectedSource));
            var source = new PostgreSqlConsistentSnapshotSource(fixture.DataSource,
                new PostgreSqlConsistentSnapshotOptions
                {
                    Source = sourceIdentity,
                    PublicationNames = [controlOnlyPublication],
                    Tables = [new PostgreSqlSnapshotTable(EventOutboxChangeDecoderTests.Table(fixture.Schema), [0, 1, 2])]
                });
            await using (var attempt = await source.BeginAttemptAsync(null, token))
            {
                slotCreated = true;
                await foreach (var batch in attempt.ReadSnapshotAsync(token)) { Assert.Empty(batch.Rows); }
                var stream = new EventStreamKey("tenant", "orders");
                var registration = await fixture.Store.RegisterPublishedRetentionConsumerAsync(stream,
                    "wal-consumer", incarnation, protectedSource, token);
                await fixture.Store.RegisterPublishedRetentionTargetAsync(registration, token);
                await fixture.AppendAsync(stream,
                    [new EventWrite(Guid.NewGuid(), "order.placed", 1, DateTimeOffset.UtcNow, "{}"u8)]);
                var archive = new Archive();
                _ = await fixture.Store.ArchiveNextAsync(stream, archive, cancellationToken: token);
                var intent = await fixture.Store.PublishRetentionIntentAsync(stream, archive, protectedSource,
                    throughSequence: 1, cancellationToken: token);

                await using var changes = attempt.CreateChangeStream().ReadTransactionsAsync(token).GetAsyncEnumerator(token);
                Assert.True(await changes.MoveNextAsync());
                await using var delivery = changes.Current;
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await processor.ProcessAsync(delivery, fixture.HandleAsync, token));
                Assert.Equal(ChangeDeliveryState.Active, delivery.State);
                Assert.Equal(0, await fixture.EffectCountAsync());
                await using var check = fixture.DataSource.CreateCommand($"SELECT count(*) FROM \"{fixture.Schema}\".published_retention_acknowledgements WHERE retention_epoch=@epoch");
                var epoch = check.CreateParameter(); epoch.ParameterName = "epoch"; epoch.Value = intent.Epoch; check.Parameters.Add(epoch);
                Assert.Equal(0L, Convert.ToInt64(await check.ExecuteScalarAsync(token), CultureInfo.InvariantCulture));
            }
        }
        finally
        {
            if (slotCreated)
            {
                await using var cleanup = await BlueTuskLogicalReplicationConnection.OpenAsync(
                    fixture.DataSource.CreateDedicatedSessionOptions());
                await cleanup.DropReplicationSlotAsync(slot, wait: true);
            }
            await using var command = administration.CreateCommand();
            command.CommandText = $"DROP PUBLICATION IF EXISTS \"{controlOnlyPublication}\"; DROP PUBLICATION IF EXISTS \"{protectedPublication}\"";
            await command.ExecuteNonQueryAsync();
        }
    }

    private sealed class FeedbackObserver(BlueTuskLogicalReplicationConnection replication,
        DbDataSource dataSource, string schema, ChangeSourceIdentity source, string consumerGroup)
        : IChangeDeliveryObserver
    {
        public async ValueTask AcknowledgeAsync(ChangeTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO "{schema}".stream_state
                    (source_fingerprint,consumer_group,checkpoint_format,system_identifier,
                     database_name,slot_name,publication_fingerprint,database_identity,
                     output_plugin,mapping_fingerprint,acknowledged_position,store_generation)
                VALUES(@fingerprint,@consumer,1,@system,@database,@slot,@publication,
                       @identity,'pgoutput','test-mapping',@position,0)
                ON CONFLICT (source_fingerprint,consumer_group) DO UPDATE
                    SET acknowledged_position=EXCLUDED.acknowledged_position,
                        store_generation=stream_state.store_generation+1
                """;
            Add(command, "fingerprint", source.Fingerprint);
            Add(command, "consumer", consumerGroup);
            Add(command, "system", source.SystemIdentifier);
            Add(command, "database", source.DatabaseName);
            Add(command, "slot", source.SlotName);
            Add(command, "publication", source.PublicationFingerprint);
            Add(command, "identity", source.SystemIdentifier + ":" + source.DatabaseName);
            Add(command, "position", (decimal)transaction.CommitEndPosition.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await replication.SendStandbyStatusUpdateAsync(new BlueTuskStandbyStatus(
                transaction.CommitEndPosition, transaction.CommitEndPosition,
                transaction.CommitEndPosition), cancellationToken);
        }

        private static void Add(DbCommand command, string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        public ValueTask NackAsync(ChangeTransaction transaction, Exception? failure,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class Archive : IEventArchiveStore
    {
        private readonly Dictionary<string, IReadOnlyList<StoredEvent>> _objects = new(StringComparer.Ordinal);

        public ValueTask<string> WriteAsync(EventStreamKey stream, IReadOnlyList<StoredEvent> events,
            CancellationToken cancellationToken = default)
        {
            var id = Guid.NewGuid().ToString("N");
            _objects.Add(id, events.ToArray());
            return ValueTask.FromResult(id);
        }

        public ValueTask<IReadOnlyList<StoredEvent>> ReadAsync(string archiveId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(_objects[archiveId]);
    }
}
