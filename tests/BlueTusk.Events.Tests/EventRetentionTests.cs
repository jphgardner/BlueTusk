using System.Data.Common;
using System.Globalization;
using System.Text;

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
