using System.Buffers.Binary;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BlueTusk.Events;

/// <summary>An application-supplied durable, immutable archive. A successful write must be readable by its stable ID.</summary>
public interface IEventArchiveStore
{
    ValueTask<string> WriteAsync(EventStreamKey stream, IReadOnlyList<StoredEvent> events,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<StoredEvent>> ReadAsync(string archiveId, CancellationToken cancellationToken = default);
}

public sealed record EventArchiveProgress(int ArchivedEvents, long ArchivedThrough, string? ArchiveId);

public sealed record EventRetentionStatus(long StreamHead, long ArchivedThrough, long RetainedThrough,
    bool LocalRetentionEnabled);

public sealed record EventPruneProgress(int DeletedEvents, bool HasRemainingEvents);

/// <summary>
/// Operator certification for one local stream. The caller is responsible for inventorying all
/// application readers; PostgreSQL can detect publications but not unknown direct readers.
/// </summary>
public sealed class EventLocalRetentionCertification
{
    public EventLocalRetentionCertification(EventStreamKey stream, string operatorId, string changeReference,
        bool confirmedNoExternalReaders)
    {
        ArgumentNullException.ThrowIfNull(stream);
        EventValidation.Key(operatorId, nameof(operatorId));
        EventValidation.Key(changeReference, nameof(changeReference));
        if (!confirmedNoExternalReaders)
        {
            throw new ArgumentException("Local retention requires an explicit no-external-readers certification.",
                nameof(confirmedNoExternalReaders));
        }

        Stream = stream;
        OperatorId = operatorId;
        ChangeReference = changeReference;
    }

    public EventStreamKey Stream { get; }
    public string OperatorId { get; }
    public string ChangeReference { get; }
}

public sealed class EventHistoryUnavailableException : InvalidOperationException
{
    public EventHistoryUnavailableException(EventStreamKey stream, long retainedThrough)
        : base($"Event history for tenant '{stream.TenantId}' stream '{stream.StreamId}' is unavailable through sequence {retainedThrough}.")
    {
        Stream = stream;
        RetainedThrough = retainedThrough;
    }

    public EventStreamKey Stream { get; }
    public long RetainedThrough { get; }
}

public sealed partial class PostgreSqlEventStore
{
    /// <summary>Read one stream's durable archive and retention horizons without reading payloads.</summary>
    public async ValueTask<EventRetentionStatus> ReadRetentionStatusAsync(EventStreamKey stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"""
            SELECT last_sequence,archived_through,retained_through,local_retention_enabled
            FROM {_schema}.streams WHERE tenant_id=@tenant AND stream_id=@stream
            """, ("tenant", stream.TenantId), ("stream", stream.StreamId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetBoolean(3))
            : new(0, 0, 0, false);
    }

