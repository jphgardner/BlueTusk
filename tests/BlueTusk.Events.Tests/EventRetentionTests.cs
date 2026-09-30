using System.Data.Common;
using System.Globalization;
using System.Text;
using BlueTusk.Replication;

namespace BlueTusk.Events.Tests;

public sealed class EventRetentionTests
{
    [Fact]
    public async Task LocalRetentionRequiresBothExplicitOptionsAndStreamBoundCertification()
    {
        await using var db = await EventDatabase.CreateAsync();
        var stream = new EventStreamKey("tenant", "orders");
        await db.AppendAsync(stream, [Write(1)]);
        _ = await db.Store.ArchiveNextAsync(stream, new TestArchive());
        Assert.Throws<ArgumentException>(() => new EventLocalRetentionCertification(stream,
            "operator", "BT-123", confirmedNoExternalReaders: false));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Store.AdvanceLocalRetentionAsync(
            stream, 1, 0, Certificate(stream)));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Store.PruneRetainedAsync(stream));

        var optedIn = new PostgreSqlEventStore(db.DataSource, new()
        {
            Schema = db.Schema,
            EnableLocalOnlyRetention = true
        });
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await optedIn.AdvanceLocalRetentionAsync(
            stream, 1, 0, Certificate(new EventStreamKey("other", "orders"))));
        Assert.Equal(0, (await optedIn.ReadRetentionStatusAsync(stream)).RetainedThrough);
        var assessment = await db.Store.AssessLocalRetentionAsync(stream, 1, 0);
        Assert.Equal(EventRetentionBlocker.LocalRetentionDisabled, assessment.Blockers);
    }

    [Fact]
    public async Task AssessmentShowsArchiveReplayAndFloorBlockersWithoutAdvancingState()
    {
        await using var db = await EventDatabase.CreateAsync(new() { EnableLocalOnlyRetention = true });
        var stream = new EventStreamKey("tenant", "orders");
        var missing = await db.Store.AssessLocalRetentionAsync(stream, 1, 0);
        Assert.True(missing.Blockers.HasFlag(EventRetentionBlocker.StreamMissing));
        Assert.True(missing.Blockers.HasFlag(EventRetentionBlocker.ArchiveIncomplete));

        await db.AppendAsync(stream, [Write(1), Write(2)]);
        var lease = Assert.IsType<EventReplayLease>(await db.Store.AcquireReplayAsync("slow", stream,
            "owner", TimeSpan.FromMinutes(1)));
        var beforeArchive = await db.Store.AssessLocalRetentionAsync(stream, 2, 0);
        Assert.True(beforeArchive.Blockers.HasFlag(EventRetentionBlocker.ArchiveIncomplete));
        Assert.True(beforeArchive.Blockers.HasFlag(EventRetentionBlocker.ReplayCheckpointBehind));
        Assert.Equal(0, beforeArchive.Status.RetainedThrough);

        _ = await db.Store.ArchiveNextAsync(stream, new TestArchive());
        Assert.Equal(2, (await db.Store.ReplayAsync(lease, db.HandleAsync)).HandledCount);
        Assert.Equal(EventRetentionBlocker.None, (await db.Store.AssessLocalRetentionAsync(stream, 2, 0)).Blockers);
        var changed = await db.Store.AssessLocalRetentionAsync(stream, 3, 1);
        Assert.True(changed.Blockers.HasFlag(EventRetentionBlocker.FloorChanged));
        Assert.True(changed.Blockers.HasFlag(EventRetentionBlocker.BeyondStreamHead));
        Assert.True(changed.Blockers.HasFlag(EventRetentionBlocker.ArchiveIncomplete));
        Assert.Equal(0, (await db.Store.ReadRetentionStatusAsync(stream)).RetainedThrough);
    }

    [Fact]
    public async Task AssessmentFailsClosedOnUnsupportedEventsSchemaVersion()
    {
        await using var db = await EventDatabase.CreateAsync(new() { EnableLocalOnlyRetention = true });
        var stream = new EventStreamKey("tenant", "orders");
        await db.AppendAsync(stream, [Write(1)]);
        await using (var connection = await db.DataSource.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"UPDATE \"{db.Schema}\".schema_version SET version=7";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await db.Store.AssessLocalRetentionAsync(stream, 1, 0));
    }

    [Fact]
    public async Task VersionFiveMigrationInstallsImmutableEndpointBindings()
    {
        await using var db = await EventDatabase.CreateAsync();
        await using (var command = db.DataSource.CreateCommand($"""
            DROP TABLE "{db.Schema}".published_retention_endpoint_bindings;
            UPDATE "{db.Schema}".schema_version SET version=5 WHERE singleton
            """))
        { await command.ExecuteNonQueryAsync(); }

        await db.Store.InitializeAsync();
        await using var verify = db.DataSource.CreateCommand($"""
            SELECT version,
                   to_regclass('"{db.Schema}".published_retention_endpoint_bindings') IS NOT NULL
            FROM "{db.Schema}".schema_version WHERE singleton
            """);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(6, reader.GetInt32(0));
        Assert.True(reader.GetBoolean(1));
    }

    [Fact]
    public async Task IdentityFenceDriftBlocksAssessmentAndBothRetentionMutations()
    {
        await using var db = await EventDatabase.CreateAsync(new() { EnableLocalOnlyRetention = true });
        var stream = new EventStreamKey("tenant", "orders");
        await db.AppendAsync(stream, [Write(1)]);
        _ = await db.Store.ArchiveNextAsync(stream, new TestArchive());
        try
        {
            await SetIdentityFenceAsync(db, enabled: false);
            var assessment = await db.Store.AssessLocalRetentionAsync(stream, 1, 0);
            Assert.True(assessment.Blockers.HasFlag(EventRetentionBlocker.OutboxContractChanged));
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await db.Store.AdvanceLocalRetentionAsync(stream, 1, 0, Certificate(stream)));

            await SetIdentityFenceAsync(db, enabled: true);
            Assert.Equal(EventRetentionBlocker.None, (await db.Store.AssessLocalRetentionAsync(stream, 1, 0)).Blockers);
            _ = await db.Store.AdvanceLocalRetentionAsync(stream, 1, 0, Certificate(stream));

            await SetIdentityFenceAsync(db, enabled: false);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Store.PruneRetainedAsync(stream));
            Assert.Equal(1L, await CountOutboxRowsAsync(db, stream));
        }
        finally
        {
            await SetIdentityFenceAsync(db, enabled: true);
        }
    }

    [Theory]
    [InlineData("explicit")]
    [InlineData("schema")]
    [InlineData("all")]
    public async Task AssessmentAndMutationRefuseEveryPublicationFormAndDetectLaterDrift(string form)
    {
        await using var db = await EventDatabase.CreateAsync(new() { EnableLocalOnlyRetention = true });
        var stream = new EventStreamKey("tenant", "orders");
        await db.AppendAsync(stream, [Write(1)]);
        _ = await db.Store.ArchiveNextAsync(stream, new TestArchive());
        var publication = "events_assess_" + Guid.NewGuid().ToString("N");
        try
        {
            await PublicationFormAsync(db, publication, form, true);
            var blocked = await db.Store.AssessLocalRetentionAsync(stream, 1, 0);
            Assert.True(blocked.Blockers.HasFlag(EventRetentionBlocker.OutboxPublished));
            Assert.Contains(publication, blocked.PublicationNames);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Store.AdvanceLocalRetentionAsync(
                stream, 1, 0, Certificate(stream)));

            await PublicationFormAsync(db, publication, form, false);
            Assert.Equal(EventRetentionBlocker.None, (await db.Store.AssessLocalRetentionAsync(stream, 1, 0)).Blockers);
            _ = await db.Store.AdvanceLocalRetentionAsync(stream, 1, 0, Certificate(stream));
            await PublicationFormAsync(db, publication, form, true);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Store.PruneRetainedAsync(stream));
        }
        finally
        {
            await PublicationFormAsync(db, publication, form, false);
        }
    }

    [Fact]
    public async Task ExplicitPublicationCommitWinsLockBeforePruneAndPruneRefusesDelete()
    {
        await using var db = await EventDatabase.CreateAsync(new() { EnableLocalOnlyRetention = true });
        var stream = new EventStreamKey("tenant", "orders");
        await db.AppendAsync(stream, [Write(1)]);
        _ = await db.Store.ArchiveNextAsync(stream, new TestArchive());
        _ = await db.Store.AdvanceLocalRetentionAsync(stream, 1, 0, Certificate(stream));
        var publication = "events_race_" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var ddlConnection = await db.DataSource.OpenConnectionAsync())
            await using (var ddlTransaction = await ddlConnection.BeginTransactionAsync())
            {
                await using (var command = ddlConnection.CreateCommand())
                {
                    command.Transaction = ddlTransaction;
                    command.CommandText = $"CREATE PUBLICATION \"{publication}\" FOR TABLE \"{db.Schema}\".outbox";
                    await command.ExecuteNonQueryAsync();
                }

                var pruneTask = db.Store.PruneRetainedAsync(stream).AsTask();
                await WaitForOutboxLockWaitAsync(db, pruneTask);
                await ddlTransaction.CommitAsync();
                await Assert.ThrowsAsync<InvalidOperationException>(async () => await pruneTask);
            }

            Assert.Equal(1L, await CountOutboxRowsAsync(db, stream));
        }
        finally
        {
            await PublicationAsync(db, publication, false);
        }
    }

    [Fact]
    public async Task ArchivedLocalPrefixCanBePrunedWithoutLosingIdentityRetriesOrOtherTenant()
    {
        await using var db = await EventDatabase.CreateAsync(new() { EnableLocalOnlyRetention = true });
        var stream = new EventStreamKey("tenant-a", "orders");
        var other = new EventStreamKey("tenant-b", "orders");
        var values = Enumerable.Range(1, 3).Select(Write).ToArray();
        await db.AppendAsync(stream, values);
        await db.AppendAsync(other, [values[0]]);
        var lease = Assert.IsType<EventReplayLease>(await db.Store.AcquireReplayAsync("worker", stream,
            "owner", TimeSpan.FromMinutes(1)));
        Assert.Equal(3, (await db.Store.ReplayAsync(lease, db.HandleAsync)).HandledCount);

        var archive = new TestArchive();
        Assert.Equal(2, (await db.Store.ArchiveNextAsync(stream, archive, maximumEvents: 2)).ArchivedEvents);
        Assert.Equal(1, (await db.Store.ArchiveNextAsync(stream, archive, maximumEvents: 2)).ArchivedEvents);
        Assert.Equal(3, (await db.Store.ReadRetentionStatusAsync(stream)).ArchivedThrough);
        Assert.Equal(2, (await db.Store.AdvanceLocalRetentionAsync(stream, 2, 0, Certificate(stream))).RetainedThrough);
        Assert.True((await db.Store.PruneRetainedAsync(stream, 1)).HasRemainingEvents);
        var last = await db.Store.PruneRetainedAsync(stream, 1);
        Assert.Equal(1, last.DeletedEvents);
        Assert.False(last.HasRemainingEvents);

        var unavailable = await Assert.ThrowsAsync<EventHistoryUnavailableException>(async () => await db.Store.ReadAsync(stream));
        Assert.Equal(2, unavailable.RetainedThrough);
        Assert.Equal([3L], (await db.Store.ReadAsync(stream, afterSequence: 2)).Select(static value => value.Sequence));
        Assert.Equal([1L], (await db.Store.ReadAsync(other)).Select(static value => value.Sequence));
        var retry = Assert.Single(await db.AppendAsync(stream, [values[0]]));
        Assert.True(retry.WasAlreadyStored);
        Assert.Equal(1, retry.Sequence);
        await Assert.ThrowsAsync<EventIdentityConflictException>(async () => await db.AppendAsync(stream,
            [new EventWrite(values[0].EventId, values[0].EventType, values[0].Version,
                values[0].OccurredAt, "changed"u8)]));
        await Assert.ThrowsAsync<EventIdentityConflictException>(async () => await db.AppendAsync(
            new EventStreamKey("tenant-a", "other"), [values[0]]));
        await Assert.ThrowsAnyAsync<DbException>(async () =>
        {
            await using var connection = await db.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO "{db.Schema}".outbox
                    (tenant_id,stream_id,sequence,event_id,event_type,version,occurred_at,payload)
                VALUES(@tenant,@stream,4,@event,@type,1,@occurred,@payload)
                """;
            Add(command, "tenant", stream.TenantId);
            Add(command, "stream", stream.StreamId);
            Add(command, "event", values[0].EventId);
            Add(command, "type", values[0].EventType);
            Add(command, "occurred", values[0].OccurredAt.UtcDateTime);
            Add(command, "payload", values[0].Payload.ToArray());
            await command.ExecuteNonQueryAsync();
        });
        await Assert.ThrowsAsync<EventHistoryUnavailableException>(async () => await db.Store.AcquireReplayAsync(
            "new-consumer", stream, "owner", TimeSpan.FromMinutes(1)));
        Assert.Equal(0, (await db.Store.ReplayAsync(lease, db.HandleAsync)).HandledCount);
    }

    [Fact]
    public async Task LaggingReplayBlocksFloorUntilItsCheckpointCommits()
    {
        await using var db = await EventDatabase.CreateAsync(new() { EnableLocalOnlyRetention = true });
        var stream = new EventStreamKey("tenant", "orders");
        await db.AppendAsync(stream, [Write(1), Write(2)]);
        var lease = Assert.IsType<EventReplayLease>(await db.Store.AcquireReplayAsync("slow", stream,
            "owner", TimeSpan.FromMinutes(1)));
        _ = await db.Store.ArchiveNextAsync(stream, new TestArchive());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Store.AdvanceLocalRetentionAsync(
            stream, 2, 0, Certificate(stream)));
        Assert.Equal(0, (await db.Store.ReadRetentionStatusAsync(stream)).RetainedThrough);
        Assert.Equal(2, (await db.Store.ReplayAsync(lease, db.HandleAsync)).HandledCount);
        Assert.Equal(2, (await db.Store.AdvanceLocalRetentionAsync(stream, 2, 0,
            Certificate(stream))).RetainedThrough);
    }

    [Fact]
    public async Task FailedArchiveReadbackLeavesNoProofAndCanRetry()
    {
        await using var db = await EventDatabase.CreateAsync(new() { EnableLocalOnlyRetention = true });
        var stream = new EventStreamKey("tenant", "orders");
        await db.AppendAsync(stream, [Write(1)]);
        var archive = new TestArchive { CorruptReadback = true };
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Store.ArchiveNextAsync(stream, archive));
        Assert.Equal(0, (await db.Store.ReadRetentionStatusAsync(stream)).ArchivedThrough);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Store.AdvanceLocalRetentionAsync(
            stream, 1, 0, Certificate(stream)));
        archive.CorruptReadback = false;
        Assert.Equal(1, (await db.Store.ArchiveNextAsync(stream, archive)).ArchivedThrough);
    }

    [Fact]
    public async Task PublishedOutboxRefusesFloorAndPruning()
    {
        await using var db = await EventDatabase.CreateAsync(new() { EnableLocalOnlyRetention = true });
        var stream = new EventStreamKey("tenant", "orders");
        await db.AppendAsync(stream, [Write(1)]);
        _ = await db.Store.ArchiveNextAsync(stream, new TestArchive());
        var publication = "events_retention_" + Guid.NewGuid().ToString("N");
        try
        {
            await PublicationAsync(db, publication, true);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Store.AdvanceLocalRetentionAsync(
                stream, 1, 0, Certificate(stream)));
            await PublicationAsync(db, publication, false);
            _ = await db.Store.AdvanceLocalRetentionAsync(stream, 1, 0, Certificate(stream));
            await PublicationAsync(db, publication, true);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Store.PruneRetainedAsync(stream));
            Assert.Empty(await db.Store.ReadAsync(stream, afterSequence: 1));
        }
        finally
        {
            await PublicationAsync(db, publication, false);
        }
    }

    [Fact]
    public async Task PublishedIntentRequiresCompletePublicationCoverageAndNeverEnablesDeletion()
    {
        await using var db = await EventDatabase.CreateAsync(new() { EnableLocalOnlyRetention = true });
        var stream = new EventStreamKey("tenant", "orders");
        var archive = new TestArchive();
        await db.AppendAsync(stream, [Write(1)]);
        _ = await db.Store.ArchiveNextAsync(stream, archive);
        var publication = "events_intent_" + Guid.NewGuid().ToString("N");
        var secondPublication = "events_extra_" + Guid.NewGuid().ToString("N");
        var slot = "events_intent_" + Guid.NewGuid().ToString("N");
        var slotCreated = false;
        try
        {
            await PublicationAsync(db, publication, true);
            await using var replication = await BlueTuskLogicalReplicationConnection.OpenAsync(
                db.DataSource.CreateDedicatedSessionOptions());
            var system = await replication.IdentifySystemAsync();
            var source = new EventPublishedSourceIdentity(system.SystemIdentifier, system.DatabaseName!,
                system.Timeline, slot, publication);
            await replication.CreateReplicationSlotAsync(slot);
            slotCreated = true;

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await db.Store.PublishRetentionIntentAsync(stream, archive, source, 1));
            await using (var connection = await db.DataSource.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"ALTER PUBLICATION \"{publication}\" ADD TABLE \"{db.Schema}\".published_retention_intents";
                await command.ExecuteNonQueryAsync();
            }
            var incarnation = Guid.NewGuid();
            var registration = await db.Store.RegisterPublishedRetentionConsumerAsync(stream,
                "retention-consumer", incarnation, source);
            Assert.Equal(1, registration.MembershipRevision);
            Assert.Equal(registration, await db.Store.RegisterPublishedRetentionConsumerAsync(stream,
                "retention-consumer", incarnation, source));
            await db.Store.RegisterPublishedRetentionTargetAsync(registration);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await db.Store.RegisterPublishedRetentionTargetAsync(registration with
                { SourcePublicationOid = registration.SourcePublicationOid + 1 }));

            await PublicationAsync(db, secondPublication, true);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await db.Store.PublishRetentionIntentAsync(stream, archive, source, 1));
            await PublicationAsync(db, secondPublication, false);

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await db.Store.PublishRetentionIntentAsync(stream, archive, source with { Timeline = source.Timeline + 1 }, 1));
            var intent = await db.Store.PublishRetentionIntentAsync(stream, archive, source, 1);
            Assert.Equal(1, intent.ThroughSequence);
            Assert.Equal(registration.MembershipRevision, intent.MembershipRevision);
            Assert.Equal(64, intent.ArchiveManifestSha256.Length);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await db.Store.ObservePublishedRetentionIntentAsync(intent.Epoch, []));
            var remote = new EventPublishedRetentionRemoteTarget("retention-consumer", incarnation,
                "events-target", db.DataSource, db.Schema, db.Schema,
                EventPublishedRetentionRemoteKind.Events);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await db.Store.ObservePublishedRetentionIntentAsync(intent.Epoch, [remote]));
            await using (var observationCheck = db.DataSource.CreateCommand($"""
                SELECT count(*) FROM "{db.Schema}".published_retention_observations
                """))
            {
                Assert.Equal(0L, Convert.ToInt64(await observationCheck.ExecuteScalarAsync(),
                    CultureInfo.InvariantCulture));
            }
            await using (var connection = await db.DataSource.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"UPDATE \"{db.Schema}\".published_retention_intents SET through_sequence=2 WHERE retention_epoch=@epoch";
                Add(command, "epoch", intent.Epoch);
                await Assert.ThrowsAnyAsync<DbException>(() => command.ExecuteNonQueryAsync());
            }
            Assert.Equal(0, (await db.Store.ReadRetentionStatusAsync(stream)).RetainedThrough);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await db.Store.AdvanceLocalRetentionAsync(stream, 1, 0, Certificate(stream)));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Store.PruneRetainedAsync(stream));
            Assert.Equal(1L, await CountOutboxRowsAsync(db, stream));

            var replacement = await db.Store.RegisterPublishedRetentionConsumerAsync(stream,
                "retention-consumer", Guid.NewGuid(), source);
            Assert.Equal(2, replacement.MembershipRevision);
            await db.Store.RegisterPublishedRetentionTargetAsync(replacement);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await db.Store.ObservePublishedRetentionIntentAsync(intent.Epoch,
                    [remote, new EventPublishedRetentionRemoteTarget("retention-consumer",
                        replacement.TargetIncarnation, "replacement-target", db.DataSource, db.Schema,
                        db.Schema, EventPublishedRetentionRemoteKind.Events)]));
            await db.AppendAsync(stream, [Write(2)]);
            _ = await db.Store.ArchiveNextAsync(stream, archive);
            archive.CorruptReadback = true;
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await db.Store.PublishRetentionIntentAsync(stream, archive, source, 2, expectedPreviousThrough: 1));
            Assert.Equal(1L, await CountRetentionIntentsAsync(db, stream));
            archive.CorruptReadback = false;
            var nextIntent = await db.Store.PublishRetentionIntentAsync(stream, archive, source, 2,
                expectedPreviousThrough: 1);
            Assert.Equal(replacement.MembershipRevision, nextIntent.MembershipRevision);
            Assert.Equal(2L, await CountRetentionIntentsAsync(db, stream));
        }
        finally
        {
            if (slotCreated)
            {
                await using var cleanup = await BlueTuskLogicalReplicationConnection.OpenAsync(
                    db.DataSource.CreateDedicatedSessionOptions());
                await cleanup.DropReplicationSlotAsync(slot, wait: true);
            }
            await PublicationAsync(db, secondPublication, false);
            await PublicationAsync(db, publication, false);
        }
    }

    [Fact]
    public async Task DeploymentMigrationArchivesLegacyIdentityInBoundedBatchBeforePruning()
    {
        await using var db = await EventDatabase.CreateAsync(new() { EnableLocalOnlyRetention = true });
        var stream = new EventStreamKey("tenant", "legacy");
        var value = Write(7);
        await db.AppendAsync(stream, [value]);
        await using (var connection = await db.DataSource.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"DELETE FROM \"{db.Schema}\".event_identities; UPDATE \"{db.Schema}\".schema_version SET version=1";
            await command.ExecuteNonQueryAsync();
        }

        await db.Store.InitializeAsync();
        _ = await db.Store.ArchiveNextAsync(stream, new TestArchive());
        _ = await db.Store.AdvanceLocalRetentionAsync(stream, 1, 0, Certificate(stream));
        Assert.Equal(1, (await db.Store.PruneRetainedAsync(stream)).DeletedEvents);
        Assert.True(Assert.Single(await db.AppendAsync(stream, [value])).WasAlreadyStored);
    }

    private static async Task PublicationAsync(EventDatabase db, string publication, bool create)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = create
            ? $"CREATE PUBLICATION \"{publication}\" FOR TABLE \"{db.Schema}\".outbox"
            : $"DROP PUBLICATION IF EXISTS \"{publication}\"";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task PublicationFormAsync(EventDatabase db, string publication, string form, bool create)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = !create
            ? $"DROP PUBLICATION IF EXISTS \"{publication}\""
            : form switch
            {
                "explicit" => $"CREATE PUBLICATION \"{publication}\" FOR TABLE \"{db.Schema}\".outbox",
                "schema" => $"CREATE PUBLICATION \"{publication}\" FOR TABLES IN SCHEMA \"{db.Schema}\"",
                "all" => $"CREATE PUBLICATION \"{publication}\" FOR ALL TABLES",
                _ => throw new ArgumentOutOfRangeException(nameof(form))
            };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetIdentityFenceAsync(EventDatabase db, bool enabled)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE \"{db.Schema}\".outbox {(enabled ? "ENABLE" : "DISABLE")} TRIGGER outbox_identity_insert";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitForOutboxLockWaitAsync(EventDatabase db, Task pruneTask)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_locks
                WHERE relation=to_regclass(@relation) AND mode='ShareUpdateExclusiveLock' AND NOT granted)
            """;
        Add(command, "relation", $"\"{db.Schema}\".outbox");
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (pruneTask.IsCompleted)
            {
                throw new InvalidOperationException("Prune completed before waiting for the publication DDL lock.");
            }

            if ((bool)(await command.ExecuteScalarAsync())!)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Prune did not reach the publication DDL lock within five seconds.");
    }

    private static async Task<long> CountOutboxRowsAsync(EventDatabase db, EventStreamKey stream)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM \"{db.Schema}\".outbox WHERE tenant_id=@tenant AND stream_id=@stream";
        Add(command, "tenant", stream.TenantId);
        Add(command, "stream", stream.StreamId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<long> CountRetentionIntentsAsync(EventDatabase db, EventStreamKey stream)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM \"{db.Schema}\".published_retention_intents WHERE tenant_id=@tenant AND stream_id=@stream";
        Add(command, "tenant", stream.TenantId);
        Add(command, "stream", stream.StreamId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
    private static EventWrite Write(int value) => new(Guid.NewGuid(), "test.event", 1,
        DateTimeOffset.UtcNow, Encoding.UTF8.GetBytes(value.ToString(CultureInfo.InvariantCulture)));

    private static EventLocalRetentionCertification Certificate(EventStreamKey stream) =>
        new(stream, "test operator", "BT-LOCAL-RETENTION-TEST", confirmedNoExternalReaders: true);

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed class TestArchive : IEventArchiveStore
    {
        private readonly Dictionary<string, IReadOnlyList<StoredEvent>> _objects = new(StringComparer.Ordinal);
        public bool CorruptReadback { get; set; }

        public ValueTask<string> WriteAsync(EventStreamKey stream, IReadOnlyList<StoredEvent> events,
            CancellationToken cancellationToken = default)
        {
            var id = Guid.NewGuid().ToString("N");
            _objects.Add(id, events.ToArray());
            return ValueTask.FromResult(id);
        }

        public ValueTask<IReadOnlyList<StoredEvent>> ReadAsync(string archiveId, CancellationToken cancellationToken = default)
        {
            var stored = _objects[archiveId];
            return ValueTask.FromResult(CorruptReadback ? (IReadOnlyList<StoredEvent>)stored.Select(static value =>
                value with { Payload = "corrupt"u8.ToArray() }).ToArray() : stored);
        }
    }
}
