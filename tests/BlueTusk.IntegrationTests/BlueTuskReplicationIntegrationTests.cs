using System.Buffers.Binary;
using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Replication;
using BlueTusk.Replication.PgOutput;
using BlueTusk.Streams;
using BlueTusk.TypeSystem;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskReplicationIntegrationTests
{
    [Fact]
    public async Task Exported_snapshot_and_matching_stream_have_no_concurrent_write_gap()
    {
        var connectionString = GetConnectionString();
        var suffix = Guid.NewGuid().ToString("N");
        var tableName = $"bluetusk_snapshot_{suffix}";
        var publicationName = $"bluetusk_snapshot_publication_{suffix}";
        var slotName = $"bluetusk_snapshot_slot_{suffix}";
        var quotedTable = BlueTuskSql.QuoteIdentifier(tableName);
        var quotedPublication = BlueTuskSql.QuoteIdentifier(publicationName);
        await using var administration = new BlueTuskConnection(connectionString);
        await administration.OpenAsync(CancellationToken.None);
        await ExecuteAsync(
            administration,
            $"CREATE TABLE {quotedTable} (id integer PRIMARY KEY, value text NOT NULL)");
        await ExecuteAsync(
            administration,
            $"INSERT INTO {quotedTable} SELECT value, 'initial-' || value FROM generate_series(1, 5) value");
        await ExecuteAsync(
            administration,
            $"CREATE PUBLICATION {quotedPublication} FOR TABLE {quotedTable}");

        try
        {
            await using var dataSource = BlueTuskDataSource.Create(connectionString);
            BlueTuskReplicationSystemIdentity identity;
            await using (var identityConnection =
                await BlueTuskLogicalReplicationConnection.OpenAsync(connectionString))
            {
                identity = await identityConnection.IdentifySystemAsync();
            }

            var table = new ChangeTable(
                relationId: 0,
                schema: "public",
                name: tableName,
                replicaIdentity: 'd',
                [
                    new ChangeColumn(0, "id", 23, -1, true),
                    new ChangeColumn(1, "value", 25, -1, false),
                ]);
            var source = new PostgreSqlConsistentSnapshotSource(
                dataSource,
                new PostgreSqlConsistentSnapshotOptions
                {
                    Source = new ChangeSourceIdentity(
                        identity.SystemIdentifier,
                        identity.DatabaseName!,
                        slotName,
                        publicationName),
                    PublicationNames = [publicationName],
                    Tables = [new PostgreSqlSnapshotTable(table, [0])],
                    CopyPageRows = 2,
                    MaximumBatchRows = 1,
                });

            await using (var attempt = await source.BeginAttemptAsync(abandonedEpoch: null))
            {
                await ExecuteAsync(
                    administration,
                    $"INSERT INTO {quotedTable} VALUES (6, 'during-snapshot')");

                var snapshotIds = new List<int>();
                await foreach (var batch in attempt.ReadSnapshotAsync())
                {
                    snapshotIds.AddRange(batch.Rows.Select(row =>
                        BinaryPrimitives.ReadInt32BigEndian(row.Row["id"].Data.Span)));
                }

                Assert.Equal([1, 2, 3, 4, 5], snapshotIds);

                await using var changes = attempt
                    .CreateChangeStream()
                    .ReadTransactionsAsync()
                    .GetAsyncEnumerator();
                Assert.True(await changes.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
                var delivery = changes.Current;
                var transactionChanges = await delivery.Transaction.Changes.MaterializeAsync();
                var insert = Assert.IsType<InsertChange>(Assert.Single(transactionChanges));
                Assert.Equal(
                    "6",
                    System.Text.Encoding.UTF8.GetString(insert.NewRow["id"].Data.Span));
                await delivery.AcknowledgeAsync();
            }

            await using var cleanup =
                await BlueTuskLogicalReplicationConnection.OpenAsync(connectionString);
            await cleanup.DropReplicationSlotAsync(slotName, wait: true);
        }
        finally
        {
            await ExecuteAsync(administration, $"DROP PUBLICATION IF EXISTS {quotedPublication}");
            await ExecuteAsync(administration, $"DROP TABLE IF EXISTS {quotedTable}");
        }
    }

    [Fact]
    public async Task New_process_can_explicitly_restart_an_inactive_snapshot_slot_with_a_new_epoch()
    {
        var connectionString = GetConnectionString();
        var suffix = Guid.NewGuid().ToString("N");
        var tableName = $"bluetusk_snapshot_restart_{suffix}";
        var publicationName = $"bluetusk_snapshot_restart_publication_{suffix}";
        var slotName = $"bluetusk_snapshot_restart_slot_{suffix}";
        var quotedTable = BlueTuskSql.QuoteIdentifier(tableName);
        var quotedPublication = BlueTuskSql.QuoteIdentifier(publicationName);
        await using var administration = new BlueTuskConnection(connectionString);
        await administration.OpenAsync(CancellationToken.None);
        await ExecuteAsync(
            administration,
            $"CREATE TABLE {quotedTable} (id integer PRIMARY KEY, value text NOT NULL)");
        await ExecuteAsync(administration, $"INSERT INTO {quotedTable} VALUES (1, 'initial')");
        await ExecuteAsync(
            administration,
            $"CREATE PUBLICATION {quotedPublication} FOR TABLE {quotedTable}");

        try
        {
            await using var dataSource = BlueTuskDataSource.Create(connectionString);
            BlueTuskReplicationSystemIdentity system;
            await using (var identityConnection =
                await BlueTuskLogicalReplicationConnection.OpenAsync(connectionString))
            {
                system = await identityConnection.IdentifySystemAsync();
            }

            var table = new ChangeTable(
                relationId: 0,
                schema: "public",
                name: tableName,
                replicaIdentity: 'd',
                [
                    new ChangeColumn(0, "id", 23, -1, true),
                    new ChangeColumn(1, "value", 25, -1, false),
                ]);
            var identity = new ChangeSourceIdentity(
                system.SystemIdentifier,
                system.DatabaseName!,
                slotName,
                publicationName);
            var firstSource = new PostgreSqlConsistentSnapshotSource(
                dataSource,
                new PostgreSqlConsistentSnapshotOptions
                {
                    Source = identity,
                    PublicationNames = [publicationName],
                    Tables = [new PostgreSqlSnapshotTable(table, [0])],
                });
            var firstAttempt = await firstSource.BeginAttemptAsync(abandonedEpoch: null);
            var firstEpoch = firstAttempt.Epoch;
            await foreach (var _ in firstAttempt.ReadSnapshotAsync())
            {
            }

            _ = firstAttempt.CreateChangeStream();
            await firstAttempt.DisposeAsync();

            var restartedSource = new PostgreSqlConsistentSnapshotSource(
                dataSource,
                new PostgreSqlConsistentSnapshotOptions
                {
                    Source = identity,
                    PublicationNames = [publicationName],
                    Tables = [new PostgreSqlSnapshotTable(table, [0])],
                    ExistingSlotMode = PostgreSqlExistingSnapshotSlotMode.RestartSnapshot,
                });
            await using var restartedAttempt =
                await restartedSource.BeginAttemptAsync(abandonedEpoch: null);

            Assert.NotEqual(firstEpoch.Value, restartedAttempt.Epoch.Value);
            Assert.True(restartedAttempt.Epoch.ConsistentPosition >= firstEpoch.ConsistentPosition);
        }
        finally
        {
            await using var cleanup =
                await BlueTuskLogicalReplicationConnection.OpenAsync(connectionString);
            var slots = await cleanup.GetReplicationSlotsAsync();
            if (slots.Any(slot => string.Equals(slot.SlotName, slotName, StringComparison.Ordinal)))
            {
                await cleanup.DropReplicationSlotAsync(slotName, wait: true);
            }

            await ExecuteAsync(administration, $"DROP PUBLICATION IF EXISTS {quotedPublication}");
            await ExecuteAsync(administration, $"DROP TABLE IF EXISTS {quotedTable}");
        }
    }

    [Fact]
    public async Task Data_source_derived_replication_session_is_dedicated_and_unpooled()
    {
        var settings = new BlueTuskConnectionStringBuilder(GetConnectionString())
        {
            Pooling = true,
        };
        await using var dataSource = BlueTuskDataSource.Create(settings.ConnectionString);

        await using var replication = await BlueTuskLogicalReplicationConnection.OpenAsync(
            dataSource.CreateDedicatedSessionOptions());
        var identity = await replication.IdentifySystemAsync();

        Assert.False(string.IsNullOrWhiteSpace(identity.SystemIdentifier));
        Assert.Equal(0, dataSource.GetPoolStatistics().Total);
    }

    [Fact]
    public async Task Physical_connection_discovers_slots_streams_wal_and_sends_feedback()
    {
        var connectionString = GetConnectionString();
        var slotName = $"bluetusk_physical_{Guid.NewGuid():N}";
        await using var replication =
            await BlueTuskPhysicalReplicationConnection.OpenAsync(connectionString);

        var identity = await replication.IdentifySystemAsync();
        Assert.False(string.IsNullOrWhiteSpace(identity.SystemIdentifier));
        Assert.True(identity.Timeline > 0);
        Assert.False(string.IsNullOrWhiteSpace(await replication.ShowAsync("server_version")));

        var slot = await replication.CreateReplicationSlotAsync(
            slotName,
            temporary: true,
            reserveWal: true);
        Assert.Equal(slotName, slot.SlotName);
        Assert.Null(slot.OutputPlugin);

        var initialSlotState = await replication.ReadReplicationSlotAsync(slotName);
        var startPosition = initialSlotState.RestartPosition ?? identity.WalPosition;
        await using var enumerator = replication.StartReplicationAsync(
            startPosition,
            slotName).GetAsyncEnumerator();
        await ForceWalSwitchAsync(connectionString);

        var message = await ReadXLogDataAsync(enumerator);
        var status = new BlueTuskStandbyStatus(
            message.WalEnd,
            message.WalEnd,
            message.WalEnd);
        await replication.SendStandbyStatusUpdateAsync(status);
        await replication.SendHotStandbyFeedbackAsync(default);

        Assert.Equal(message.WalEnd, replication.LastReceivedWalPosition);
        Assert.Equal(status, replication.StandbyStatus);
        var activeSlot = Assert.Single(
            await replication.GetReplicationSlotsAsync(),
            candidate => candidate.SlotName == slotName);
        Assert.Equal("physical", activeSlot.SlotType);
        Assert.True(activeSlot.IsActive);

        await enumerator.DisposeAsync();
        var discovered = await replication.ReadReplicationSlotAsync(slotName);
        Assert.Equal("physical", discovered.SlotType);
        Assert.NotNull(discovered.RestartPosition);
        Assert.NotNull(discovered.RestartTimeline);
    }

    [Fact]
    public async Task Logical_connection_uses_the_convenience_pgoutput_stream()
    {
        var connectionString = GetConnectionString();
        var suffix = Guid.NewGuid().ToString("N");
        var tableName = $"bluetusk_replication_{suffix}";
        var publicationName = $"bluetusk_publication_{suffix}";
        var slotName = $"bluetusk_logical_{suffix}";
        var quotedTable = BlueTuskSql.QuoteIdentifier(tableName);
        var quotedPublication = BlueTuskSql.QuoteIdentifier(publicationName);

        await using var administration = new BlueTuskConnection(connectionString);
        await administration.OpenAsync(CancellationToken.None);
        await ExecuteAsync(
            administration,
            $"CREATE TABLE {quotedTable} (id int PRIMARY KEY, value text NOT NULL)");
        try
        {
            await ExecuteAsync(
                administration,
                $"CREATE PUBLICATION {quotedPublication} FOR TABLE {quotedTable}");
            try
            {
                await using var replication =
                    await BlueTuskLogicalReplicationConnection.OpenAsync(connectionString);
                var slot = await replication.CreateReplicationSlotAsync(
                    slotName,
                    temporary: true);
                Assert.Equal("pgoutput", slot.OutputPlugin);
                var publication = Assert.Single(
                    await replication.GetPublicationsAsync(),
                    candidate => candidate.Name == publicationName);
                Assert.True(publication.PublishesInserts);
                var publicationTable = Assert.Single(
                    await replication.GetPublicationTablesAsync(publicationName));
                Assert.Equal(tableName, publicationTable.TableName);
                Assert.Equal(["id", "value"], publicationTable.Columns);
                Assert.Null(publicationTable.RowFilter);
                var discoveredSlot = Assert.Single(
                    await replication.GetReplicationSlotsAsync(),
                    candidate => candidate.SlotName == slotName);
                Assert.Equal("logical", discoveredSlot.SlotType);
                Assert.Equal("pgoutput", discoveredSlot.OutputPlugin);

                await using var enumerator = replication.StartReplicationAsync(
                    slotName,
                    publicationName).GetAsyncEnumerator();
                var firstMessage = enumerator.MoveNextAsync().AsTask();
                await ExecuteAsync(
                    administration,
                    $"INSERT INTO {quotedTable} VALUES (1, 'hello')");

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var completed = await Task.WhenAny(
                    firstMessage,
                    Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token));
                Assert.Same(firstMessage, completed);
                Assert.True(await firstMessage);
                var decoder = new BlueTuskPgOutputDecoder();
                var xLogData = enumerator.Current as BlueTuskXLogData ??
                    await ReadXLogDataAsync(enumerator);
                BlueTuskPgOutputRelation? relation = null;
                BlueTuskPgOutputInsert? insert = null;
                do
                {
                    var decoded = decoder.Decode(xLogData).Message;
                    relation = decoded as BlueTuskPgOutputRelation ?? relation;
                    insert = decoded as BlueTuskPgOutputInsert;
                    if (insert is null)
                    {
                        xLogData = await ReadXLogDataAsync(enumerator);
                    }
                }
                while (insert is null);

                Assert.NotNull(relation);
                Assert.Equal(tableName, relation.Name);
                Assert.Equal(relation.RelationId, insert.RelationId);
                Assert.Equal(2, insert.NewRow.Values.Count);
                Assert.Equal(
                    "1",
                    System.Text.Encoding.UTF8.GetString(
                        insert.NewRow.Values[0].Data.Span));
                Assert.Equal(
                    "hello",
                    System.Text.Encoding.UTF8.GetString(
                        insert.NewRow.Values[1].Data.Span));
                await replication.SendStandbyStatusUpdateAsync(
                    new BlueTuskStandbyStatus(
                        xLogData.WalEnd,
                        xLogData.WalEnd,
                        xLogData.WalEnd));
            }
            finally
            {
                await ExecuteAsync(
                    administration,
                    $"DROP PUBLICATION IF EXISTS {quotedPublication}");
            }
        }
        finally
        {
            await ExecuteAsync(administration, $"DROP TABLE IF EXISTS {quotedTable}");
        }
    }

    [Fact]
    public async Task Streams_assembles_live_pgoutput_DML_as_one_ordered_transaction()
    {
        var connectionString = GetConnectionString();
        var suffix = Guid.NewGuid().ToString("N");
        var tableName = $"bluetusk_streams_{suffix}";
        var publicationName = $"bluetusk_streams_publication_{suffix}";
        var slotName = $"bluetusk_streams_slot_{suffix}";
        var quotedTable = BlueTuskSql.QuoteIdentifier(tableName);
        var quotedPublication = BlueTuskSql.QuoteIdentifier(publicationName);

        await using var administration = new BlueTuskConnection(connectionString);
        await administration.OpenAsync(CancellationToken.None);
        await ExecuteAsync(
            administration,
            $"CREATE TABLE {quotedTable} (id int PRIMARY KEY, value text, payload text)");
        try
        {
            await ExecuteAsync(
                administration,
                $"CREATE PUBLICATION {quotedPublication} FOR TABLE {quotedTable}");
            try
            {
                await using var replication =
                    await BlueTuskLogicalReplicationConnection.OpenAsync(connectionString);
                var identity = await replication.IdentifySystemAsync();
                _ = await replication.CreateReplicationSlotAsync(slotName, temporary: true);
                var stream = new PgOutputChangeStream(
                    replication.StartReplicationAsync(slotName, publicationName).DecodePgOutputAsync(),
                    new ChangeSourceIdentity(
                        identity.SystemIdentifier,
                        identity.DatabaseName!,
                        slotName,
                        publicationName));
                await using var enumerator = stream.ReadTransactionsAsync().GetAsyncEnumerator();
                var deliveryTask = enumerator.MoveNextAsync().AsTask();

                await using (var transaction = await administration.BeginTransactionAsync(CancellationToken.None))
                {
                    await ExecuteAsync(
                        administration,
                        $"INSERT INTO {quotedTable} VALUES (1, 'before', repeat('x', 4096))",
                        transaction);
                    await ExecuteAsync(
                        administration,
                        $"UPDATE {quotedTable} SET value = 'after' WHERE id = 1",
                        transaction);
                    await ExecuteAsync(
                        administration,
                        $"DELETE FROM {quotedTable} WHERE id = 1",
                        transaction);
                    await transaction.CommitAsync(CancellationToken.None);
                }

                Assert.True(await deliveryTask.WaitAsync(TimeSpan.FromSeconds(15)));
                var delivery = enumerator.Current;
                var changes = await delivery.Transaction.Changes.MaterializeAsync();
                Assert.Collection(
                    changes,
                    change => Assert.IsType<InsertChange>(change),
                    change => Assert.IsType<UpdateChange>(change),
                    change => Assert.IsType<DeleteChange>(change));
                Assert.Equal([0, 1, 2], changes.Select(change => change.Id.Ordinal));
                Assert.Equal(delivery.Transaction.CommitEndPosition, changes[2].Id.CommitEndPosition);
                await delivery.AcknowledgeAsync();

                var truncateDeliveryTask = enumerator.MoveNextAsync().AsTask();
                await ExecuteAsync(administration, $"TRUNCATE TABLE {quotedTable}");
                Assert.True(await truncateDeliveryTask.WaitAsync(TimeSpan.FromSeconds(15)));
                var truncateDelivery = enumerator.Current;
                var truncate = Assert.IsType<TruncateChange>(
                    Assert.Single(await truncateDelivery.Transaction.Changes.MaterializeAsync()));
                Assert.Equal(tableName, Assert.Single(truncate.Tables).Name);
                await truncateDelivery.AcknowledgeAsync();
            }
            finally
            {
                await ExecuteAsync(
                    administration,
                    $"DROP PUBLICATION IF EXISTS {quotedPublication}");
            }
        }
        finally
        {
            await ExecuteAsync(administration, $"DROP TABLE IF EXISTS {quotedTable}");
        }
    }

    [Fact]
    public async Task Logical_replication_validates_and_resumes_durable_checkpoints_across_sessions()
    {
        var connectionString = GetConnectionString();
        var suffix = Guid.NewGuid().ToString("N");
        var tableName = $"bluetusk_resume_{suffix}";
        var publicationName = $"bluetusk_resume_publication_{suffix}";
        var slotName = $"bluetusk_resume_slot_{suffix}";
        var quotedTable = BlueTuskSql.QuoteIdentifier(tableName);
        var quotedPublication = BlueTuskSql.QuoteIdentifier(publicationName);
        await using var administration = new BlueTuskConnection(connectionString);
        await administration.OpenAsync(CancellationToken.None);
        await ExecuteAsync(
            administration,
            $"CREATE TABLE {quotedTable} (id int PRIMARY KEY, value text NOT NULL)");

        BlueTuskLogicalReplicationCheckpoint? initialCheckpoint = null;
        var receivedIds = new List<int>();
        try
        {
            await ExecuteAsync(
                administration,
                $"CREATE PUBLICATION {quotedPublication} FOR TABLE {quotedTable}");
            BlueTuskLogicalReplicationCheckpoint checkpoint;
            await using (var setup =
                await BlueTuskLogicalReplicationConnection.OpenAsync(connectionString))
            {
                var identity = await setup.IdentifySystemAsync();
                var slot = await setup.CreateReplicationSlotAsync(slotName, temporary: false);
                checkpoint = new BlueTuskLogicalReplicationCheckpoint(
                    identity.SystemIdentifier,
                    identity.DatabaseName!,
                    slotName,
                    "pgoutput",
                    slot.ConsistentPoint);
                initialCheckpoint = checkpoint;
            }

            var epochs = GetDurabilityEpochs();
            const int rowsPerEpoch = 4;
            for (var epoch = 0; epoch < epochs; epoch++)
            {
                await using var replication =
                    await BlueTuskLogicalReplicationConnection.OpenAsync(connectionString);
                var validatedSlot = await replication.ValidateResumeCheckpointAsync(checkpoint);
                Assert.Equal(slotName, validatedSlot.SlotName);
                Assert.False(validatedSlot.IsActive);

                if (epoch == 0)
                {
                    var wrongSystem = checkpoint with
                    {
                        SystemIdentifier = checkpoint.SystemIdentifier + "-other",
                    };
                    await Assert.ThrowsAsync<BlueTuskReplicationCheckpointException>(
                        () => replication.ValidateResumeCheckpointAsync(wrongSystem).AsTask());
                }

                await using var enumerator = replication.StartReplicationAsync(
                    new BlueTuskPgOutputReplicationOptions
                    {
                        SlotName = slotName,
                        PublicationNames = [publicationName],
                        StartPosition = checkpoint.AppliedPosition,
                    }).GetAsyncEnumerator();
                var firstMove = enumerator.MoveNextAsync().AsTask();
                if (epoch == 0)
                {
                    await using var contender =
                        await BlueTuskLogicalReplicationConnection.OpenAsync(connectionString);
                    using var activeTimeout =
                        new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    while (!(await contender.GetReplicationSlotsAsync(activeTimeout.Token))
                        .Single(slot => slot.SlotName == slotName)
                        .IsActive)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(25), activeTimeout.Token);
                    }

                    var activeException =
                        await Assert.ThrowsAsync<BlueTuskReplicationCheckpointException>(
                            () => contender.ValidateResumeCheckpointAsync(
                                checkpoint,
                                activeTimeout.Token).AsTask());
                    Assert.Contains("already active", activeException.Message, StringComparison.Ordinal);

                    var missingSlot = checkpoint with { SlotName = slotName + "_missing" };
                    var missingException =
                        await Assert.ThrowsAsync<BlueTuskReplicationCheckpointException>(
                            () => contender.ValidateResumeCheckpointAsync(
                                missingSlot,
                                activeTimeout.Token).AsTask());
                    Assert.Contains("no longer exists", missingException.Message, StringComparison.Ordinal);
                }

                var firstId = epoch * rowsPerEpoch + 1;
                await ExecuteAsync(
                    administration,
                    $"INSERT INTO {quotedTable} " +
                    $"SELECT id, 'value-' || id::text FROM generate_series({firstId}, {firstId + rowsPerEpoch - 1}) AS id");

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                Assert.True(await firstMove.WaitAsync(timeout.Token));
                var transaction = await ReadCommittedTransactionAsync(
                    enumerator,
                    timeout.Token);
                receivedIds.AddRange(transaction.InsertedIds);

                checkpoint = checkpoint with { AppliedPosition = transaction.EndPosition };
                await replication.SendStandbyStatusUpdateAsync(
                    new BlueTuskStandbyStatus(
                        checkpoint.AppliedPosition,
                        checkpoint.AppliedPosition,
                        checkpoint.AppliedPosition),
                    timeout.Token);
            }

            Assert.Equal(
                Enumerable.Range(1, epochs * rowsPerEpoch),
                receivedIds.Order());

            await using (var validation =
                await BlueTuskLogicalReplicationConnection.OpenAsync(connectionString))
            {
                _ = await validation.ValidateResumeCheckpointAsync(checkpoint);
                var unsafeOldCheckpoint = Assert.IsType<BlueTuskLogicalReplicationCheckpoint>(
                    initialCheckpoint);
                var exception = await Assert.ThrowsAsync<BlueTuskReplicationCheckpointException>(
                    () => validation.ValidateResumeCheckpointAsync(unsafeOldCheckpoint).AsTask());
                Assert.True(
                    exception.Message.Contains("ahead of the durable", StringComparison.Ordinal) ||
                    exception.Message.Contains("older than", StringComparison.Ordinal),
                    exception.Message);
            }
        }
        finally
        {
            await using (var cleanup =
                await BlueTuskLogicalReplicationConnection.OpenAsync(connectionString))
            {
                if ((await cleanup.GetReplicationSlotsAsync()).Any(slot => slot.SlotName == slotName))
                {
                    await cleanup.DropReplicationSlotAsync(slotName);
                }
            }

            await ExecuteAsync(
                administration,
                $"DROP PUBLICATION IF EXISTS {quotedPublication}");
            await ExecuteAsync(administration, $"DROP TABLE IF EXISTS {quotedTable}");
        }
    }

    [Fact]
    public async Task Pgoutput_streams_large_in_progress_transactions()
    {
        var connectionString = GetConnectionString();
        var suffix = Guid.NewGuid().ToString("N");
        var tableName = $"bluetusk_streaming_{suffix}";
        var publicationName = $"bluetusk_streaming_pub_{suffix}";
        var slotName = $"bluetusk_streaming_slot_{suffix}";
        var quotedTable = BlueTuskSql.QuoteIdentifier(tableName);
        var quotedPublication = BlueTuskSql.QuoteIdentifier(publicationName);
        var transactionOpen = false;

        await using var administration = new BlueTuskConnection(connectionString);
        await administration.OpenAsync(CancellationToken.None);
        await ExecuteAsync(
            administration,
            $"CREATE TABLE {quotedTable} (id int PRIMARY KEY, value text NOT NULL)");
        try
        {
            await ExecuteAsync(
                administration,
                $"CREATE PUBLICATION {quotedPublication} FOR TABLE {quotedTable}");
            try
            {
                await using var replication =
                    await BlueTuskLogicalReplicationConnection.OpenAsync(connectionString);
                _ = await replication.CreateReplicationSlotAsync(
                    slotName,
                    temporary: true);

                await ExecuteAsync(administration, "BEGIN");
                transactionOpen = true;
                await ExecuteAsync(
                    administration,
                    $"""
                     INSERT INTO {quotedTable}
                     SELECT value, repeat('x', 1024)
                     FROM generate_series(1, 512) AS value
                     """);

                await using var enumerator = replication.StartReplicationAsync(
                    new BlueTuskPgOutputReplicationOptions
                    {
                        SlotName = slotName,
                        PublicationNames = [publicationName],
                        ProtocolVersion = 2,
                        StreamingMode = BlueTuskLogicalStreamingMode.On,
                    }).GetAsyncEnumerator();
                var decoder = new BlueTuskPgOutputDecoder(
                    new BlueTuskPgOutputDecoderOptions
                    {
                        ProtocolVersion = 2,
                        StreamingMode = BlueTuskPgOutputStreamingMode.On,
                    });
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

                var streamStart = Assert.IsType<BlueTuskPgOutputStreamStart>(
                    (await ReadDecodedUntilAsync(
                        enumerator,
                        decoder,
                        static message => message is BlueTuskPgOutputStreamStart,
                        timeout.Token)).Message);
                var streamedInsert = Assert.IsType<BlueTuskPgOutputInsert>(
                    (await ReadDecodedUntilAsync(
                        enumerator,
                        decoder,
                        static message => message is BlueTuskPgOutputInsert,
                        timeout.Token)).Message);
                _ = Assert.IsType<BlueTuskPgOutputStreamStop>(
                    (await ReadDecodedUntilAsync(
                        enumerator,
                        decoder,
                        static message => message is BlueTuskPgOutputStreamStop,
                        timeout.Token)).Message);

                Assert.Equal(streamStart.TransactionId, streamedInsert.StreamingTransactionId);
                await ExecuteAsync(administration, "COMMIT");
                transactionOpen = false;

                var streamCommit = Assert.IsType<BlueTuskPgOutputStreamCommit>(
                    (await ReadDecodedUntilAsync(
                        enumerator,
                        decoder,
                        static message => message is BlueTuskPgOutputStreamCommit,
                        timeout.Token)).Message);
                Assert.Equal(streamStart.TransactionId, streamCommit.TransactionId);
            }
            finally
            {
                if (transactionOpen)
                {
                    await ExecuteAsync(administration, "ROLLBACK");
                    transactionOpen = false;
                }

                await ExecuteAsync(
                    administration,
                    $"DROP PUBLICATION IF EXISTS {quotedPublication}");
            }
        }
        finally
        {
            await ExecuteAsync(administration, $"DROP TABLE IF EXISTS {quotedTable}");
        }
    }

    [Fact]
    public async Task Streams_stages_and_commits_a_live_prepared_transaction()
    {
        var connectionString = GetConnectionString();
        var suffix = Guid.NewGuid().ToString("N");
        var tableName = $"bluetusk_streams_twophase_{suffix}";
        var publicationName = $"bluetusk_streams_twophase_pub_{suffix}";
        var slotName = $"bluetusk_streams_twophase_slot_{suffix}";
        var globalTransactionId = $"bluetusk_streams_gid_{suffix}";
        var quotedTable = BlueTuskSql.QuoteIdentifier(tableName);
        var quotedPublication = BlueTuskSql.QuoteIdentifier(publicationName);
        var quotedGlobalTransactionId = BlueTuskSql.QuoteLiteral(globalTransactionId);
        var prepared = false;

        await using var administration = new BlueTuskConnection(connectionString);
        await administration.OpenAsync(CancellationToken.None);
        await ExecuteAsync(
            administration,
            $"CREATE TABLE {quotedTable} (id int PRIMARY KEY, value text NOT NULL)");
        try
        {
            await ExecuteAsync(
                administration,
                $"CREATE PUBLICATION {quotedPublication} FOR TABLE {quotedTable}");
            try
            {
                await using var dataSource = BlueTuskDataSource.Create(connectionString);
                BlueTuskReplicationSystemIdentity system;
                await using (var identityConnection =
                    await BlueTuskLogicalReplicationConnection.OpenAsync(connectionString))
                {
                    system = await identityConnection.IdentifySystemAsync();
                }

                var table = new ChangeTable(
                    relationId: 0,
                    schema: "public",
                    name: tableName,
                    replicaIdentity: 'd',
                    [
                        new ChangeColumn(0, "id", 23, -1, true),
                        new ChangeColumn(1, "value", 25, -1, false),
                    ]);
                var source = new PostgreSqlConsistentSnapshotSource(
                    dataSource,
                    new PostgreSqlConsistentSnapshotOptions
                    {
                        Source = new ChangeSourceIdentity(
                            system.SystemIdentifier,
                            system.DatabaseName!,
                            slotName,
                            publicationName),
                        PublicationNames = [publicationName],
                        Tables = [new PostgreSqlSnapshotTable(table, [0])],
                        TransactionAssembly = new TransactionAssemblyOptions
                        {
                            PreparedTransactionMode = PreparedTransactionMode.Stage,
                        },
                    });
                await using var attempt = await source.BeginAttemptAsync(abandonedEpoch: null);
                await foreach (var _ in attempt.ReadSnapshotAsync())
                {
                }

                var stream = attempt.CreateChangeStream();
                await using var enumerator = stream.ReadTransactionsAsync().GetAsyncEnumerator();

                // PostgreSQL emits a prepared transaction as an ordinary commit when PREPARE
                // happens before logical decoding has consumed it. A non-transactional message
                // provides a deterministic stream-readiness barrier on every supported release.
                var readyMove = enumerator.MoveNextAsync().AsTask();
                await ExecuteAsync(
                    administration,
                    $"SELECT pg_logical_emit_message(false, " +
                    $"{BlueTuskSql.QuoteLiteral($"bluetusk-ready-{suffix}")}, 'ready')");
                Assert.True(await readyMove.WaitAsync(TimeSpan.FromSeconds(20)));
                Assert.True(enumerator.Current.Transaction.IsSynthetic);
                await enumerator.Current.AcknowledgeAsync();

                var preparedMove = enumerator.MoveNextAsync().AsTask();

                await ExecuteAsync(administration, "BEGIN");
                await ExecuteAsync(
                    administration,
                    $"INSERT INTO {quotedTable} VALUES (1, 'prepared')");
                await ExecuteAsync(
                    administration,
                    $"PREPARE TRANSACTION {quotedGlobalTransactionId}");
                prepared = true;

                Assert.True(await preparedMove.WaitAsync(TimeSpan.FromSeconds(20)));
                var staged = enumerator.Current;
                Assert.Equal(ChangeTransactionOutcome.Prepared, staged.Transaction.Outcome);
                Assert.Equal(globalTransactionId, staged.Transaction.GlobalTransactionId);
                var insert = Assert.IsType<InsertChange>(
                    Assert.Single(await staged.Transaction.Changes.MaterializeAsync()));
                Assert.Equal("1", System.Text.Encoding.UTF8.GetString(insert.NewRow["id"].Data.Span));
                await staged.AcknowledgeAsync();

                var committedMove = enumerator.MoveNextAsync().AsTask();
                await ExecuteAsync(
                    administration,
                    $"COMMIT PREPARED {quotedGlobalTransactionId}");
                prepared = false;

                Assert.True(await committedMove.WaitAsync(TimeSpan.FromSeconds(20)));
                var committed = enumerator.Current;
                Assert.Equal(ChangeTransactionOutcome.Committed, committed.Transaction.Outcome);
                Assert.Equal(globalTransactionId, committed.Transaction.GlobalTransactionId);
                Assert.Empty(await committed.Transaction.Changes.MaterializeAsync());
                await committed.AcknowledgeAsync();
            }
            finally
            {
                if (prepared)
                {
                    await ExecuteAsync(
                        administration,
                        $"ROLLBACK PREPARED {quotedGlobalTransactionId}");
                }

                await using var cleanup =
                    await BlueTuskLogicalReplicationConnection.OpenAsync(connectionString);
                var slots = await cleanup.GetReplicationSlotsAsync();
                if (slots.Any(slot => string.Equals(slot.SlotName, slotName, StringComparison.Ordinal)))
                {
                    await cleanup.DropReplicationSlotAsync(slotName, wait: true);
                }

                await ExecuteAsync(
                    administration,
                    $"DROP PUBLICATION IF EXISTS {quotedPublication}");
            }
        }
        finally
        {
            await ExecuteAsync(administration, $"DROP TABLE IF EXISTS {quotedTable}");
        }
    }

    [Fact]
    public async Task Pgoutput_decodes_prepared_transaction_metadata()
    {
        var connectionString = GetConnectionString();
        var suffix = Guid.NewGuid().ToString("N");
        var tableName = $"bluetusk_twophase_{suffix}";
        var publicationName = $"bluetusk_twophase_pub_{suffix}";
        var slotName = $"bluetusk_twophase_slot_{suffix}";
        var globalTransactionId = $"bluetusk_gid_{suffix}";
        var quotedTable = BlueTuskSql.QuoteIdentifier(tableName);
        var quotedPublication = BlueTuskSql.QuoteIdentifier(publicationName);
        var quotedGlobalTransactionId = BlueTuskSql.QuoteLiteral(globalTransactionId);
        var prepared = false;

        await using var administration = new BlueTuskConnection(connectionString);
        await administration.OpenAsync(CancellationToken.None);
        await ExecuteAsync(
            administration,
            $"CREATE TABLE {quotedTable} (id int PRIMARY KEY, value text NOT NULL)");
        try
        {
            await ExecuteAsync(
                administration,
                $"CREATE PUBLICATION {quotedPublication} FOR TABLE {quotedTable}");
            try
            {
                await using var replication =
                    await BlueTuskLogicalReplicationConnection.OpenAsync(connectionString);
                _ = await replication.CreateReplicationSlotAsync(
                    slotName,
                    temporary: true,
                    twoPhase: true);
                await using var enumerator = replication.StartReplicationAsync(
                    new BlueTuskPgOutputReplicationOptions
                    {
                        SlotName = slotName,
                        PublicationNames = [publicationName],
                        ProtocolVersion = 3,
                        StreamingMode = BlueTuskLogicalStreamingMode.On,
                        TwoPhase = true,
                    }).GetAsyncEnumerator();

                var firstMove = enumerator.MoveNextAsync().AsTask();
                await ExecuteAsync(administration, "BEGIN");
                await ExecuteAsync(
                    administration,
                    $"INSERT INTO {quotedTable} VALUES (1, 'prepared')");
                await ExecuteAsync(
                    administration,
                    $"PREPARE TRANSACTION {quotedGlobalTransactionId}");
                prepared = true;

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                Assert.True(await firstMove.WaitAsync(timeout.Token));
                var decoder = new BlueTuskPgOutputDecoder(
                    new BlueTuskPgOutputDecoderOptions
                    {
                        ProtocolVersion = 3,
                        StreamingMode = BlueTuskPgOutputStreamingMode.On,
                        TwoPhase = true,
                    });
                BlueTuskPgOutputBeginPrepare? beginPrepare = null;
                BlueTuskPgOutputInsert? insert = null;
                BlueTuskPgOutputPrepare? prepare = null;
                if (enumerator.Current is BlueTuskXLogData initialData)
                {
                    CapturePreparedMessage(
                        decoder.Decode(initialData).Message,
                        ref beginPrepare,
                        ref insert,
                        ref prepare);
                }

                while (prepare is null)
                {
                    var decoded = await ReadDecodedUntilAsync(
                        enumerator,
                        decoder,
                        static message =>
                            message is BlueTuskPgOutputBeginPrepare or
                                BlueTuskPgOutputInsert or
                                BlueTuskPgOutputPrepare,
                        timeout.Token);
                    CapturePreparedMessage(
                        decoded.Message,
                        ref beginPrepare,
                        ref insert,
                        ref prepare);
                }

                Assert.NotNull(beginPrepare);
                Assert.NotNull(insert);
                Assert.Equal(globalTransactionId, beginPrepare.GlobalTransactionId);
                Assert.Equal(globalTransactionId, prepare.GlobalTransactionId);
                Assert.Equal(beginPrepare.TransactionId, prepare.TransactionId);
            }
            finally
            {
                if (prepared)
                {
                    await ExecuteAsync(
                        administration,
                        $"ROLLBACK PREPARED {quotedGlobalTransactionId}");
                    prepared = false;
                }

                await ExecuteAsync(
                    administration,
                    $"DROP PUBLICATION IF EXISTS {quotedPublication}");
            }
        }
        finally
        {
            await ExecuteAsync(administration, $"DROP TABLE IF EXISTS {quotedTable}");
        }
    }

    [Fact]
    public async Task Logical_connection_streams_custom_output_plugin_payloads()
    {
        var connectionString = GetConnectionString();
        var suffix = Guid.NewGuid().ToString("N");
        var tableName = $"bluetusk_custom_plugin_{suffix}";
        var slotName = $"bluetusk_custom_slot_{suffix}";
        var quotedTable = BlueTuskSql.QuoteIdentifier(tableName);

        await using var administration = new BlueTuskConnection(connectionString);
        await administration.OpenAsync(CancellationToken.None);
        await ExecuteAsync(
            administration,
            $"CREATE TABLE {quotedTable} (id int PRIMARY KEY, value text NOT NULL)");
        try
        {
            await using var replication =
                await BlueTuskLogicalReplicationConnection.OpenAsync(connectionString);
            var slot = await replication.CreateReplicationSlotAsync(
                slotName,
                outputPlugin: "test_decoding",
                temporary: true);
            Assert.Equal("test_decoding", slot.OutputPlugin);

            await using var enumerator = replication.StartReplicationAsync(
                new BlueTuskLogicalReplicationRequest
                {
                    SlotName = slotName,
                }).GetAsyncEnumerator();
            var firstMove = enumerator.MoveNextAsync().AsTask();
            await ExecuteAsync(
                administration,
                $"INSERT INTO {quotedTable} VALUES (1, 'custom')");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Assert.True(await firstMove.WaitAsync(timeout.Token));
            string? decodedText = enumerator.Current is BlueTuskXLogData initial
                ? System.Text.Encoding.UTF8.GetString(initial.Data.Span)
                : null;
            while (decodedText is null ||
                !decodedText.Contains("INSERT:", StringComparison.Ordinal) ||
                !decodedText.Contains(tableName, StringComparison.Ordinal))
            {
                var xLogData = await ReadXLogDataAsync(enumerator);
                decodedText = System.Text.Encoding.UTF8.GetString(xLogData.Data.Span);
            }

            Assert.Contains(tableName, decodedText, StringComparison.Ordinal);
            Assert.Contains("'custom'", decodedText, StringComparison.Ordinal);
        }
        finally
        {
            await ExecuteAsync(administration, $"DROP TABLE IF EXISTS {quotedTable}");
        }
    }

    private static async Task<BlueTuskXLogData> ReadXLogDataAsync(
        IAsyncEnumerator<BlueTuskReplicationMessage> enumerator)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await enumerator.MoveNextAsync().AsTask().WaitAsync(timeout.Token))
        {
            if (enumerator.Current is BlueTuskXLogData xLogData)
            {
                return xLogData;
            }
        }

        throw new XunitException("The physical replication stream completed before sending WAL.");
    }

    private static async Task<BlueTuskPgOutputEnvelope> ReadDecodedUntilAsync(
        IAsyncEnumerator<BlueTuskReplicationMessage> enumerator,
        BlueTuskPgOutputDecoder decoder,
        Func<BlueTuskPgOutputMessage, bool> predicate,
        CancellationToken cancellationToken)
    {
        while (await enumerator.MoveNextAsync().AsTask().WaitAsync(cancellationToken))
        {
            if (enumerator.Current is not BlueTuskXLogData xLogData)
            {
                continue;
            }

            var decoded = decoder.Decode(xLogData);
            if (predicate(decoded.Message))
            {
                return decoded;
            }
        }

        throw new XunitException(
            "The logical replication stream completed before the expected pgoutput message.");
    }

    private static async Task<(IReadOnlyList<int> InsertedIds, BlueTuskLogSequenceNumber EndPosition)>
        ReadCommittedTransactionAsync(
            IAsyncEnumerator<BlueTuskReplicationMessage> enumerator,
            CancellationToken cancellationToken)
    {
        var decoder = new BlueTuskPgOutputDecoder();
        var insertedIds = new List<int>();
        while (true)
        {
            if (enumerator.Current is BlueTuskXLogData xLogData)
            {
                var envelope = decoder.Decode(xLogData);
                if (envelope.Message is BlueTuskPgOutputInsert insert)
                {
                    insertedIds.Add(
                        int.Parse(
                            System.Text.Encoding.UTF8.GetString(
                                insert.NewRow.Values[0].Data.Span),
                            System.Globalization.CultureInfo.InvariantCulture));
                }

                if (envelope.TryGetTransactionEndPosition(out var endPosition))
                {
                    return (insertedIds, endPosition);
                }
            }

            if (!await enumerator.MoveNextAsync().AsTask().WaitAsync(cancellationToken))
            {
                throw new XunitException(
                    "Logical replication completed before the transaction checkpoint arrived.");
            }
        }
    }

    private static void CapturePreparedMessage(
        BlueTuskPgOutputMessage message,
        ref BlueTuskPgOutputBeginPrepare? beginPrepare,
        ref BlueTuskPgOutputInsert? insert,
        ref BlueTuskPgOutputPrepare? prepare)
    {
        beginPrepare = message as BlueTuskPgOutputBeginPrepare ?? beginPrepare;
        insert = message as BlueTuskPgOutputInsert ?? insert;
        prepare = message as BlueTuskPgOutputPrepare ?? prepare;
    }

    private static async Task ForceWalSwitchAsync(string connectionString)
    {
        await using var connection = new BlueTuskConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await ExecuteAsync(connection, "SELECT pg_switch_wal()");
    }

    private static async Task ExecuteAsync(
        BlueTuskConnection connection,
        string sql,
        BlueTuskTransaction? transaction = null)
    {
        await using var command = new BlueTuskCommand(sql, connection)
        {
            Transaction = transaction,
        };
        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var settings = new BlueTuskConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        };
        return settings.ConnectionString;
    }

    private static int GetDurabilityEpochs()
    {
        var configured = Environment.GetEnvironmentVariable(
            "BLUETUSK_REPLICATION_DURABILITY_EPOCHS");
        return int.TryParse(
                configured,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var epochs) && epochs > 0
            ? epochs
            : 3;
    }
}