    /// <summary>
    /// Archive the next bounded contiguous prefix. External storage is written and read back first;
    /// the manifest and identity hashes then commit atomically. An interrupted attempt may leave an
    /// unreferenced external object, but never a false committed archive claim.
    /// </summary>
    public async ValueTask<EventArchiveProgress> ArchiveNextAsync(EventStreamKey stream, IEventArchiveStore archive,
        int maximumEvents = 256, int maximumPayloadBytes = 8_388_608, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(archive);
        ValidateRead(stream, 0, maximumEvents, maximumPayloadBytes);
        var status = await ReadRetentionStatusAsync(stream, cancellationToken).ConfigureAwait(false);
        if (status.ArchivedThrough == status.StreamHead)
        {
            return new EventArchiveProgress(0, status.ArchivedThrough, null);
        }

        var events = await ReadAsync(stream, status.ArchivedThrough, maximumEvents, maximumPayloadBytes, cancellationToken)
            .ConfigureAwait(false);
        ValidateArchiveBatch(stream, events, checked(status.ArchivedThrough + 1));
        var archiveId = await archive.WriteAsync(stream, events, cancellationToken).ConfigureAwait(false);
        ValidateArchiveId(archiveId);
        var restored = await archive.ReadAsync(archiveId, cancellationToken).ConfigureAwait(false);
        if (!SameEvents(events, restored))
        {
            throw new InvalidOperationException("The event archive readback does not match the immutable source batch.");
        }

        var digest = HashBatch(events);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        long current;
        await using (var lockCommand = Command(connection, transaction, $"""
            SELECT archived_through FROM {_schema}.streams
            WHERE tenant_id=@tenant AND stream_id=@stream FOR UPDATE
            """, ("tenant", stream.TenantId), ("stream", stream.StreamId)))
        {
            current = Convert.ToInt64(await lockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        if (current != status.ArchivedThrough)
        {
            // Another archiver won the prefix. Its manifest is authoritative; this object is orphaned.
            return new EventArchiveProgress(0, current, null);
        }

        var source = await ReadAsync(connection, transaction, stream, current, events.Count,
            maximumPayloadBytes, cancellationToken).ConfigureAwait(false);
        if (!SameEvents(events, source))
        {
            throw new InvalidOperationException("The event source changed while its archive was being verified.");
        }

        foreach (var value in events)
        {
            await using var identity = Command(connection, transaction, $"""
                INSERT INTO {_schema}.event_identities AS existing
                    (tenant_id,event_id,stream_id,sequence,event_type,version,occurred_at,payload_length,payload_sha256)
                VALUES(@tenant,@event,@stream,@sequence,@type,@version,@occurred,@length,@hash)
                ON CONFLICT (tenant_id,event_id) DO UPDATE SET payload_sha256=EXCLUDED.payload_sha256
                WHERE existing.stream_id=EXCLUDED.stream_id AND existing.sequence=EXCLUDED.sequence
                    AND existing.event_type=EXCLUDED.event_type AND existing.version=EXCLUDED.version
                    AND existing.occurred_at=EXCLUDED.occurred_at AND existing.payload_length=EXCLUDED.payload_length
                    AND (existing.payload_sha256 IS NULL OR existing.payload_sha256=EXCLUDED.payload_sha256)
                """, ("tenant", stream.TenantId), ("event", value.EventId), ("stream", stream.StreamId),
                ("sequence", value.Sequence), ("type", value.EventType), ("version", value.Version),
                ("occurred", value.OccurredAt.UtcDateTime), ("length", value.Payload.Length),
                ("hash", SHA256.HashData(value.Payload.Span)));
            if (await identity.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("The durable event identity ledger does not match the archived source event.");
            }
        }

        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO {_schema}.archive_segments
                (tenant_id,stream_id,first_sequence,last_sequence,archive_id,batch_sha256,event_count)
            VALUES(@tenant,@stream,@first,@last,@archive,@hash,@count)
            """, cancellationToken, ("tenant", stream.TenantId), ("stream", stream.StreamId),
            ("first", events[0].Sequence), ("last", events[^1].Sequence), ("archive", archiveId),
            ("hash", digest), ("count", events.Count)).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"""
            UPDATE {_schema}.streams SET archived_through=@last
            WHERE tenant_id=@tenant AND stream_id=@stream AND archived_through=@expected
            """, cancellationToken, ("tenant", stream.TenantId), ("stream", stream.StreamId),
            ("last", events[^1].Sequence), ("expected", current)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new EventArchiveProgress(events.Count, events[^1].Sequence, archiveId);
    }

    /// <summary>
    /// Declare a local-only retention floor after archive proof and all registered replay checkpoints.
    /// The operator assertion acknowledges that no unregistered external reader depends on this prefix.
    /// Published outboxes are refused. The floor cannot move backward.
    /// </summary>
    public async ValueTask<EventRetentionStatus> AdvanceLocalRetentionAsync(EventStreamKey stream, long throughSequence,
        long expectedRetainedThrough, EventLocalRetentionCertification certification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(throughSequence);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRetainedThrough);
        ArgumentNullException.ThrowIfNull(certification);
        if (!_options.EnableLocalOnlyRetention || certification.Stream != stream)
        {
            throw new InvalidOperationException("Local retention requires enabled options and certification for the exact stream.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var status = await LockRetentionStatusAsync(connection, transaction, stream, cancellationToken).ConfigureAwait(false);
        await AssertUnpublishedAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (status.RetainedThrough != expectedRetainedThrough || throughSequence <= status.RetainedThrough ||
            throughSequence > status.ArchivedThrough)
        {
            throw new InvalidOperationException("Retention requires the expected prior floor and a complete contiguous archive prefix.");
        }

        await using (var lagging = Command(connection, transaction, $"""
            SELECT EXISTS(SELECT 1 FROM {_schema}.replay
                WHERE tenant_id=@tenant AND stream_id=@stream AND checkpoint < @through)
            """, ("tenant", stream.TenantId), ("stream", stream.StreamId), ("through", throughSequence)))
        {
            if ((bool)(await lagging.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
            {
                throw new InvalidOperationException("A registered replay consumer has not acknowledged the proposed retention floor.");
            }
        }

        await ExecuteAsync(connection, transaction, $"""
            UPDATE {_schema}.streams SET retained_through=@through,local_retention_enabled=true,
                retention_operator=@operator,retention_change_ref=@change,
                retention_enabled_at=COALESCE(retention_enabled_at,clock_timestamp())
            WHERE tenant_id=@tenant AND stream_id=@stream AND retained_through=@expected
            """, cancellationToken, ("through", throughSequence), ("tenant", stream.TenantId),
            ("stream", stream.StreamId), ("expected", expectedRetainedThrough),
            ("operator", certification.OperatorId), ("change", certification.ChangeReference)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return status with { RetainedThrough = throughSequence, LocalRetentionEnabled = true };
    }

    /// <summary>Delete at most maximumEvents archived events beneath an opted-in local-only floor.</summary>
    public async ValueTask<EventPruneProgress> PruneRetainedAsync(EventStreamKey stream, int maximumEvents = 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!_options.EnableLocalOnlyRetention)
        {
            throw new InvalidOperationException("Local retention is disabled in this event store's options.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEvents);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumEvents, 65_536);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var status = await LockRetentionStatusAsync(connection, transaction, stream, cancellationToken).ConfigureAwait(false);
        if (!status.LocalRetentionEnabled || status.RetainedThrough == 0)
        {
            throw new InvalidOperationException("The stream has no enabled local-only retention floor.");
        }

        await AssertUnpublishedAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var delete = Command(connection, transaction, $"""
            WITH victims AS (
                SELECT ctid FROM {_schema}.outbox
                WHERE tenant_id=@tenant AND stream_id=@stream AND sequence<=@through
                ORDER BY sequence LIMIT @limit FOR UPDATE
            )
            DELETE FROM {_schema}.outbox o USING victims v WHERE o.ctid=v.ctid
            """, ("tenant", stream.TenantId), ("stream", stream.StreamId),
            ("through", status.RetainedThrough), ("limit", maximumEvents));
        var deleted = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using var remaining = Command(connection, transaction, $"""
            SELECT EXISTS(SELECT 1 FROM {_schema}.outbox
                WHERE tenant_id=@tenant AND stream_id=@stream AND sequence<=@through)
            """, ("tenant", stream.TenantId), ("stream", stream.StreamId), ("through", status.RetainedThrough));
        var hasRemaining = (bool)(await remaining.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new EventPruneProgress(deleted, hasRemaining);
    }

    private async ValueTask<EventRetentionStatus> LockRetentionStatusAsync(DbConnection connection,
        DbTransaction transaction, EventStreamKey stream, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"""
            SELECT last_sequence,archived_through,retained_through,local_retention_enabled
            FROM {_schema}.streams WHERE tenant_id=@tenant AND stream_id=@stream FOR UPDATE
            """, ("tenant", stream.TenantId), ("stream", stream.StreamId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The event stream does not exist.");
        }

        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetBoolean(3));
    }

    private async ValueTask AssertUnpublishedAsync(DbConnection connection, DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        // This conflicts with explicit-table publication changes while the catalog is checked and a
        // bounded delete commits. FOR ALL TABLES/schema publication DDL also requires an operator
        // freeze; it does not necessarily lock this individual relation.
        await ExecuteAsync(connection, transaction, $"LOCK TABLE {_schema}.outbox IN SHARE UPDATE EXCLUSIVE MODE", cancellationToken)
            .ConfigureAwait(false);
        await using var command = Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_publication_tables
                WHERE schemaname=@schema AND tablename='outbox')
            """, ("schema", _options.Schema));
        if ((bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
        {
            throw new InvalidOperationException("The outbox is published to CDC; local-only retention is refused.");
        }
    }

    private static void ValidateArchiveBatch(EventStreamKey stream, IReadOnlyList<StoredEvent> events, long expectedFirst)
    {
        if (events.Count == 0)
        {
            throw new InvalidOperationException("The event source has a gap before the next archive prefix.");
        }

        for (var i = 0; i < events.Count; i++)
        {
            if (events[i].Stream != stream || events[i].Sequence != checked(expectedFirst + i))
            {
                throw new InvalidOperationException("Only a contiguous tenant-scoped event prefix can be archived.");
            }
        }
    }

    private static bool SameEvents(IReadOnlyList<StoredEvent> expected, IReadOnlyList<StoredEvent>? actual)
    {
        if (actual is null || actual.Count != expected.Count) { return false; }
        for (var i = 0; i < expected.Count; i++)
        {
            var left = expected[i]; var right = actual[i];
            if (left.Stream != right.Stream || left.Sequence != right.Sequence || left.EventId != right.EventId ||
                !string.Equals(left.EventType, right.EventType, StringComparison.Ordinal) || left.Version != right.Version ||
                left.OccurredAt != right.OccurredAt || !left.Payload.Span.SequenceEqual(right.Payload.Span))
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] HashBatch(IReadOnlyList<StoredEvent> events)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> number = stackalloc byte[8];
        foreach (var value in events)
        {
            AppendString(hash, value.Stream.TenantId);
            AppendString(hash, value.Stream.StreamId);
            BinaryPrimitives.WriteInt64LittleEndian(number, value.Sequence); hash.AppendData(number);
            hash.AppendData(value.EventId.ToByteArray());
            AppendString(hash, value.EventType);
            BinaryPrimitives.WriteInt64LittleEndian(number, value.Version); hash.AppendData(number);
            BinaryPrimitives.WriteInt64LittleEndian(number, value.OccurredAt.UtcTicks); hash.AppendData(number);
            BinaryPrimitives.WriteInt64LittleEndian(number, value.Payload.Length); hash.AppendData(number);
            hash.AppendData(value.Payload.Span);
        }

        return hash.GetHashAndReset();
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> number = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(number, bytes.Length);
        hash.AppendData(number);
        hash.AppendData(bytes);
    }

    private static void ValidateArchiveId(string? archiveId)
    {
        if (string.IsNullOrWhiteSpace(archiveId) || archiveId.Length > 2048 || archiveId.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The archive did not return a bounded stable object identity.");
        }
    }
}
