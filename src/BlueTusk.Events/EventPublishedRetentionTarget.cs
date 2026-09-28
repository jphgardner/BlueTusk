using System.Data.Common;
using System.Globalization;

namespace BlueTusk.Events;

/// <summary>Exact retention control decoded from the ordered source WAL.</summary>
internal sealed record PublishedRetentionControl(Guid Epoch, EventStreamKey Stream, long FirstSequence,
    long ThroughSequence, byte[] ArchiveManifestSha256, EventPublishedSourceIdentity Source,
    uint SourceDatabaseOid, uint SourcePublicationOid, long MembershipRevision);

public sealed partial class PostgreSqlEventStore
{
    /// <summary>
    /// Bind a source registration to this target's current database incarnation. This must be
    /// repeated with a fresh target incarnation after a restore; an old acknowledgement is never
    /// transferable. The registered source membership must precede the marker to be acknowledged.
    /// </summary>
    public async ValueTask RegisterPublishedRetentionTargetAsync(EventPublishedConsumerRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(registration.Stream);
        ArgumentNullException.ThrowIfNull(registration.Source);
        EventValidation.Key(registration.ConsumerGroup, nameof(registration));
        ValidatePublishedSource(registration.Source);
        if (registration.TargetIncarnation == Guid.Empty || registration.MembershipRevision <= 0 ||
            registration.SourceDatabaseOid == 0 || registration.SourcePublicationOid == 0)
        {
            throw new ArgumentException("The target requires a complete source membership registration.", nameof(registration));
        }
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var target = await ReadTargetIdentityAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO {_schema}.published_retention_targets
                (tenant_id,stream_id,consumer_group,target_incarnation,membership_revision,
                 source_system_identifier,source_database,source_database_oid,source_timeline,
                 source_slot,source_publication,source_publication_oid,
                 target_system_identifier,target_database,target_database_oid,target_timeline)
            VALUES(@tenant,@stream,@consumer,@incarnation,@revision,@system,@database,@database_oid,
                   @timeline,@slot,@publication,@publication_oid,
                   @target_system,@target_database,@target_database_oid,@target_timeline)
            ON CONFLICT DO NOTHING
            """, cancellationToken, ("tenant", registration.Stream.TenantId),
            ("stream", registration.Stream.StreamId), ("consumer", registration.ConsumerGroup),
            ("incarnation", registration.TargetIncarnation), ("revision", registration.MembershipRevision),
            ("system", registration.Source.SystemIdentifier), ("database", registration.Source.DatabaseName),
            ("database_oid", registration.SourceDatabaseOid), ("timeline", registration.Source.Timeline),
            ("slot", registration.Source.SlotName), ("publication", registration.Source.PublicationName),
            ("publication_oid", registration.SourcePublicationOid), ("target_system", target.SystemIdentifier),
            ("target_database", target.DatabaseName), ("target_database_oid", target.DatabaseOid),
            ("target_timeline", target.Timeline)).ConfigureAwait(false);
        await using (var command = Command(connection, transaction, $"""
            SELECT membership_revision,source_system_identifier,source_database,source_database_oid,
                source_timeline,source_slot,source_publication,source_publication_oid,
                target_system_identifier,target_database,target_database_oid,target_timeline
            FROM {_schema}.published_retention_targets
            WHERE tenant_id=@tenant AND stream_id=@stream AND consumer_group=@consumer
                AND target_incarnation=@incarnation
            """, ("tenant", registration.Stream.TenantId), ("stream", registration.Stream.StreamId),
            ("consumer", registration.ConsumerGroup), ("incarnation", registration.TargetIncarnation)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.GetInt64(0) != registration.MembershipRevision ||
                reader.GetString(1) != registration.Source.SystemIdentifier ||
                reader.GetString(2) != registration.Source.DatabaseName ||
                reader.GetFieldValue<uint>(3) != registration.SourceDatabaseOid ||
                reader.GetInt64(4) != registration.Source.Timeline ||
                reader.GetString(5) != registration.Source.SlotName ||
                reader.GetString(6) != registration.Source.PublicationName ||
                reader.GetFieldValue<uint>(7) != registration.SourcePublicationOid ||
                reader.GetString(8) != target.SystemIdentifier || reader.GetString(9) != target.DatabaseName ||
                reader.GetFieldValue<uint>(10) != target.DatabaseOid || reader.GetInt64(11) != target.Timeline)
            {
                throw new InvalidOperationException("The target incarnation or source membership differs from the durable registration.");
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Advance the target checkpoint under the caller's effect transaction before any handler runs.</summary>
    internal async ValueTask LockPublishedRetentionDeliveryAsync(DbConnection connection, DbTransaction transaction,
        string consumerGroup, Guid targetIncarnation, EventPublishedSourceIdentity source,
        ulong commitEndPosition, uint sourceTransactionId, PublishedRetentionControl? control,
        CancellationToken cancellationToken)
    {
        EventValidation.Transaction(connection, transaction);
        EventValidation.Key(consumerGroup, nameof(consumerGroup));
        ValidatePublishedSource(source);
        if (targetIncarnation == Guid.Empty || commitEndPosition == 0)
        {
            throw new InvalidOperationException("The protected delivery requires an incarnation and nonzero commit position.");
        }
        await using var command = Command(connection, transaction, $"""
            INSERT INTO {_schema}.published_retention_target_checkpoints AS current
                (consumer_group,target_incarnation,source_system_identifier,source_database,
                 source_slot,source_publication,source_timeline,commit_end_position,source_transaction_id)
            VALUES(@consumer,@incarnation,@system,@database,@slot,@publication,@timeline,@position,@xid)
            ON CONFLICT (consumer_group,target_incarnation,source_system_identifier,source_database,
                source_slot,source_publication,source_timeline)
            DO UPDATE SET commit_end_position=EXCLUDED.commit_end_position,
                source_transaction_id=EXCLUDED.source_transaction_id,updated_at=clock_timestamp()
            WHERE current.commit_end_position < EXCLUDED.commit_end_position OR
                (current.commit_end_position=EXCLUDED.commit_end_position AND
                 current.source_transaction_id=EXCLUDED.source_transaction_id)
            """, ("consumer", consumerGroup), ("incarnation", targetIncarnation),
            ("system", source.SystemIdentifier), ("database", source.DatabaseName),
            ("slot", source.SlotName), ("publication", source.PublicationName),
            ("timeline", source.Timeline), ("position", (decimal)commitEndPosition),
            ("xid", (long)sourceTransactionId));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            if (control is not null)
            {
                await using var existing = Command(connection, transaction, $"""
                    SELECT EXISTS(SELECT 1 FROM {_schema}.published_retention_acknowledgements
                        WHERE retention_epoch=@epoch AND consumer_group=@consumer
                            AND target_incarnation=@incarnation AND commit_end_position=@position)
                    """, ("epoch", control.Epoch), ("consumer", consumerGroup),
                    ("incarnation", targetIncarnation), ("position", (decimal)commitEndPosition));
                if ((bool)(await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
                {
                    // A later checkpoint is safe only for an already committed exact marker ACK.
                    // AcknowledgePublishedRetentionControlAsync verifies every remaining field.
                    return;
                }
            }
            throw new InvalidOperationException("The protected source delivery is older than its durable target checkpoint or has a conflicting transaction identity.");
        }
    }

    /// <summary>Persist an exact marker ACK in the same target transaction as its source checkpoint and effects.</summary>
    internal async ValueTask AcknowledgePublishedRetentionControlAsync(DbConnection connection, DbTransaction transaction,
        string consumerGroup, Guid targetIncarnation, EventPublishedSourceIdentity source,
        ulong commitEndPosition, PublishedRetentionControl control, CancellationToken cancellationToken)
    {
        EventValidation.Transaction(connection, transaction);
        if (control.Source != source || control.Epoch == Guid.Empty || control.FirstSequence <= 0 ||
            control.ThroughSequence < control.FirstSequence || control.MembershipRevision <= 0 ||
            control.ArchiveManifestSha256.Length != 32)
        {
            throw new InvalidOperationException("The ordered retention control does not match this protected source.");
        }
        var target = await ReadTargetIdentityAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        long registrationRevision;
        await using (var command = Command(connection, transaction, $"""
            SELECT membership_revision FROM {_schema}.published_retention_targets
            WHERE tenant_id=@tenant AND stream_id=@stream AND consumer_group=@consumer
                AND target_incarnation=@incarnation AND source_system_identifier=@system
                AND source_database=@database AND source_database_oid=@database_oid
                AND source_timeline=@timeline AND source_slot=@slot
                AND source_publication=@publication AND source_publication_oid=@publication_oid
                AND target_system_identifier=@target_system AND target_database=@target_database
                AND target_database_oid=@target_database_oid AND target_timeline=@target_timeline
            FOR SHARE
            """, ("tenant", control.Stream.TenantId), ("stream", control.Stream.StreamId),
            ("consumer", consumerGroup), ("incarnation", targetIncarnation),
            ("system", source.SystemIdentifier), ("database", source.DatabaseName),
            ("database_oid", control.SourceDatabaseOid), ("timeline", source.Timeline),
            ("slot", source.SlotName), ("publication", source.PublicationName),
            ("publication_oid", control.SourcePublicationOid),
            ("target_system", target.SystemIdentifier), ("target_database", target.DatabaseName),
            ("target_database_oid", target.DatabaseOid), ("target_timeline", target.Timeline)))
        {
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is null or DBNull)
            {
                throw new InvalidOperationException("The protected target incarnation is unregistered or its source/target lineage changed.");
            }
            registrationRevision = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        if (registrationRevision > control.MembershipRevision)
        {
            throw new InvalidOperationException("This target incarnation registered after the source marker; a newer marker is required.");
        }

        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO {_schema}.published_retention_acknowledgements
                (retention_epoch,tenant_id,stream_id,consumer_group,target_incarnation,
                 first_sequence,through_sequence,archive_manifest_sha256,membership_revision,
                 source_system_identifier,source_database,source_database_oid,source_timeline,
                 source_slot,source_publication,source_publication_oid,
                 target_system_identifier,target_database,target_database_oid,target_timeline,commit_end_position)
            VALUES(@epoch,@tenant,@stream,@consumer,@incarnation,@first,@through,@digest,@revision,
                   @system,@database,@database_oid,@timeline,@slot,@publication,@publication_oid,
                   @target_system,@target_database,@target_database_oid,@target_timeline,@position)
            ON CONFLICT DO NOTHING
            """, cancellationToken, ("epoch", control.Epoch), ("tenant", control.Stream.TenantId),
            ("stream", control.Stream.StreamId), ("consumer", consumerGroup),
            ("incarnation", targetIncarnation), ("first", control.FirstSequence),
            ("through", control.ThroughSequence), ("digest", control.ArchiveManifestSha256),
            ("revision", control.MembershipRevision), ("system", source.SystemIdentifier),
            ("database", source.DatabaseName), ("database_oid", control.SourceDatabaseOid),
            ("timeline", source.Timeline), ("slot", source.SlotName),
            ("publication", source.PublicationName), ("publication_oid", control.SourcePublicationOid),
            ("target_system", target.SystemIdentifier), ("target_database", target.DatabaseName),
            ("target_database_oid", target.DatabaseOid), ("target_timeline", target.Timeline),
            ("position", (decimal)commitEndPosition)).ConfigureAwait(false);
        await using var verify = Command(connection, transaction, $"""
            SELECT tenant_id,stream_id,first_sequence,through_sequence,archive_manifest_sha256,
                membership_revision,source_system_identifier,source_database,source_database_oid,
                source_timeline,source_slot,source_publication,source_publication_oid,
                target_system_identifier,target_database,target_database_oid,target_timeline,commit_end_position
            FROM {_schema}.published_retention_acknowledgements
            WHERE retention_epoch=@epoch AND consumer_group=@consumer AND target_incarnation=@incarnation
            """, ("epoch", control.Epoch), ("consumer", consumerGroup), ("incarnation", targetIncarnation));
        await using var reader = await verify.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetString(0) != control.Stream.TenantId || reader.GetString(1) != control.Stream.StreamId ||
            reader.GetInt64(2) != control.FirstSequence || reader.GetInt64(3) != control.ThroughSequence ||
            !((byte[])reader.GetValue(4)).AsSpan().SequenceEqual(control.ArchiveManifestSha256) ||
            reader.GetInt64(5) != control.MembershipRevision || reader.GetString(6) != source.SystemIdentifier ||
            reader.GetString(7) != source.DatabaseName || reader.GetFieldValue<uint>(8) != control.SourceDatabaseOid ||
            reader.GetInt64(9) != source.Timeline || reader.GetString(10) != source.SlotName ||
            reader.GetString(11) != source.PublicationName || reader.GetFieldValue<uint>(12) != control.SourcePublicationOid ||
            reader.GetString(13) != target.SystemIdentifier || reader.GetString(14) != target.DatabaseName ||
            reader.GetFieldValue<uint>(15) != target.DatabaseOid || reader.GetInt64(16) != target.Timeline ||
            reader.GetDecimal(17) != (decimal)commitEndPosition)
        {
            throw new InvalidOperationException("An acknowledgement for this epoch has conflicting durable contents.");
        }
    }

    private async ValueTask<(string SystemIdentifier, string DatabaseName, uint DatabaseOid, long Timeline)>
        ReadTargetIdentityAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT s.system_identifier::text,current_database(),d.oid,c.timeline_id::bigint,pg_is_in_recovery()
            FROM pg_catalog.pg_control_system() s CROSS JOIN pg_catalog.pg_control_checkpoint() c
            JOIN pg_catalog.pg_database d ON d.datname=current_database()
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetBoolean(4))
        {
            throw new InvalidOperationException("The protected target database identity is unavailable or is in recovery.");
        }
        return (reader.GetString(0), reader.GetString(1), reader.GetFieldValue<uint>(2), reader.GetInt64(3));
    }
}
