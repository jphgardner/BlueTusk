using System.Runtime.CompilerServices;
using BlueTusk.Data;
using BlueTusk.Replication;
using BlueTusk.Replication.PgOutput;
using BlueTusk.Streams;
using BlueTusk.Streams.Storage.PostgreSql;
using BlueTusk.TypeSystem;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskStreamsRelayIntegrationTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PostgreSql_relay_change_stream_renews_fences_and_redelivers_until_acknowledged()
    {
        var connectionString = GetConnectionString();
        var schema = "bluetusk_relay_stream_test_" + Guid.NewGuid().ToString("N");
        await using var dataSource = BlueTuskDataSource.Create(connectionString);
        var relay = new PostgreSqlDurableChangeRelay(
            new PostgreSqlStreamsStorageOptions
            {
                ControlDataSource = dataSource,
                ControlSchema = schema,
                MaxRelayStorageBytes = 1024 * 1024,
            });
        try
        {
            await relay.InitializeAsync();
            var source = await relay.RegisterSourceAsync(SourceIdentity());
            var sourceLease = Assert.IsType<ChangeStreamLease>(
                (await relay.AcquireSourceLeaseAsync(
                    source,
                    "relay-source",
                    TimeSpan.FromMinutes(1))).Lease);
            var direct = CreateStream(source.Source);
            await using (var directEnumerator = direct.ReadTransactionsAsync().GetAsyncEnumerator())
            {
                Assert.True(await directEnumerator.MoveNextAsync());
                _ = await relay.AppendAsync(
                    source,
                    directEnumerator.Current.Transaction,
                    sourceLease);
            }

            Assert.True(await relay.ReleaseSourceLeaseAsync(sourceLease));
            var group = await relay.CreateConsumerGroupAsync(source, "sync-orders");
            var firstStream = CreateRelayStream(relay, source, "sync-orders", "worker-a");
            await using (var first = firstStream.ReadTransactionsAsync().GetAsyncEnumerator())
            {
                Assert.True(await first.MoveNextAsync());
                Assert.Equal(1U, first.Current.Transaction.TransactionId);

                var competing = CreateRelayStream(relay, source, "sync-orders", "worker-b");
                await using var competingEnumerator =
                    competing.ReadTransactionsAsync().GetAsyncEnumerator();
                await Assert.ThrowsAsync<ChangeRelayLeaseUnavailableException>(
                    () => competingEnumerator.MoveNextAsync().AsTask());

                await Task.Delay(TimeSpan.FromMilliseconds(750));
            }

            var retryStream = CreateRelayStream(relay, source, "sync-orders", "worker-b");
            await using (var retry = retryStream.ReadTransactionsAsync().GetAsyncEnumerator())
            {
                Assert.True(await retry.MoveNextAsync());
                await retry.Current.AcknowledgeAsync();
            }

            var inspectionLease = Assert.IsType<ChangeRelayGroupLease>(
                await relay.AcquireConsumerGroupAsync(
                    group,
                    "inspector",
                    TimeSpan.FromMinutes(1)));
            var empty = await relay.ReadConsumerGroupAsync(
                inspectionLease,
                maxTransactions: 1,
                maxBytes: 1024 * 1024);
            Assert.Empty(empty.Records);
            Assert.Equal(1, empty.Group.CheckpointSequence);
        }
        finally
        {
            await DropSchemaAsync(dataSource, schema);
        }
    }

    [Fact]
    public async Task PostgreSql_relay_fans_out_replays_fences_and_retains_safely()
    {
        var connectionString = GetConnectionString();
        var schema = "bluetusk_relay_test_" + Guid.NewGuid().ToString("N");
        await using var dataSource = BlueTuskDataSource.Create(connectionString);
        var relay = new PostgreSqlDurableChangeRelay(
            new PostgreSqlStreamsStorageOptions
            {
                ControlDataSource = dataSource,
                ControlSchema = schema,
                ResumeRetentionWindow = TimeSpan.Zero,
                MaxRelayStorageBytes = 1024 * 1024,
                MaxWalLagBytes = 1,
            });
        try
        {
            await relay.InitializeAsync();
            var source = SourceIdentity();
            var feedback = new RecordingFeedbackSender { FailuresRemaining = 1 };
            await using var observer = await PostgreSqlRelayChangeDeliveryObserver.AcquireAsync(
                relay,
                source,
                "source-worker",
                TimeSpan.FromMinutes(1),
                feedback);
            var stream = CreateStream(source, observer);
            await using var enumerator = stream.ReadTransactionsAsync().GetAsyncEnumerator();
            Assert.True(await enumerator.MoveNextAsync());
            var delivery = enumerator.Current;

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => delivery.AcknowledgeAsync().AsTask());
            await delivery.AcknowledgeAsync();

            Assert.Equal(2, feedback.AttemptCount);
            Assert.Equal(Lsn(21), Assert.Single(feedback.Positions));
            var firstGroup = await relay.CreateConsumerGroupAsync(observer.Source, "sync-a");
            var secondGroup = await relay.CreateConsumerGroupAsync(observer.Source, "live-b");
            var firstLease = Assert.IsType<ChangeRelayGroupLease>(
                await relay.AcquireConsumerGroupAsync(
                    firstGroup,
                    "consumer-a",
                    TimeSpan.FromMinutes(1)));
            var secondLease = Assert.IsType<ChangeRelayGroupLease>(
                await relay.AcquireConsumerGroupAsync(
                    secondGroup,
                    "consumer-b",
                    TimeSpan.FromMinutes(1)));

            var firstBatch = await relay.ReadConsumerGroupAsync(firstLease, 10, 1024 * 1024);
            var secondBatch = await relay.ReadConsumerGroupAsync(secondLease, 10, 1024 * 1024);
            var firstRecord = Assert.Single(firstBatch.Records);
            var secondRecord = Assert.Single(secondBatch.Records);
            Assert.Equal(firstRecord.Sequence, secondRecord.Sequence);
            Assert.Equal(1U, firstRecord.Transaction.TransactionId);
            Assert.IsType<InsertChange>(
                Assert.Single(await firstRecord.Transaction.Changes.MaterializeAsync()));
            var health = await relay.GetHealthAsync(observer.Source, Lsn(500));
            Assert.True(health.IsWalRetentionDanger);
            Assert.True(health.WalLagBytes > 0);

            var firstAck = await relay.AcknowledgeConsumerGroupAsync(
                firstLease,
                firstBatch.Group.StoreGeneration,
                firstRecord.Sequence);
            Assert.Equal(ChangeRelayAcknowledgeStatus.Stored, firstAck.Status);
            var retainedForSlowGroup = await relay.ApplyRetentionAsync(observer.Source);
            Assert.Equal(0, retainedForSlowGroup.DeletedTransactions);

            var secondAck = await relay.AcknowledgeConsumerGroupAsync(
                secondLease,
                secondBatch.Group.StoreGeneration,
                secondRecord.Sequence);
            Assert.Equal(ChangeRelayAcknowledgeStatus.Stored, secondAck.Status);

            Assert.True(await relay.ReleaseConsumerGroupAsync(firstLease));
            var replacementLease = Assert.IsType<ChangeRelayGroupLease>(
                await relay.AcquireConsumerGroupAsync(
                    firstGroup,
                    "consumer-c",
                    TimeSpan.FromMinutes(1)));
            Assert.True(replacementLease.FencingToken > firstLease.FencingToken);
            var fenced = await relay.AcknowledgeConsumerGroupAsync(
                firstLease,
                firstAck.Current.StoreGeneration,
                firstRecord.Sequence);
            Assert.Equal(ChangeRelayAcknowledgeStatus.Fenced, fenced.Status);

            var retention = await relay.ApplyRetentionAsync(observer.Source);
            Assert.Equal(1, retention.DeletedTransactions);
            Assert.True(retention.DeletedBytes > 0);
            var metrics = await relay.GetMetricsAsync(observer.Source);
            Assert.Equal(0, metrics.TransactionCount);
            Assert.Equal(0, metrics.StorageBytes);

            var empty = await relay.ReadConsumerGroupAsync(replacementLease, 10, 1024 * 1024);
            Assert.Empty(empty.Records);
        }
        finally
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
            _ = await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task PostgreSql_relay_rejects_append_before_exceeding_storage_bound()
    {
        var connectionString = GetConnectionString();
        var schema = "bluetusk_relay_limit_test_" + Guid.NewGuid().ToString("N");
        await using var dataSource = BlueTuskDataSource.Create(connectionString);
        var relay = new PostgreSqlDurableChangeRelay(
            new PostgreSqlStreamsStorageOptions
            {
                ControlDataSource = dataSource,
                ControlSchema = schema,
                MaxRelayStorageBytes = 1,
            });
        try
        {
            await relay.InitializeAsync();
            var source = await relay.RegisterSourceAsync(SourceIdentity());
            var lease = Assert.IsType<ChangeStreamLease>(
                (await relay.AcquireSourceLeaseAsync(
                    source,
                    "source-worker",
                    TimeSpan.FromMinutes(1))).Lease);
            var stream = CreateStream(source.Source);
            await using var enumerator = stream.ReadTransactionsAsync().GetAsyncEnumerator();
            Assert.True(await enumerator.MoveNextAsync());

            await Assert.ThrowsAsync<ChangeRelayStorageExhaustedException>(
                () => relay.AppendAsync(source, enumerator.Current.Transaction, lease).AsTask());

            var metrics = await relay.GetMetricsAsync(source);
            Assert.Equal(0, metrics.TransactionCount);
            Assert.Equal(0, metrics.StorageBytes);
        }
        finally
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
            _ = await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task PostgreSql_relay_migrates_protects_removes_and_compacts_safely()
    {
        var connectionString = GetConnectionString();
        var schema = "bluetusk_relay_hardening_test_" + Guid.NewGuid().ToString("N");
        await using var dataSource = BlueTuskDataSource.Create(connectionString);
        var relay = new PostgreSqlDurableChangeRelay(
            new PostgreSqlStreamsStorageOptions
            {
                ControlDataSource = dataSource,
                ControlSchema = schema,
                ResumeRetentionWindow = TimeSpan.Zero,
                RemovedConsumerGroupRetentionWindow = TimeSpan.FromHours(1),
                RetentionDeleteBatchSize = 1,
                MaxCompactionBatches = 10,
                MaxRelayStorageBytes = 1024 * 1024,
                EnvelopeProtection = new XorEnvelopeProtectionProvider(),
            });
        try
        {
            await relay.InitializeAsync();
            Assert.Equal(PostgreSqlDurableChangeRelay.CurrentSchemaVersion, await relay.GetSchemaVersionAsync());

            await ExecuteControlAsync(
                dataSource,
                $"ALTER TABLE \"{schema}\".relay_transactions DROP COLUMN protection_id");
            await ExecuteControlAsync(
                dataSource,
                $"ALTER TABLE \"{schema}\".relay_consumer_groups DROP COLUMN removed_at");
            await ExecuteControlAsync(
                dataSource,
                $"ALTER TABLE \"{schema}\".relay_consumer_groups " +
                "DROP COLUMN retention_protected_until CASCADE");
            await ExecuteControlAsync(
                dataSource,
                $"UPDATE \"{schema}\".storage_metadata SET schema_version = 1 WHERE singleton");

            await relay.InitializeAsync();
            Assert.Equal(PostgreSqlDurableChangeRelay.CurrentSchemaVersion, await relay.GetSchemaVersionAsync());

            var source = await relay.RegisterSourceAsync(SourceIdentity());
            var sourceLease = Assert.IsType<ChangeStreamLease>(
                (await relay.AcquireSourceLeaseAsync(
                    source,
                    "source-worker",
                    TimeSpan.FromMinutes(1))).Lease);
            var stream = CreateStream(source.Source);
            await using (var enumerator = stream.ReadTransactionsAsync().GetAsyncEnumerator())
            {
                Assert.True(await enumerator.MoveNextAsync());
                _ = await relay.AppendAsync(source, enumerator.Current.Transaction, sourceLease);
                await enumerator.Current.AcknowledgeAsync();
            }

            var stored = await ReadProtectedEnvelopeAsync(dataSource, schema);
            Assert.Equal("test-xor-v1", stored.ProtectionId);
            Assert.NotEmpty(stored.Payload);
            Assert.NotEqual((byte)'B', stored.Payload[0]);

            var group = await relay.CreateConsumerGroupAsync(source, "slow-consumer");
            var lease = Assert.IsType<ChangeRelayGroupLease>(
                await relay.AcquireConsumerGroupAsync(
                    group,
                    "consumer-worker",
                    TimeSpan.FromMinutes(1)));
            var batch = await relay.ReadConsumerGroupAsync(lease, 10, 1024 * 1024);
            Assert.Single(batch.Records);

            var removed = await relay.RemoveConsumerGroupAsync(
                group,
                group.StoreGeneration,
                confirmation: group.Name);
            Assert.Equal(ChangeRelayConsumerGroupRemovalStatus.Removed, removed.Status);
            Assert.False(removed.Current.IsActive);
            Assert.NotNull(removed.Current.RemovedAt);
            Assert.NotNull(removed.Current.RetentionProtectedUntil);
            await Assert.ThrowsAsync<ChangeRelayConsumerGroupException>(
                () => relay.AcquireConsumerGroupAsync(
                    removed.Current,
                    "replacement",
                    TimeSpan.FromMinutes(1)).AsTask());

            var protectedRetention = await relay.ApplyRetentionAsync(source);
            Assert.Equal(0, protectedRetention.DeletedTransactions);

            var conflict = await relay.RemoveConsumerGroupAsync(
                removed.Current,
                expectedGeneration: 0,
                confirmation: group.Name,
                ChangeRelayConsumerGroupRemovalMode.ReleaseRetentionImmediately);
            Assert.Equal(ChangeRelayConsumerGroupRemovalStatus.Conflict, conflict.Status);

            var released = await relay.RemoveConsumerGroupAsync(
                removed.Current,
                removed.Current.StoreGeneration,
                confirmation: group.Name,
                ChangeRelayConsumerGroupRemovalMode.ReleaseRetentionImmediately);
            Assert.Equal(ChangeRelayConsumerGroupRemovalStatus.RetentionReleased, released.Status);

            var compacted = await relay.CompactAsync(source);
            Assert.Equal(1, compacted.DeletedTransactions);
            Assert.True(compacted.DeletedBytes > 0);
            Assert.Equal(2, compacted.Batches);
            Assert.True(compacted.FullyApplied);
            Assert.True(compacted.Vacuumed);

            await ExecuteControlAsync(
                dataSource,
                $"UPDATE \"{schema}\".storage_metadata SET schema_version = 999 WHERE singleton");
            await Assert.ThrowsAsync<ChangeRelaySchemaVersionException>(
                () => relay.InitializeAsync().AsTask());
        }
        finally
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
            _ = await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task PostgreSql_relay_backup_restores_atomically_and_preserves_fencing_history()
    {
        var connectionString = GetConnectionString();
        var sourceSchema = "bluetusk_relay_backup_test_" + Guid.NewGuid().ToString("N");
        var restoredSchema = "bluetusk_relay_restore_test_" + Guid.NewGuid().ToString("N");
        await using var dataSource = BlueTuskDataSource.Create(connectionString);
        var protection = new XorEnvelopeProtectionProvider();
        var sourceRelay = CreateProtectedRelay(dataSource, sourceSchema, protection);
        var restoredRelay = CreateProtectedRelay(dataSource, restoredSchema, protection);
        try
        {
            await sourceRelay.InitializeAsync();
            var source = await sourceRelay.RegisterSourceAsync(SourceIdentity());
            var sourceLease = Assert.IsType<ChangeStreamLease>(
                (await sourceRelay.AcquireSourceLeaseAsync(
                    source,
                    "source-worker",
                    TimeSpan.FromMinutes(1))).Lease);
            var stream = CreateStream(source.Source);
            await using (var enumerator = stream.ReadTransactionsAsync().GetAsyncEnumerator())
            {
                Assert.True(await enumerator.MoveNextAsync());
                _ = await sourceRelay.AppendAsync(source, enumerator.Current.Transaction, sourceLease);
                await enumerator.Current.AcknowledgeAsync();
            }

            var activeGroup = await sourceRelay.CreateConsumerGroupAsync(source, "active-consumer");
            var originalLease = Assert.IsType<ChangeRelayGroupLease>(
                await sourceRelay.AcquireConsumerGroupAsync(
                    activeGroup,
                    "original-worker",
                    TimeSpan.FromMinutes(1)));
            Assert.True(await sourceRelay.ReleaseConsumerGroupAsync(originalLease));

            var removedGroup = await sourceRelay.CreateConsumerGroupAsync(source, "removed-consumer");
            var removed = await sourceRelay.RemoveConsumerGroupAsync(
                removedGroup,
                removedGroup.StoreGeneration,
                removedGroup.Name);
            Assert.Equal(ChangeRelayConsumerGroupRemovalStatus.Removed, removed.Status);
            await SeedBackupStateAsync(dataSource, sourceSchema, source);

            await using var backup = new MemoryStream();
            var backupResult = await sourceRelay.BackupAsync(source, backup);
            Assert.Equal(ChangeRelayBackupFormat.CurrentVersion, backupResult.FormatVersion);
            Assert.Equal(source.Source.Fingerprint, backupResult.SourceFingerprint);
            Assert.Equal(1, backupResult.Transactions);
            Assert.Equal(2, backupResult.ConsumerGroups);
            Assert.Equal(1, backupResult.SnapshotRuns);
            Assert.Equal(1, backupResult.DeadLetters);
            Assert.Equal(backup.Length, backupResult.BytesWritten);

            backup.Position = 0;
            await Assert.ThrowsAsync<ChangeRelayBackupException>(
                () => restoredRelay.RestoreAsync(backup, "wrong-source").AsTask());

            var corrupted = CorruptSecondBackupFrame(backup.ToArray());
            await using (var corruptedBackup = new MemoryStream(corrupted, writable: false))
            {
                await Assert.ThrowsAsync<ChangeRelayBackupException>(
                    () => restoredRelay.RestoreAsync(
                        corruptedBackup,
                        source.Source.Fingerprint).AsTask());
            }

            backup.Position = 0;
            var restoreResult = await restoredRelay.RestoreAsync(
                backup,
                source.Source.Fingerprint);
            Assert.Equal(ChangeRelayBackupFormat.CurrentVersion, restoreResult.FormatVersion);
            Assert.Equal(source.Source, restoreResult.Source.Source);
            Assert.Equal(source.SourceEpoch, restoreResult.Source.SourceEpoch);
            Assert.Equal(1, restoreResult.Source.LastSequence);
            Assert.Equal(Lsn(21), restoreResult.Source.LastCommitPosition);
            Assert.Equal(1, restoreResult.Transactions);
            Assert.Equal(2, restoreResult.ConsumerGroups);
            Assert.Equal(1, restoreResult.SnapshotRuns);
            Assert.Equal(1, restoreResult.DeadLetters);
            Assert.Equal(backup.Length, restoreResult.BytesRead);

            var restoredActiveGroup = await restoredRelay.CreateConsumerGroupAsync(
                restoreResult.Source,
                activeGroup.Name);
            var restoredLease = Assert.IsType<ChangeRelayGroupLease>(
                await restoredRelay.AcquireConsumerGroupAsync(
                    restoredActiveGroup,
                    "restored-worker",
                    TimeSpan.FromMinutes(1)));
            Assert.True(restoredLease.FencingToken > originalLease.FencingToken);
            var restoredBatch = await restoredRelay.ReadConsumerGroupAsync(
                restoredLease,
                10,
                1024 * 1024);
            Assert.Equal(1U, Assert.Single(restoredBatch.Records).Transaction.TransactionId);

            var restoredRemovedGroup = await restoredRelay.CreateConsumerGroupAsync(
                restoreResult.Source,
                removedGroup.Name);
            Assert.False(restoredRemovedGroup.IsActive);
            Assert.NotNull(restoredRemovedGroup.RemovedAt);
            await Assert.ThrowsAsync<ChangeRelayConsumerGroupException>(
                () => restoredRelay.AcquireConsumerGroupAsync(
                    restoredRemovedGroup,
                    "invalid-worker",
                    TimeSpan.FromMinutes(1)).AsTask());
            Assert.Equal(
                (SnapshotRuns: 1L, DeadLetters: 1L, Watermarks: 1L),
                await ReadBackupStateCountsAsync(dataSource, restoredSchema));

            backup.Position = 0;
            await Assert.ThrowsAsync<ChangeRelayBackupException>(
                () => restoredRelay.RestoreAsync(
                    backup,
                    source.Source.Fingerprint).AsTask());
        }
        finally
        {
            await DropSchemaAsync(dataSource, sourceSchema);
            await DropSchemaAsync(dataSource, restoredSchema);
        }
    }

    private static PgOutputChangeStream CreateStream(
        ChangeSourceIdentity source,
        IChangeDeliveryObserver? observer = null) =>
        new(
            Messages(
                Envelope(new BlueTuskPgOutputRelation(
                    null,
                    7,
                    "public",
                    "orders",
                    'd',
                    [new BlueTuskPgOutputRelationColumn(
                        BlueTuskPgOutputRelationColumnOptions.Key,
                        "id",
                        23,
                        -1)])),
                Envelope(new BlueTuskPgOutputBegin(Lsn(10), Timestamp, 1)),
                Envelope(new BlueTuskPgOutputInsert(
                    null,
                    7,
                    new BlueTuskPgOutputTuple(
                        [new BlueTuskPgOutputTupleValue(
                            BlueTuskPgOutputTupleValueKind.Text,
                            "1"u8.ToArray())]))),
                Envelope(new BlueTuskPgOutputCommit(Lsn(20), Lsn(21), Timestamp))),
            source,
            observer: observer);

    private static PostgreSqlRelayChangeStream CreateRelayStream(
        PostgreSqlDurableChangeRelay relay,
        ChangeRelaySourceRegistration source,
        string consumerGroup,
        string ownerId) =>
        new(
            relay,
            source,
            new PostgreSqlRelayChangeStreamOptions
            {
                ConsumerGroup = consumerGroup,
                OwnerId = ownerId,
                EmptyReadDelay = TimeSpan.FromMilliseconds(10),
                LeaseDuration = TimeSpan.FromMilliseconds(500),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(100),
                MaxTransactionsPerRead = 2,
                MaxBytesPerRead = 1024 * 1024,
            });

    private static ChangeSourceIdentity SourceIdentity() =>
        new("739463", "app", "orders_slot", "public:orders");

    private static BlueTuskPgOutputEnvelope Envelope(BlueTuskPgOutputMessage message) =>
        new(
            new BlueTuskXLogData(
                Lsn(1),
                Lsn(500),
                Timestamp,
                ReadOnlyMemory<byte>.Empty),
            message);

    private static BlueTuskLogSequenceNumber Lsn(ulong value) => new(value);

    private static async IAsyncEnumerable<BlueTuskPgOutputEnvelope> Messages(
        IEnumerable<BlueTuskPgOutputEnvelope> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return message;
        }
    }

    private static IAsyncEnumerable<BlueTuskPgOutputEnvelope> Messages(
        params BlueTuskPgOutputEnvelope[] messages) =>
        Messages((IEnumerable<BlueTuskPgOutputEnvelope>)messages);

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "BLUETUSK_TEST_CONNECTION_STRING");
        return string.IsNullOrWhiteSpace(connectionString)
            ? throw SkipException.ForSkip(
                "BLUETUSK_TEST_CONNECTION_STRING is not configured.")
            : connectionString;
    }

    private static PostgreSqlDurableChangeRelay CreateProtectedRelay(
        BlueTuskDataSource dataSource,
        string schema,
        IChangeRelayEnvelopeProtectionProvider protection) =>
        new(
            new PostgreSqlStreamsStorageOptions
            {
                ControlDataSource = dataSource,
                ControlSchema = schema,
                MaxRelayStorageBytes = 1024 * 1024,
                EnvelopeProtection = protection,
            });

    private static async Task SeedBackupStateAsync(
        BlueTuskDataSource dataSource,
        string schema,
        ChangeRelaySourceRegistration source)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             INSERT INTO "{schema}".snapshot_runs (
                 source_fingerprint, source_epoch, snapshot_epoch, state, progress)
             VALUES ('{source.Source.Fingerprint}', {source.SourceEpoch}, 'snapshot-1', 'complete', '\x0102');
             INSERT INTO "{schema}".dead_letters (
                 source_fingerprint, source_epoch, consumer_group, sequence, reason, payload)
             VALUES ('{source.Source.Fingerprint}', {source.SourceEpoch}, 'removed-consumer', 1, 'test', '\x0304');
             INSERT INTO "{schema}".retention_watermarks (
                 source_fingerprint, source_epoch, retained_after_sequence)
             VALUES ('{source.Source.Fingerprint}', {source.SourceEpoch}, 0);
             """;
        _ = await command.ExecuteNonQueryAsync();
    }

    private static byte[] CorruptSecondBackupFrame(byte[] backup)
    {
        var firstPayloadLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
            backup.AsSpan(1, sizeof(int)));
        var secondFrameOffset = 1 + sizeof(int) + firstPayloadLength + 32;
        var secondPayloadLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
            backup.AsSpan(secondFrameOffset + 1, sizeof(int)));
        Assert.True(secondPayloadLength > 0);
        backup[secondFrameOffset + 1 + sizeof(int)] ^= 0x01;
        return backup;
    }

    private static async Task<(long SnapshotRuns, long DeadLetters, long Watermarks)>
        ReadBackupStateCountsAsync(BlueTuskDataSource dataSource, string schema)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT
                 (SELECT COUNT(*) FROM "{schema}".snapshot_runs),
                 (SELECT COUNT(*) FROM "{schema}".dead_letters),
                 (SELECT COUNT(*) FROM "{schema}".retention_watermarks)
             """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static async Task DropSchemaAsync(BlueTuskDataSource dataSource, string schema)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteControlAsync(BlueTuskDataSource dataSource, string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<(string ProtectionId, byte[] Payload)> ReadProtectedEnvelopeAsync(
        BlueTuskDataSource dataSource,
        string schema)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT protection_id, envelope FROM \"{schema}\".relay_transactions";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetFieldValue<byte[]>(1));
    }

    private sealed class XorEnvelopeProtectionProvider : IChangeRelayEnvelopeProtectionProvider
    {
        public string CurrentProtectorId => "test-xor-v1";

        public byte[] Protect(ReadOnlySpan<byte> plaintext) => Xor(plaintext);

        public byte[] Unprotect(string protectorId, ReadOnlySpan<byte> protectedData)
        {
            Assert.Equal(CurrentProtectorId, protectorId);
            return Xor(protectedData);
        }

        private static byte[] Xor(ReadOnlySpan<byte> data)
        {
            var transformed = data.ToArray();
            for (var index = 0; index < transformed.Length; index++)
            {
                transformed[index] ^= 0xA5;
            }

            return transformed;
        }
    }

    private sealed class RecordingFeedbackSender : IReplicationFeedbackSender
    {
        public int AttemptCount { get; private set; }

        public int FailuresRemaining { get; set; }

        public List<BlueTuskLogSequenceNumber> Positions { get; } = [];

        public ValueTask SendFeedbackAsync(
            BlueTuskLogSequenceNumber position,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AttemptCount++;
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("Injected feedback failure.");
            }

            Positions.Add(position);
            return ValueTask.CompletedTask;
        }
    }
}
