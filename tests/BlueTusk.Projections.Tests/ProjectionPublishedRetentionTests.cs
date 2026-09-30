using System.Globalization;
using System.Text;
using BlueTusk.Events;
using BlueTusk.Replication;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;

namespace BlueTusk.Projections.Tests;

public sealed class ProjectionPublishedRetentionTests
{
    [Fact]
    public async Task MarkerAckAndCheckpointCommitTogetherAndExactReplayIsIdempotent()
    {
        await using var setup = await Setup.CreateAsync();
        var (lease, definition, options) = await setup.ReadyVersionAsync(1, 100);
        await setup.Database.Store.PromoteAsync(lease, new BlueTuskLogSequenceNumber(100), null);
        var consumer = StreamsProjectionConsumer.CreateProtected(setup.Database.Store, lease, definition, options);
        var epoch = Guid.NewGuid();

        // A target without exact stream membership must roll back even the already staged checkpoint.
        await using (var missing = setup.Marker(200, epoch, stream: "unregistered"))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await consumer.ConsumeTransactionAsync(missing));
            Assert.Equal(ChangeDeliveryState.Active, missing.State);
        }
        Assert.Equal(100UL, (await setup.Database.Store.ReadStateAsync(lease.Identity)).Checkpoint.Value);
        Assert.Equal(0L, await setup.AckCountAsync());

        await using (var marker = setup.Marker(200, epoch))
        {
            await consumer.ConsumeTransactionAsync(marker);
            Assert.Equal(ChangeDeliveryState.Acknowledged, marker.State);
        }
        Assert.Equal(200UL, (await setup.Database.Store.ReadStateAsync(lease.Identity)).Checkpoint.Value);
        Assert.Equal(1L, await setup.AckCountAsync());
        Assert.Equal(("active", (int?)1, (Guid?)null), await setup.AckRoleAsync(epoch));

        await using (var replay = setup.Marker(200, epoch))
        {
            await consumer.ConsumeTransactionAsync(replay);
            Assert.Equal(ChangeDeliveryState.Acknowledged, replay.State);
        }
        Assert.Equal(1L, await setup.AckCountAsync());
        await using (var changed = setup.Marker(200, epoch, through: 3))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await consumer.ConsumeTransactionAsync(changed));
        }
        Assert.Equal(1L, await setup.AckCountAsync());
    }

    [Fact]
    public async Task CandidateAckReplaysAfterPromotionWithoutBecomingActive()
    {
        await using var setup = await Setup.CreateAsync();
        var (lease, definition, options) = await setup.ReadyVersionAsync(1, 100);
        var consumer = StreamsProjectionConsumer.CreateProtected(setup.Database.Store, lease, definition, options);
        var epoch = Guid.NewGuid();
        await using (var marker = setup.Marker(200, epoch)) { await consumer.ConsumeTransactionAsync(marker); }
        Assert.Equal(("candidate", (int?)null, (Guid?)null), await setup.AckRoleAsync(epoch));
        await setup.Database.Store.PromoteAsync(lease, new BlueTuskLogSequenceNumber(200), null);
        await using var replay = setup.Marker(200, epoch);
        await consumer.ConsumeTransactionAsync(replay);
        Assert.Equal(ChangeDeliveryState.Acknowledged, replay.State);
        Assert.Equal(1L, await setup.AckCountAsync());
        Assert.Equal(("candidate", (int?)null, (Guid?)null), await setup.AckRoleAsync(epoch));
    }

    [Fact]
    public async Task FreshSnapshotPastMarkerCannotManufactureAnAck()
    {
        await using var setup = await Setup.CreateAsync();
        var (lease, definition, options) = await setup.ReadyVersionAsync(1, 300);
        var consumer = StreamsProjectionConsumer.CreateProtected(setup.Database.Store, lease, definition, options);
        await using var oldMarker = setup.Marker(200, Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await consumer.ConsumeTransactionAsync(oldMarker));
        Assert.Equal(300UL, (await setup.Database.Store.ReadStateAsync(lease.Identity)).Checkpoint.Value);
        Assert.Equal(0L, await setup.AckCountAsync());
    }

    [Fact]
    public async Task MarkerWithAnotherRawChangeOrWithoutVerifiedPublicationCannotAck()
    {
        await using var setup = await Setup.CreateAsync();
        var (lease, definition, options) = await setup.ReadyVersionAsync(1, 100);
        var consumer = StreamsProjectionConsumer.CreateProtected(setup.Database.Store, lease, definition, options);
        await using var mixed = setup.Marker(200, Guid.NewGuid(), unrelatedChange: true);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await consumer.ConsumeTransactionAsync(mixed));
        await using var sourceDelivery = setup.Marker(200, Guid.NewGuid());
        await using var wrongPublication = new ChangeTransactionDelivery(sourceDelivery.Transaction, new NoOpObserver());
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await consumer.ConsumeTransactionAsync(wrongPublication));
        await using var wrongTimeline = setup.Marker(200, Guid.NewGuid(),
            timelineOverride: checked(setup.SourceTimeline + 1));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await consumer.ConsumeTransactionAsync(wrongTimeline));
        await using var wrongDatabaseOid = setup.Marker(200, Guid.NewGuid(),
            databaseOidOverride: checked(setup.Lineage.DatabaseOid + 1));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await consumer.ConsumeTransactionAsync(wrongDatabaseOid));
        await using var wrongPublicationOid = setup.Marker(200, Guid.NewGuid(),
            publicationOidOverride: checked(setup.PublicationOid + 1));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await consumer.ConsumeTransactionAsync(wrongPublicationOid));
        await using var missingOids = setup.Marker(200, Guid.NewGuid(), verifiedOids: false);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await consumer.ConsumeTransactionAsync(missingOids));
        Assert.Equal(100UL, (await setup.Database.Store.ReadStateAsync(lease.Identity)).Checkpoint.Value);
        Assert.Equal(0L, await setup.AckCountAsync());
    }

    [Fact]
    public async Task UnrelatedSchemaWithSameTableNameIsAnOrdinaryChange()
    {
        await using var setup = await Setup.CreateAsync();
        var (lease, definition, options) = await setup.ReadyVersionAsync(1, 100);
        var consumer = StreamsProjectionConsumer.CreateProtected(setup.Database.Store, lease, definition, options);
        await using var delivery = setup.UnrelatedNamedRelation(200);

        await consumer.ConsumeTransactionAsync(delivery);

        Assert.Equal(ChangeDeliveryState.Acknowledged, delivery.State);
        Assert.Equal(200UL, (await setup.Database.Store.ReadStateAsync(lease.Identity)).Checkpoint.Value);
        Assert.Equal(0L, await setup.AckCountAsync());
    }

    [Fact]
    public async Task RecoveryCandidateAckNamesTicketAndFrozenPredecessorCannotAck()
    {
        await using var setup = await Setup.CreateAsync();
        var (active, activeDefinition, activeOptions) = await setup.ReadyVersionAsync(1, 100);
        await setup.Database.Store.PromoteAsync(active, new BlueTuskLogSequenceNumber(100), null);
        var (candidateIdentity, candidateOptions) = await setup.RegisterVersionAsync(2);
        var evidence = await setup.CaptureCutoverAsync();
        var recoveryId = Guid.NewGuid();
        await setup.Database.Store.BeginRecoveryAsync(candidateIdentity, 1, evidence, recoveryId,
            "retention integration test");
        var candidate = Assert.IsType<ProjectionLease>(await setup.Database.Store.AcquireAsync(
            candidateIdentity, "recovery-worker", TimeSpan.FromMinutes(5)));
        var snapshotPosition = checked(evidence.BarrierPosition.Value + 1);
        await setup.CompleteEmptySnapshotAsync(candidate, new NoOpProjection(candidateIdentity), snapshotPosition);
        var markerPosition = checked(snapshotPosition + 1);
        var candidateConsumer = StreamsProjectionConsumer.CreateProtected(setup.Database.Store,
            candidate, new NoOpProjection(candidateIdentity), candidateOptions);
        var epoch = Guid.NewGuid();
        await using (var marker = setup.Marker(markerPosition, epoch))
        { await candidateConsumer.ConsumeTransactionAsync(marker); }
        Assert.Equal(("recovery_candidate", (int?)1, (Guid?)recoveryId), await setup.AckRoleAsync(epoch));

        var former = StreamsProjectionConsumer.CreateProtected(setup.Database.Store, active,
            activeDefinition, activeOptions);
        await using var oldWorker = setup.Marker(200, Guid.NewGuid());
        await Assert.ThrowsAsync<ProjectionFencedException>(async () =>
            await former.ConsumeTransactionAsync(oldWorker));
        Assert.Equal(1L, await setup.AckCountAsync());
    }

    private sealed class Setup : IAsyncDisposable
    {
        private Setup(ProjectionDatabase database, string publication, ChangeSourceIdentity source,
            ProjectionSourceLineage lineage, EventPublishedSourceIdentity eventSource, uint publicationOid)
        {
            Database = database; Publication = publication; Source = source;
            Lineage = lineage; EventSource = eventSource; PublicationOid = publicationOid;
        }

        internal ProjectionDatabase Database { get; }
        private string Publication { get; }
        private ChangeSourceIdentity Source { get; }
        internal ProjectionSourceLineage Lineage { get; }
        private EventPublishedSourceIdentity EventSource { get; }
        internal uint PublicationOid { get; }
        internal uint SourceTimeline => Lineage.Timeline;

        internal static async ValueTask<Setup> CreateAsync()
        {
            var database = await ProjectionDatabase.CreateAsync();
            var publication = "proj_retention_" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var command = database.DataSource.CreateCommand($"""
                    CREATE TABLE "{database.Schema}".published_retention_intents (
                        retention_epoch uuid PRIMARY KEY, tenant_id text NOT NULL, stream_id text NOT NULL,
                        first_sequence bigint NOT NULL, through_sequence bigint NOT NULL,
                        archive_manifest_sha256 bytea NOT NULL, source_system_identifier text NOT NULL,
                        source_database text NOT NULL, source_database_oid oid NOT NULL,
                        source_timeline bigint NOT NULL, source_slot text NOT NULL,
                        source_publication text NOT NULL, source_publication_oid oid NOT NULL,
                        membership_revision bigint NOT NULL);
                    CREATE PUBLICATION "{publication}" FOR TABLE "{database.Schema}".published_retention_intents
                    """))
                { await command.ExecuteNonQueryAsync(); }
                BlueTuskReplicationSystemIdentity system;
                await using (var replication = await BlueTuskLogicalReplicationConnection.OpenAsync(
                    database.DataSource.CreateDedicatedSessionOptions()))
                { system = await replication.IdentifySystemAsync(); }
                var source = new ChangeSourceIdentity(system.SystemIdentifier, system.DatabaseName!,
                    "proj_retention_slot_" + Guid.NewGuid().ToString("N"), publication);
                var lineage = await PostgreSqlProjectionLineage.CaptureAsync(database.DataSource, source, [publication]);
                uint publicationOid;
                await using (var command = database.DataSource.CreateCommand(
                    $"SELECT oid FROM pg_catalog.pg_publication WHERE pubname='{publication}'"))
                { publicationOid = (uint)(await command.ExecuteScalarAsync())!; }
                var eventSource = new EventPublishedSourceIdentity(system.SystemIdentifier,
                    system.DatabaseName!, lineage.Timeline, source.SlotName, publication);
                return new Setup(database, publication, source, lineage, eventSource, publicationOid);
            }
            catch
            {
                try
                {
                    await using var cleanup = database.DataSource.CreateCommand(
                        $"DROP PUBLICATION IF EXISTS \"{publication}\"");
                    await cleanup.ExecuteNonQueryAsync();
                }
                finally { await database.DisposeAsync(); }
                throw;
            }
        }

        internal async ValueTask<(ProjectionLease Lease, NoOpProjection Definition,
            ProjectionPublishedRetentionTargetOptions Options)> ReadyVersionAsync(int version, ulong snapshotPosition)
        {
            var (identity, options) = await RegisterVersionAsync(version);
            var lease = Assert.IsType<ProjectionLease>(await Database.Store.AcquireAsync(identity,
                "worker-" + version, TimeSpan.FromMinutes(5)));
            var definition = new NoOpProjection(identity);
            await CompleteEmptySnapshotAsync(lease, definition, snapshotPosition);
            return (lease, definition, options);
        }

        internal async ValueTask<(ProjectionIdentity Identity, ProjectionPublishedRetentionTargetOptions Options)>
            RegisterVersionAsync(int version)
        {
            var identity = new ProjectionIdentity("retained", version, "retained-v" + version, Source);
            await Database.Store.RegisterWithLineageAsync(identity, Lineage);
            var incarnation = Guid.NewGuid();
            var group = "projection-retained-v" + version;
            await Database.Store.RegisterPublishedRetentionTargetAsync(identity, Lineage,
                new EventPublishedConsumerRegistration(new EventStreamKey("tenant", "orders"), group,
                    incarnation, 1, EventSource, Lineage.DatabaseOid, PublicationOid));
            return (identity, new ProjectionPublishedRetentionTargetOptions(
                incarnation, EventSource, Database.Schema, group));
        }

        internal ValueTask<ProjectionCutoverEvidence> CaptureCutoverAsync() =>
            PostgreSqlProjectionLineage.CaptureForCutoverAsync(Database.DataSource, Source, [Publication]);

        internal async ValueTask CompleteEmptySnapshotAsync(ProjectionLease lease,
            NoOpProjection definition, ulong snapshotPosition)
        {
            var epoch = SnapshotEpoch.Create(Source, new BlueTuskLogSequenceNumber(snapshotPosition));
            await Database.Store.StartSnapshotAsync(lease, new SnapshotStart(epoch, 1));
            await Database.Store.ApplySnapshotAsync(lease, definition,
                new ChangeSnapshotBatch(epoch, Assert.Single(Lineage.Tables), 0, [], true));
            await Database.Store.CompleteSnapshotAsync(lease, new SnapshotComplete(epoch, 0, 1));
        }

        internal ChangeTransactionDelivery Marker(ulong position, Guid epoch, string stream = "orders",
            long through = 2, bool unrelatedChange = false, uint? timelineOverride = null,
            uint? databaseOidOverride = null, uint? publicationOidOverride = null,
            bool verifiedOids = true)
        {
            var lsn = new BlueTuskLogSequenceNumber(position);
            var table = Assert.Single(Lineage.Tables);
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["retention_epoch"] = epoch.ToString("D"),
                ["tenant_id"] = "tenant",
                ["stream_id"] = stream,
                ["first_sequence"] = "1",
                ["through_sequence"] = through.ToString(CultureInfo.InvariantCulture),
                ["archive_manifest_sha256"] = "\\x" + new string('a', 64),
                ["source_system_identifier"] = EventSource.SystemIdentifier,
                ["source_database"] = EventSource.DatabaseName,
                ["source_database_oid"] = Lineage.DatabaseOid.ToString(CultureInfo.InvariantCulture),
                ["source_timeline"] = EventSource.Timeline.ToString(CultureInfo.InvariantCulture),
                ["source_slot"] = EventSource.SlotName,
                ["source_publication"] = EventSource.PublicationName,
                ["source_publication_oid"] = PublicationOid.ToString(CultureInfo.InvariantCulture),
                ["membership_revision"] = "1"
            };
            var row = new ChangeRow(table, table.Columns.Select(column =>
                ChangeColumnValue.FromValue(Encoding.UTF8.GetBytes(values[column.Name]), ChangeValueEncoding.Text)));
            var changes = new List<Change> { new InsertChange(new ChangeId(Source, lsn, 42, 0), row) };
            if (unrelatedChange)
            {
                var other = new ChangeTable(1000, Database.Schema, "other", 'd',
                    [new ChangeColumn(0, "id", 25, -1, true)]);
                changes.Add(new InsertChange(new ChangeId(Source, lsn, 42, 1), new ChangeRow(other,
                    [ChangeColumnValue.FromValue(Encoding.UTF8.GetBytes("x"), ChangeValueEncoding.Text)])));
            }
            return verifiedOids
                ? ChangeDeliveryTestFactory.CreateCommittedWithLineage(Source, 42, lsn,
                    timelineOverride ?? Lineage.Timeline, databaseOidOverride ?? Lineage.DatabaseOid,
                    publicationOidOverride ?? PublicationOid, changes)
                : ChangeDeliveryTestFactory.CreateCommittedWithTimeline(Source, 42, lsn,
                    timelineOverride ?? Lineage.Timeline, changes);
        }

        internal ChangeTransactionDelivery UnrelatedNamedRelation(ulong position)
        {
            var lsn = new BlueTuskLogSequenceNumber(position);
            var table = new ChangeTable(1000, "unrelated", "published_retention_intents", 'd',
                [new ChangeColumn(0, "id", 25, -1, true)]);
            var row = new ChangeRow(table,
                [ChangeColumnValue.FromValue(Encoding.UTF8.GetBytes("x"), ChangeValueEncoding.Text)]);
            return ChangeDeliveryTestFactory.CreateCommittedWithLineage(Source, 42, lsn,
                Lineage.Timeline, Lineage.DatabaseOid, PublicationOid,
                [new InsertChange(new ChangeId(Source, lsn, 42, 0), row)]);
        }

        internal async ValueTask<long> AckCountAsync()
        {
            await using var command = Database.DataSource.CreateCommand(
                $"SELECT count(*) FROM \"{Database.Schema}\".published_retention_acknowledgements");
            return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        internal async ValueTask<(string Role, int? ActiveVersion, Guid? RecoveryId)> AckRoleAsync(Guid epoch)
        {
            await using var connection = await Database.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT role,active_version,recovery_id FROM \"{Database.Schema}\".published_retention_acknowledgements WHERE retention_epoch=@epoch";
            var parameter = command.CreateParameter(); parameter.ParameterName = "epoch"; parameter.Value = epoch;
            command.Parameters.Add(parameter);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2));
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = Database.DataSource.CreateCommand($"DROP PUBLICATION IF EXISTS \"{Publication}\"");
                await command.ExecuteNonQueryAsync();
            }
            finally { await Database.DisposeAsync(); }
        }
    }

    private sealed class NoOpProjection(ProjectionIdentity identity) : IProjectionDefinition
    {
        public ProjectionIdentity Identity { get; } = identity;
        public ValueTask ApplySnapshotAsync(ChangeSnapshotBatch batch, ProjectionWriteContext context,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask ApplyTransactionAsync(ChangeTransaction transaction, ProjectionWriteContext context,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class NoOpObserver : IChangeDeliveryObserver
    {
        public ValueTask AcknowledgeAsync(ChangeTransaction transaction,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask NackAsync(ChangeTransaction transaction, Exception? failure = null,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
