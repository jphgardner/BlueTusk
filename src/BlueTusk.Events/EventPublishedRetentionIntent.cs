using System.Buffers.Binary;
using System.Data.Common;
using System.Security.Cryptography;

namespace BlueTusk.Events;

/// <summary>Expected immutable PostgreSQL source and slot for an ordered retention intent.</summary>
public sealed record EventPublishedSourceIdentity(string SystemIdentifier, string DatabaseName,
    long Timeline, string SlotName, string PublicationName);

/// <summary>Append-only source membership for one protected target incarnation and stream.</summary>
public sealed record EventPublishedConsumerRegistration(EventStreamKey Stream, string ConsumerGroup,
    Guid TargetIncarnation, long MembershipRevision, EventPublishedSourceIdentity Source,
    uint SourceDatabaseOid, uint SourcePublicationOid);

/// <summary>An archived, source-ordered intent. This is not permission to advance a floor or delete rows.</summary>
public sealed record EventPublishedRetentionIntent(Guid Epoch, EventStreamKey Stream, long FirstSequence,
    long ThroughSequence, string ArchiveManifestSha256, EventPublishedSourceIdentity Source,
    long MembershipRevision);

public sealed partial class PostgreSqlEventStore
{
    /// <summary>
    /// Register an immutable protected consumer incarnation. Registrations cannot be removed or
    /// replaced by this protocol; every new registration fences older intents by bumping revision.
    /// </summary>
    public async ValueTask<EventPublishedConsumerRegistration> RegisterPublishedRetentionConsumerAsync(
        EventStreamKey stream, string consumerGroup, Guid targetIncarnation, EventPublishedSourceIdentity source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(source);
        EventValidation.Key(consumerGroup, nameof(consumerGroup));
        if (targetIncarnation == Guid.Empty) { throw new ArgumentException("A nonempty target incarnation is required.", nameof(targetIncarnation)); }
        ValidatePublishedSource(source);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO {_schema}.streams(tenant_id,stream_id) VALUES(@tenant,@stream) ON CONFLICT DO NOTHING
            """, cancellationToken, ("tenant", stream.TenantId), ("stream", stream.StreamId)).ConfigureAwait(false);
        var status = await LockRetentionStatusAsync(connection, transaction, stream, cancellationToken).ConfigureAwait(false);
        if (status.LocalRetentionEnabled || status.RetainedThrough != 0)
        {
            throw new InvalidOperationException("Published protection cannot be added to a stream with a local-retention floor.");
        }
        await ExecuteAsync(connection, transaction,
            $"LOCK TABLE {_schema}.outbox, {_schema}.published_retention_intents IN SHARE UPDATE EXCLUSIVE MODE",
            cancellationToken).ConfigureAwait(false);
        if (!await HasOutboxIdentityFenceAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The outbox identity insert fence changed.");
        }
        await CheckControlPublicationCoverageAsync(connection, transaction, source.PublicationName,
            cancellationToken).ConfigureAwait(false);
        var (databaseOid, publicationOid) = await CheckPublishedSourceAsync(connection, transaction, source,
            cancellationToken).ConfigureAwait(false);

        // This stage emits one marker lineage per stream. A different slot/publication requires a
        // separate multi-lineage marker/ACK protocol and must not be silently mixed into this set.
        await using (var lineage = Command(connection, transaction, $"""
            SELECT EXISTS(SELECT 1 FROM {_schema}.published_retention_members
                WHERE tenant_id=@tenant AND stream_id=@stream AND
                    (source_system_identifier<>@system OR source_database<>@database OR
                     source_database_oid<>@database_oid OR source_timeline<>@timeline OR
                     source_slot<>@slot OR source_publication<>@publication OR
                     source_publication_oid<>@publication_oid))
            """, ("tenant", stream.TenantId), ("stream", stream.StreamId),
            ("system", source.SystemIdentifier), ("database", source.DatabaseName),
            ("database_oid", databaseOid), ("timeline", source.Timeline),
            ("slot", source.SlotName), ("publication", source.PublicationName),
            ("publication_oid", publicationOid)))
        {
            if ((bool)(await lineage.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
            {
                throw new InvalidOperationException("All protected members of this stream must use the same immutable source/slot/publication lineage.");
            }
        }

        EventPublishedConsumerRegistration? existingRegistration = null;
        await using (var existing = Command(connection, transaction, $"""
            SELECT membership_revision,source_system_identifier,source_database,source_database_oid,
                source_timeline,source_slot,source_publication,source_publication_oid
            FROM {_schema}.published_retention_members
            WHERE tenant_id=@tenant AND stream_id=@stream AND consumer_group=@consumer
                AND target_incarnation=@incarnation
            """, ("tenant", stream.TenantId), ("stream", stream.StreamId),
            ("consumer", consumerGroup), ("incarnation", targetIncarnation)))
        await using (var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetString(1) != source.SystemIdentifier || reader.GetString(2) != source.DatabaseName ||
                    reader.GetFieldValue<uint>(3) != databaseOid || reader.GetInt64(4) != source.Timeline ||
                    reader.GetString(5) != source.SlotName || reader.GetString(6) != source.PublicationName ||
                    reader.GetFieldValue<uint>(7) != publicationOid)
                {
                    throw new InvalidOperationException("The registered consumer incarnation has different source lineage.");
                }
                existingRegistration = new EventPublishedConsumerRegistration(stream, consumerGroup, targetIncarnation,
                    reader.GetInt64(0), source, databaseOid, publicationOid);
            }
        }
        if (existingRegistration is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existingRegistration;
        }

        long revision;
        await using (var increment = Command(connection, transaction, $"""
            UPDATE {_schema}.streams SET published_membership_revision=published_membership_revision+1
            WHERE tenant_id=@tenant AND stream_id=@stream RETURNING published_membership_revision
            """, ("tenant", stream.TenantId), ("stream", stream.StreamId)))
        {
            revision = Convert.ToInt64(await increment.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
        }
        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO {_schema}.published_retention_members
                (tenant_id,stream_id,consumer_group,target_incarnation,membership_revision,
                 source_system_identifier,source_database,source_database_oid,source_timeline,
                 source_slot,source_publication,source_publication_oid)
            VALUES(@tenant,@stream,@consumer,@incarnation,@revision,@system,@database,@database_oid,
                   @timeline,@slot,@publication,@publication_oid)
            """, cancellationToken, ("tenant", stream.TenantId), ("stream", stream.StreamId),
            ("consumer", consumerGroup), ("incarnation", targetIncarnation), ("revision", revision),
            ("system", source.SystemIdentifier), ("database", source.DatabaseName),
            ("database_oid", databaseOid), ("timeline", source.Timeline), ("slot", source.SlotName),
            ("publication", source.PublicationName), ("publication_oid", publicationOid)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new EventPublishedConsumerRegistration(stream, consumerGroup, targetIncarnation,
            revision, source, databaseOid, publicationOid);
    }

