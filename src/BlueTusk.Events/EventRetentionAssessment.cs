using System.Data;
using System.Globalization;

namespace BlueTusk.Events;

/// <summary>Database-observable blockers for a proposed local-only retention floor.</summary>
[Flags]
public enum EventRetentionBlocker
{
    None = 0,
    LocalRetentionDisabled = 1,
    StreamMissing = 2,
    FloorChanged = 4,
    FloorNotForward = 8,
    BeyondStreamHead = 16,
    ArchiveIncomplete = 32,
    ReplayCheckpointBehind = 64,
    OutboxPublished = 128,
    OutboxContractChanged = 256,
}

/// <summary>
/// Point-in-time database assessment. It cannot detect unregistered readers or certify an external
/// archive's continuing durability; mutation repeats its own checks and requires operator certification.
/// </summary>
public sealed class EventRetentionAssessment
{
    internal EventRetentionAssessment(EventStreamKey stream, long requestedThrough, long expectedRetainedThrough,
        EventRetentionStatus status, EventRetentionBlocker blockers, IReadOnlyList<string> publicationNames,
        bool additionalPublicationsOmitted)
    {
        Stream = stream;
        RequestedThrough = requestedThrough;
        ExpectedRetainedThrough = expectedRetainedThrough;
        Status = status;
        Blockers = blockers;
        PublicationNames = publicationNames;
        AdditionalPublicationsOmitted = additionalPublicationsOmitted;
    }

    public EventStreamKey Stream { get; }
    public long RequestedThrough { get; }
    public long ExpectedRetainedThrough { get; }
    public EventRetentionStatus Status { get; }
    public EventRetentionBlocker Blockers { get; }
    public IReadOnlyList<string> PublicationNames { get; }
    public bool AdditionalPublicationsOmitted { get; }
}

public sealed partial class PostgreSqlEventStore
{
    /// <summary>
    /// Inspect stream/replay metadata and return at most 32 publication names plus an overflow marker.
    /// This advisory snapshot never authorizes deletion or replaces the mutation-time checks.
    /// </summary>
    public async ValueTask<EventRetentionAssessment> AssessLocalRetentionAsync(EventStreamKey stream,
        long throughSequence, long expectedRetainedThrough, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(throughSequence);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRetainedThrough);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,
            cancellationToken).ConfigureAwait(false);
        await using (var versionCommand = Command(connection, transaction,
            $"SELECT version FROM {_schema}.schema_version WHERE singleton"))
        {
            var version = await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (version is null or DBNull || Convert.ToInt32(version, CultureInfo.InvariantCulture) != 2)
            {
                throw new InvalidOperationException("The Events retention schema version is not supported.");
            }
        }

        EventRetentionStatus status;
        var streamExists = false;
        await using (var statusCommand = Command(connection, transaction, $"""
            SELECT last_sequence,archived_through,retained_through,local_retention_enabled
            FROM {_schema}.streams WHERE tenant_id=@tenant AND stream_id=@stream
            """, ("tenant", stream.TenantId), ("stream", stream.StreamId)))
        await using (var reader = await statusCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            streamExists = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            status = streamExists
                ? new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetBoolean(3))
                : new(0, 0, 0, false);
        }

        var publicationNames = new List<string>(33);
        var identityFencePresent = await HasOutboxIdentityFenceAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        await using (var publicationCommand = Command(connection, transaction, """
            SELECT pubname FROM pg_catalog.pg_publication_tables
            WHERE schemaname=@schema AND tablename='outbox'
            ORDER BY pubname LIMIT 33
            """, ("schema", _options.Schema)))
        await using (var reader = await publicationCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                publicationNames.Add(reader.GetString(0));
            }
        }

        var lagging = false;
        if (streamExists)
        {
            await using var replayCommand = Command(connection, transaction, $"""
                SELECT EXISTS(SELECT 1 FROM {_schema}.replay
                    WHERE tenant_id=@tenant AND stream_id=@stream AND checkpoint<@through)
                """, ("tenant", stream.TenantId), ("stream", stream.StreamId), ("through", throughSequence));
            lagging = (bool)(await replayCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        var blockers = EventRetentionBlocker.None;
        if (!_options.EnableLocalOnlyRetention) { blockers |= EventRetentionBlocker.LocalRetentionDisabled; }
        if (!streamExists) { blockers |= EventRetentionBlocker.StreamMissing; }
        if (status.RetainedThrough != expectedRetainedThrough) { blockers |= EventRetentionBlocker.FloorChanged; }
        if (throughSequence <= status.RetainedThrough) { blockers |= EventRetentionBlocker.FloorNotForward; }
        if (throughSequence > status.StreamHead) { blockers |= EventRetentionBlocker.BeyondStreamHead; }
        if (throughSequence > status.ArchivedThrough) { blockers |= EventRetentionBlocker.ArchiveIncomplete; }
        if (lagging) { blockers |= EventRetentionBlocker.ReplayCheckpointBehind; }
        if (publicationNames.Count > 0) { blockers |= EventRetentionBlocker.OutboxPublished; }
        if (!identityFencePresent) { blockers |= EventRetentionBlocker.OutboxContractChanged; }

        var additionalPublicationsOmitted = publicationNames.Count > 32;
        if (additionalPublicationsOmitted) { publicationNames.RemoveAt(32); }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new EventRetentionAssessment(stream, throughSequence, expectedRetainedThrough, status, blockers,
            publicationNames.AsReadOnly(), additionalPublicationsOmitted);
    }
}