    /// <summary>
    /// Emit an append-only CDC control row only after bounded archive readback and a fresh source/catalogue
    /// check. Every publication carrying the outbox must also carry the complete control relation.
    /// The returned intent is preparation only; published pruning remains prohibited.
    /// </summary>
    public async ValueTask<EventPublishedRetentionIntent> PublishRetentionIntentAsync(EventStreamKey stream,
        IEventArchiveStore archive, EventPublishedSourceIdentity source, long throughSequence,
        long expectedPreviousThrough = 0, int maximumSegments = 64,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(throughSequence);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedPreviousThrough);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSegments);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumSegments, 256);
        ValidatePublishedSource(source);
        if (throughSequence <= expectedPreviousThrough)
        {
            throw new ArgumentException("The published range must advance beyond the expected prior intent.", nameof(throughSequence));
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var status = await LockRetentionStatusAsync(connection, transaction, stream, cancellationToken).ConfigureAwait(false);
        if (status.LocalRetentionEnabled || status.RetainedThrough != 0 || throughSequence > status.ArchivedThrough)
        {
            throw new InvalidOperationException("Published intents require a complete archived prefix and no prior local-retention floor.");
        }
        long membershipRevision;
        await using (var membership = Command(connection, transaction, $"""
            SELECT published_membership_revision FROM {_schema}.streams
            WHERE tenant_id=@tenant AND stream_id=@stream
            """, ("tenant", stream.TenantId), ("stream", stream.StreamId)))
        {
            membershipRevision = Convert.ToInt64(await membership.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        }
        if (membershipRevision == 0)
        {
            throw new InvalidOperationException("A published intent requires at least one protected consumer registration.");
        }

        // Explicit table-publication DDL conflicts with these locks. A deployment must also freeze
        // FOR ALL TABLES / FOR TABLES IN SCHEMA publication DDL during this protocol.
        await ExecuteAsync(connection, transaction,
            $"LOCK TABLE {_schema}.outbox, {_schema}.published_retention_intents IN SHARE UPDATE EXCLUSIVE MODE",
            cancellationToken).ConfigureAwait(false);
        if (!await HasOutboxIdentityFenceAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The outbox identity insert fence changed.");
        }

        await CheckControlPublicationCoverageAsync(connection, transaction, source.PublicationName,
            cancellationToken).ConfigureAwait(false);
        var (databaseOid, publicationOid) = await CheckPublishedSourceAsync(connection, transaction, source,
            cancellationToken).ConfigureAwait(false);

        long previousThrough = 0;
        byte[]? previousDigest = null;
        await using (var previous = Command(connection, transaction, $"""
            SELECT through_sequence,archive_manifest_sha256,source_system_identifier,source_database,
                source_database_oid,source_timeline,source_slot,source_publication,source_publication_oid
            FROM {_schema}.published_retention_intents
            WHERE tenant_id=@tenant AND stream_id=@stream
            ORDER BY through_sequence DESC LIMIT 1
            """, ("tenant", stream.TenantId), ("stream", stream.StreamId)))
        await using (var reader = await previous.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                previousThrough = reader.GetInt64(0);
                previousDigest = (byte[])reader.GetValue(1);
                if (reader.GetString(2) != source.SystemIdentifier || reader.GetString(3) != source.DatabaseName ||
                    reader.GetFieldValue<uint>(4) != databaseOid || reader.GetInt64(5) != source.Timeline ||
                    reader.GetString(6) != source.SlotName || reader.GetString(7) != source.PublicationName ||
                    reader.GetFieldValue<uint>(8) != publicationOid)
                {
                    throw new InvalidOperationException("Published retention source lineage changed since the prior intent.");
                }
            }
        }
        if (previousThrough != expectedPreviousThrough)
        {
            throw new InvalidOperationException("The previous published retention intent differs from the expected range.");
        }

        using var manifest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(manifest, "BlueTusk.PublishedRetentionManifest.v1");
        AppendString(manifest, stream.TenantId);
        AppendString(manifest, stream.StreamId);
        if (previousDigest is not null) { manifest.AppendData(previousDigest); }
        var next = checked(previousThrough + 1);
        var segmentCount = 0;
        long totalPayloadBytes = 0;
        await using (var segments = Command(connection, transaction, $"""
            SELECT first_sequence,last_sequence,archive_id,batch_sha256,event_count
            FROM {_schema}.archive_segments
            WHERE tenant_id=@tenant AND stream_id=@stream AND first_sequence>=@first
                AND last_sequence<=@through
            ORDER BY first_sequence LIMIT @limit
            """, ("tenant", stream.TenantId), ("stream", stream.StreamId),
            ("first", next), ("through", throughSequence), ("limit", maximumSegments + 1)))
        await using (var reader = await segments.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (++segmentCount > maximumSegments)
                {
                    throw new InvalidOperationException("The intent exceeds its archive segment bound; publish a smaller prefix.");
                }
                var first = reader.GetInt64(0);
                var last = reader.GetInt64(1);
                var archiveId = reader.GetString(2);
                var expectedHash = (byte[])reader.GetValue(3);
                var expectedCount = reader.GetInt32(4);
                if (first != next || last - first + 1 != expectedCount || expectedCount > 65_536)
                {
                    throw new InvalidOperationException("The archived prefix has a gap or invalid segment bound.");
                }
                var restored = await archive.ReadAsync(archiveId, cancellationToken).ConfigureAwait(false);
                var valid = restored is not null && restored.Count == expectedCount;
                long payloadBytes = 0;
                if (valid)
                {
                    for (var index = 0; index < restored!.Count; index++)
                    {
                        var value = restored[index];
                        if (value is null || value.Stream != stream || value.Sequence != first + index ||
                            value.Payload.Length == 0 || value.Payload.Length > 16_777_216 ||
                            (payloadBytes += value.Payload.Length) > 67_108_864 ||
                            (totalPayloadBytes += value.Payload.Length) > 67_108_864)
                        {
                            valid = false;
                            break;
                        }
                    }
                }
                if (!valid || !HashBatch(restored!).AsSpan().SequenceEqual(expectedHash))
                {
                    throw new InvalidOperationException("The committed archive manifest no longer matches its readback.");
                }
                AppendInt64(manifest, first);
                AppendInt64(manifest, last);
                AppendInt64(manifest, expectedCount);
                AppendString(manifest, archiveId);
                manifest.AppendData(expectedHash);
                next = checked(last + 1);
            }
        }
        if (segmentCount == 0 || next - 1 != throughSequence)
        {
            throw new InvalidOperationException("The requested published intent is not an exact contiguous archive boundary.");
        }

        var digest = manifest.GetHashAndReset();
        var epoch = Guid.NewGuid();
        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO {_schema}.published_retention_intents
                (retention_epoch,tenant_id,stream_id,first_sequence,through_sequence,archive_manifest_sha256,
                 source_system_identifier,source_database,source_database_oid,source_timeline,source_slot,
                 source_publication,source_publication_oid,membership_revision)
            VALUES(@epoch,@tenant,@stream,@first,@through,@digest,@system,@database,@database_oid,
                   @timeline,@slot,@publication,@publication_oid,@revision)
            """, cancellationToken, ("epoch", epoch), ("tenant", stream.TenantId),
            ("stream", stream.StreamId), ("first", checked(previousThrough + 1)), ("through", throughSequence),
            ("digest", digest), ("system", source.SystemIdentifier), ("database", source.DatabaseName),
            ("database_oid", databaseOid), ("timeline", source.Timeline), ("slot", source.SlotName),
            ("publication", source.PublicationName), ("publication_oid", publicationOid),
            ("revision", membershipRevision)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new EventPublishedRetentionIntent(epoch, stream, checked(previousThrough + 1), throughSequence,
            Convert.ToHexStringLower(digest), source, membershipRevision);
    }

    private static void ValidatePublishedSource(EventPublishedSourceIdentity source)
    {
        if (source.Timeline <= 0 ||
            string.IsNullOrWhiteSpace(source.SystemIdentifier) ||
            string.IsNullOrWhiteSpace(source.DatabaseName) ||
            string.IsNullOrWhiteSpace(source.SlotName) ||
            string.IsNullOrWhiteSpace(source.PublicationName) ||
            source.SlotName.Length > 63 || source.PublicationName.Length > 63 ||
            source.SystemIdentifier.Length > 200 || source.DatabaseName.Length > 200 ||
            source.SystemIdentifier.Contains('\0') || source.DatabaseName.Contains('\0') ||
            source.SlotName.Contains('\0') || source.PublicationName.Contains('\0'))
        {
            throw new ArgumentException("Bounded, explicit source lineage is required.", nameof(source));
        }
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private async ValueTask<(uint DatabaseOid, uint PublicationOid)> CheckPublishedSourceAsync(
        DbConnection connection, DbTransaction transaction, EventPublishedSourceIdentity source,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT s.system_identifier::text,c.timeline_id::bigint,current_database(),d.oid,p.oid,
                r.plugin,r.database,r.slot_type,pg_is_in_recovery(),r.temporary
            FROM pg_catalog.pg_control_system() s CROSS JOIN pg_catalog.pg_control_checkpoint() c
            JOIN pg_catalog.pg_database d ON d.datname=current_database()
            JOIN pg_catalog.pg_publication p ON p.pubname=@publication
            JOIN pg_catalog.pg_replication_slots r ON r.slot_name=@slot
            """, ("publication", source.PublicationName), ("slot", source.SlotName));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetString(0) != source.SystemIdentifier || reader.GetInt64(1) != source.Timeline ||
            reader.GetString(2) != source.DatabaseName || reader.GetString(5) != "pgoutput" ||
            reader.GetString(6) != source.DatabaseName || reader.GetString(7) != "logical" ||
            reader.GetBoolean(8) || reader.GetBoolean(9))
        {
            throw new InvalidOperationException("The current PostgreSQL system, timeline, database, publication or logical slot differs from the expected source.");
        }
        return (reader.GetFieldValue<uint>(3), reader.GetFieldValue<uint>(4));
    }

    private async ValueTask CheckControlPublicationCoverageAsync(DbConnection connection, DbTransaction transaction,
        string sourcePublication, CancellationToken cancellationToken)
    {
        var sourceSeen = false;
        var count = 0;
        await using var command = Command(connection, transaction, """
            SELECT o.pubname,p.pubinsert,o.rowfilter,cardinality(o.attnames),
                (SELECT count(*) FROM pg_catalog.pg_attribute a
                 JOIN pg_catalog.pg_class c ON c.oid=a.attrelid
                 JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                 WHERE n.nspname=@schema AND c.relname='outbox' AND a.attnum>0 AND NOT a.attisdropped),
                m.rowfilter,cardinality(m.attnames),
                (SELECT count(*) FROM pg_catalog.pg_attribute a
                 JOIN pg_catalog.pg_class c ON c.oid=a.attrelid
                 JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                 WHERE n.nspname=@schema AND c.relname='published_retention_intents' AND a.attnum>0 AND NOT a.attisdropped),
                m.pubname IS NOT NULL
            FROM pg_catalog.pg_publication_tables o
            JOIN pg_catalog.pg_publication p ON p.pubname=o.pubname
            LEFT JOIN pg_catalog.pg_publication_tables m
                ON m.pubname=o.pubname AND m.schemaname=@schema AND m.tablename='published_retention_intents'
            WHERE o.schemaname=@schema AND o.tablename='outbox'
            ORDER BY o.pubname LIMIT 257
            """, ("schema", _options.Schema));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (++count > 256 || !reader.GetBoolean(1) || !reader.IsDBNull(2) ||
                reader.GetInt32(3) != reader.GetInt64(4) || !reader.GetBoolean(8) ||
                !reader.IsDBNull(5) || reader.GetInt32(6) != reader.GetInt64(7))
            {
                throw new InvalidOperationException("Every outbox publication must insert complete, unfiltered outbox and retention-control rows.");
            }
            sourceSeen |= reader.GetString(0) == sourcePublication;
        }
        if (!sourceSeen)
        {
            throw new InvalidOperationException("The expected source publication does not carry the outbox and retention control relation.");
        }
    }
}
