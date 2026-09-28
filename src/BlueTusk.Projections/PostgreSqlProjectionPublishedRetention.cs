using System.Data;
using System.Data.Common;
using System.Globalization;
using BlueTusk.Events;
using BlueTusk.Streams;

namespace BlueTusk.Projections;

/// <summary>Explicit source registration and target incarnation for a protected projection worker.</summary>
public sealed record ProjectionPublishedRetentionTargetOptions(
    Guid TargetIncarnation, EventPublishedSourceIdentity Source, string EventsSchema, string ConsumerGroup);

public sealed partial class PostgreSqlProjectionStore
{
    /// <summary>
    /// Bind one source membership to a projection version and this database incarnation. This
    /// target-local registration is not remote proof that the source membership still exists.
    /// </summary>
    public async ValueTask RegisterPublishedRetentionTargetAsync(ProjectionIdentity identity,
        ProjectionSourceLineage sourceLineage, EventPublishedConsumerRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(sourceLineage);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(registration.Stream);
        ArgumentNullException.ThrowIfNull(registration.Source);
        ProjectionValidation.Key(registration.ConsumerGroup, nameof(registration));
        if (System.Diagnostics.Stopwatch.GetElapsedTime(sourceLineage.CaptureTimestamp) > TimeSpan.FromMinutes(1))
        {
            throw new InvalidOperationException("Capture fresh actual source lineage before binding a protected target registration.");
        }
        if (registration.TargetIncarnation == Guid.Empty || registration.MembershipRevision <= 0 ||
            registration.SourceDatabaseOid == 0 || registration.SourcePublicationOid == 0 ||
            registration.Source.Timeline <= 0 ||
            identity.Source.SystemIdentifier != registration.Source.SystemIdentifier ||
            identity.Source.DatabaseName != registration.Source.DatabaseName ||
            identity.Source.SlotName != registration.Source.SlotName ||
            sourceLineage.Source != identity.Source ||
            sourceLineage.Source.SystemIdentifier != registration.Source.SystemIdentifier ||
            sourceLineage.Source.DatabaseName != registration.Source.DatabaseName ||
            sourceLineage.Source.SlotName != registration.Source.SlotName ||
            sourceLineage.DatabaseOid != registration.SourceDatabaseOid ||
            sourceLineage.Timeline != registration.Source.Timeline ||
            sourceLineage.PublicationName != registration.Source.PublicationName ||
            sourceLineage.PublicationOid != registration.SourcePublicationOid)
        {
            throw new ArgumentException("The target registration must match a complete immutable projection source membership.", nameof(registration));
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockHeadAsync(connection, transaction, identity.Name, cancellationToken).ConfigureAwait(false);
        _ = await ReadStateAsync(connection, transaction, identity, true, cancellationToken).ConfigureAwait(false);
        var lineage = await ReadBoundLineageAsync(connection, transaction, identity, cancellationToken).ConfigureAwait(false);
        if (lineage != sourceLineage.Fingerprint)
        {
            throw new InvalidOperationException("The protected target registration does not match the projection's pre-snapshot source lineage.");
        }
        var target = await ReadRetentionTargetIdentityAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO {_schema}.published_retention_targets
                (projection,version,tenant_id,stream_id,consumer_group,target_incarnation,membership_revision,
                 source_system_identifier,source_database,source_database_oid,source_timeline,source_slot,
                 source_publication,source_publication_oid,source_fingerprint,definition_fingerprint,
                 projection_source_lineage,target_system_identifier,target_database,target_database_oid,target_timeline)
            VALUES(@projection,@version,@tenant,@stream,@consumer,@incarnation,@revision,
                   @system,@database,@database_oid,@timeline,@slot,@publication,@publication_oid,
                   @source_fingerprint,@definition,@lineage,@target_system,@target_database,@target_database_oid,@target_timeline)
            ON CONFLICT DO NOTHING
            """, cancellationToken, ("projection", identity.Name), ("version", identity.Version),
            ("tenant", registration.Stream.TenantId), ("stream", registration.Stream.StreamId),
            ("consumer", registration.ConsumerGroup), ("incarnation", registration.TargetIncarnation),
            ("revision", registration.MembershipRevision), ("system", registration.Source.SystemIdentifier),
            ("database", registration.Source.DatabaseName), ("database_oid", registration.SourceDatabaseOid),
            ("timeline", registration.Source.Timeline), ("slot", registration.Source.SlotName),
            ("publication", registration.Source.PublicationName), ("publication_oid", registration.SourcePublicationOid),
            ("source_fingerprint", identity.Source.Fingerprint), ("definition", identity.DefinitionFingerprint),
            ("lineage", lineage), ("target_system", target.SystemIdentifier),
            ("target_database", target.DatabaseName), ("target_database_oid", target.DatabaseOid),
            ("target_timeline", target.Timeline)).ConfigureAwait(false);
        await using (var command = Command(connection, transaction, $"""
            SELECT membership_revision,source_system_identifier,source_database,source_database_oid,
                source_timeline,source_slot,source_publication,source_publication_oid,
                source_fingerprint,definition_fingerprint,projection_source_lineage,
                target_system_identifier,target_database,target_database_oid,target_timeline
            FROM {_schema}.published_retention_targets
            WHERE projection=@projection AND version=@version AND tenant_id=@tenant AND stream_id=@stream
                AND consumer_group=@consumer AND target_incarnation=@incarnation
            """, ("projection", identity.Name), ("version", identity.Version),
            ("tenant", registration.Stream.TenantId), ("stream", registration.Stream.StreamId),
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
                reader.GetString(8) != identity.Source.Fingerprint ||
                reader.GetString(9) != identity.DefinitionFingerprint || reader.GetString(10) != lineage ||
                reader.GetString(11) != target.SystemIdentifier ||
                reader.GetString(12) != target.DatabaseName ||
                reader.GetFieldValue<uint>(13) != target.DatabaseOid || reader.GetInt64(14) != target.Timeline)
            {
                throw new InvalidOperationException("The projection target registration has conflicting durable identity.");
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<ProjectionApplyResult> ApplyProtectedAsync(ProjectionLease lease,
        IProjectionDefinition definition, ChangeTransactionDelivery delivery,
        ProjectionPublishedRetentionTargetOptions protectedRetention, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var outcome = "failed";
        try
        {
            var result = await ApplyProtectedCoreAsync(lease, definition, delivery,
                protectedRetention, cancellationToken).ConfigureAwait(false);
            outcome = result.WasApplied ? "committed" : "duplicate";
            return result;
        }
        catch (OperationCanceledException) { outcome = "canceled"; throw; }
        finally { ProjectionsDiagnostics.Apply(started, outcome); }
    }

    private async ValueTask<ProjectionApplyResult> ApplyProtectedCoreAsync(ProjectionLease lease,
        IProjectionDefinition definition, ChangeTransactionDelivery delivery,
        ProjectionPublishedRetentionTargetOptions protectedRetention, CancellationToken cancellationToken)
    {
        ValidateDefinition(lease, definition);
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(protectedRetention);
        if (delivery.State != ChangeDeliveryState.Active ||
            protectedRetention.TargetIncarnation == Guid.Empty ||
            protectedRetention.Source is null ||
            protectedRetention.Source.SystemIdentifier != lease.Identity.Source.SystemIdentifier ||
            protectedRetention.Source.DatabaseName != lease.Identity.Source.DatabaseName ||
            protectedRetention.Source.SlotName != lease.Identity.Source.SlotName ||
            string.IsNullOrWhiteSpace(protectedRetention.ConsumerGroup) ||
            string.IsNullOrWhiteSpace(protectedRetention.EventsSchema) ||
            !delivery.HasSingleReplicationPublicationOnTimeline(
                protectedRetention.Source.PublicationName, protectedRetention.Source.Timeline))
        {
            throw new InvalidOperationException("Protected projection delivery needs an active verified single-publication source and target incarnation.");
        }
        return await ApplyCoreAsync(lease, definition, delivery.Transaction, cancellationToken,
            protectedRetention).ConfigureAwait(false);
    }

    private async ValueTask AcknowledgeProjectionControlAsync(DbConnection connection, DbTransaction transaction,
        ProjectionLease lease, ProjectionPublishedRetentionTargetOptions targetOptions,
        PublishedRetentionControl control, ChangeTransaction source, ProjectionState state,
        CancellationToken cancellationToken)
    {
        var identity = lease.Identity;
        var target = await ReadRetentionTargetIdentityAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var lineage = await ReadBoundLineageAsync(connection, transaction, identity, cancellationToken).ConfigureAwait(false);
        var role = await ReadRetentionRoleAsync(connection, transaction, identity, lineage, cancellationToken).ConfigureAwait(false);
        await using (var member = Command(connection, transaction, $"""
            SELECT membership_revision FROM {_schema}.published_retention_targets
            WHERE projection=@projection AND version=@version AND tenant_id=@tenant AND stream_id=@stream
                AND consumer_group=@consumer AND target_incarnation=@incarnation
                AND source_system_identifier=@system AND source_database=@database
                AND source_database_oid=@database_oid AND source_timeline=@timeline
                AND source_slot=@slot AND source_publication=@publication
                AND source_publication_oid=@publication_oid AND source_fingerprint=@source_fingerprint
                AND definition_fingerprint=@definition AND projection_source_lineage=@lineage
                AND target_system_identifier=@target_system AND target_database=@target_database
                AND target_database_oid=@target_database_oid AND target_timeline=@target_timeline
            FOR SHARE
            """, ("projection", identity.Name), ("version", identity.Version),
            ("tenant", control.Stream.TenantId), ("stream", control.Stream.StreamId),
            ("consumer", targetOptions.ConsumerGroup), ("incarnation", targetOptions.TargetIncarnation),
            ("system", control.Source.SystemIdentifier), ("database", control.Source.DatabaseName),
            ("database_oid", control.SourceDatabaseOid), ("timeline", control.Source.Timeline),
            ("slot", control.Source.SlotName), ("publication", control.Source.PublicationName),
            ("publication_oid", control.SourcePublicationOid), ("source_fingerprint", identity.Source.Fingerprint),
            ("definition", identity.DefinitionFingerprint), ("lineage", lineage),
            ("target_system", target.SystemIdentifier), ("target_database", target.DatabaseName),
            ("target_database_oid", target.DatabaseOid), ("target_timeline", target.Timeline)))
        {
            var revision = await member.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (revision is null || revision is DBNull ||
                Convert.ToInt64(revision, CultureInfo.InvariantCulture) > control.MembershipRevision)
            {
                throw new InvalidOperationException("The ordered retention intent predates or lacks this target's immutable membership.");
            }
        }

        await using (var insert = Command(connection, transaction, $"""
            INSERT INTO {_schema}.published_retention_acknowledgements
                (projection,version,tenant_id,stream_id,consumer_group,target_incarnation,retention_epoch,
                 first_sequence,through_sequence,archive_manifest_sha256,membership_revision,
                 source_system_identifier,source_database,source_database_oid,source_timeline,source_slot,
                 source_publication,source_publication_oid,target_system_identifier,target_database,
                 target_database_oid,target_timeline,definition_fingerprint,source_fingerprint,
                 projection_source_lineage,snapshot_epoch,active_version,recovery_id,role,
                 commit_end_position,source_transaction_id,checkpoint)
            VALUES(@projection,@version,@tenant,@stream,@consumer,@incarnation,@epoch,
                   @first,@through,@digest,@revision,@system,@database,@database_oid,@timeline,@slot,
                   @publication,@publication_oid,@target_system,@target_database,@target_database_oid,
                   @target_timeline,@definition,@source_fingerprint,@lineage,@snapshot,
                   @active,@recovery,@role,@position,@xid,@checkpoint)
            ON CONFLICT DO NOTHING
            """, ("projection", identity.Name), ("version", identity.Version),
            ("tenant", control.Stream.TenantId), ("stream", control.Stream.StreamId),
            ("consumer", targetOptions.ConsumerGroup), ("incarnation", targetOptions.TargetIncarnation),
            ("epoch", control.Epoch), ("first", control.FirstSequence), ("through", control.ThroughSequence),
            ("digest", control.ArchiveManifestSha256), ("revision", control.MembershipRevision),
            ("system", control.Source.SystemIdentifier), ("database", control.Source.DatabaseName),
            ("database_oid", control.SourceDatabaseOid), ("timeline", control.Source.Timeline),
            ("slot", control.Source.SlotName), ("publication", control.Source.PublicationName),
            ("publication_oid", control.SourcePublicationOid), ("target_system", target.SystemIdentifier),
            ("target_database", target.DatabaseName), ("target_database_oid", target.DatabaseOid),
            ("target_timeline", target.Timeline), ("definition", identity.DefinitionFingerprint),
            ("source_fingerprint", identity.Source.Fingerprint), ("lineage", lineage),
            ("snapshot", state.SnapshotEpoch!.Value), ("active", role.ActiveVersion.GetValueOrDefault()),
            ("recovery", role.RecoveryId.GetValueOrDefault()),
            ("role", role.Name), ("position", (decimal)source.CommitEndPosition.Value),
            ("xid", (long)source.TransactionId), ("checkpoint", (decimal)source.CommitEndPosition.Value)))
        {
            if (role.ActiveVersion is null)
            {
                insert.Parameters["active"].DbType = DbType.Int32;
                insert.Parameters["active"].Value = DBNull.Value;
            }
            if (role.RecoveryId is null)
            {
                insert.Parameters["recovery"].DbType = DbType.Guid;
                insert.Parameters["recovery"].Value = DBNull.Value;
            }
            _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await VerifyProjectionControlAsync(connection, transaction, lease, targetOptions, control, source,
            state, target, lineage, role, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask VerifyExistingProjectionControlAsync(DbConnection connection,
        DbTransaction transaction, ProjectionLease lease,
        ProjectionPublishedRetentionTargetOptions targetOptions, PublishedRetentionControl control,
        ChangeTransaction source, ProjectionState state, CancellationToken cancellationToken)
    {
        var target = await ReadRetentionTargetIdentityAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var lineage = await ReadBoundLineageAsync(connection, transaction, lease.Identity, cancellationToken).ConfigureAwait(false);
        // Transport redelivery can cross promotion after the target commit. Verify the durable
        // historical ACK, but never relabel a candidate ACK as active on replay.
        await VerifyProjectionControlAsync(connection, transaction, lease, targetOptions, control, source,
            state, target, lineage, null, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask VerifyProjectionControlAsync(DbConnection connection, DbTransaction transaction,
        ProjectionLease lease, ProjectionPublishedRetentionTargetOptions options, PublishedRetentionControl control,
        ChangeTransaction source, ProjectionState state, RetentionTargetIdentity target, string lineage,
        RetentionRole? role, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"""
            SELECT tenant_id,stream_id,first_sequence,through_sequence,archive_manifest_sha256,membership_revision,
                source_system_identifier,source_database,source_database_oid,source_timeline,source_slot,
                source_publication,source_publication_oid,target_system_identifier,target_database,
                target_database_oid,target_timeline,definition_fingerprint,source_fingerprint,
                projection_source_lineage,snapshot_epoch,active_version,recovery_id,role,
                commit_end_position,source_transaction_id,checkpoint
            FROM {_schema}.published_retention_acknowledgements
            WHERE projection=@projection AND version=@version AND retention_epoch=@epoch
                AND consumer_group=@consumer AND target_incarnation=@incarnation
            """, ("projection", lease.Identity.Name), ("version", lease.Identity.Version),
            ("epoch", control.Epoch), ("consumer", options.ConsumerGroup),
            ("incarnation", options.TargetIncarnation));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetString(0) != control.Stream.TenantId || reader.GetString(1) != control.Stream.StreamId ||
            reader.GetInt64(2) != control.FirstSequence || reader.GetInt64(3) != control.ThroughSequence ||
            !((byte[])reader.GetValue(4)).AsSpan().SequenceEqual(control.ArchiveManifestSha256) ||
            reader.GetInt64(5) != control.MembershipRevision ||
            reader.GetString(6) != control.Source.SystemIdentifier ||
            reader.GetString(7) != control.Source.DatabaseName ||
            reader.GetFieldValue<uint>(8) != control.SourceDatabaseOid ||
            reader.GetInt64(9) != control.Source.Timeline || reader.GetString(10) != control.Source.SlotName ||
            reader.GetString(11) != control.Source.PublicationName ||
            reader.GetFieldValue<uint>(12) != control.SourcePublicationOid ||
            reader.GetString(13) != target.SystemIdentifier || reader.GetString(14) != target.DatabaseName ||
            reader.GetFieldValue<uint>(15) != target.DatabaseOid || reader.GetInt64(16) != target.Timeline ||
            reader.GetString(17) != lease.Identity.DefinitionFingerprint ||
            reader.GetString(18) != lease.Identity.Source.Fingerprint || reader.GetString(19) != lineage ||
            reader.GetGuid(20) != state.SnapshotEpoch ||
            (role is not null && ((reader.IsDBNull(21) ? null : reader.GetInt32(21)) != role.ActiveVersion ||
                (reader.IsDBNull(22) ? null : reader.GetGuid(22)) != role.RecoveryId ||
                reader.GetString(23) != role.Name)) ||
            (role is null && !IsHistoricalRoleConsistent(reader, lease.Identity.Version)) ||
            reader.GetDecimal(24) != (decimal)source.CommitEndPosition.Value ||
            reader.GetInt64(25) != source.TransactionId ||
            reader.GetDecimal(26) != (decimal)source.CommitEndPosition.Value)
        {
            throw new InvalidOperationException("A projection ACK for this marker has conflicting immutable source, target, version, or recovery identity.");
        }
    }

    private static bool IsHistoricalRoleConsistent(DbDataReader reader, int version)
    {
        var active = reader.IsDBNull(21) ? (int?)null : reader.GetInt32(21);
        var recovery = reader.IsDBNull(22) ? (Guid?)null : reader.GetGuid(22);
        return reader.GetString(23) switch
        {
            "active" => active == version,
            "candidate" => active != version && recovery is null,
            "recovery_candidate" => active != version && recovery is not null,
            _ => false
        };
    }

    private async ValueTask<string> ReadBoundLineageAsync(DbConnection connection, DbTransaction transaction,
        ProjectionIdentity identity, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            $"SELECT source_lineage FROM {_schema}.state WHERE projection=@projection AND version=@version",
            ("projection", identity.Name), ("version", identity.Version));
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string lineage ||
            string.IsNullOrWhiteSpace(lineage))
        {
            throw new InvalidOperationException("Protected projection retention requires a source lineage bound before snapshot and CDC.");
        }
        return lineage;
    }

    private async ValueTask<RetentionRole> ReadRetentionRoleAsync(DbConnection connection, DbTransaction transaction,
        ProjectionIdentity identity, string lineage, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"""
            SELECT h.active_version,r.recovery_id,r.active_version,r.candidate_version,
                r.target_lineage,r.completed_at IS NOT NULL
            FROM {_schema}.heads h LEFT JOIN {_schema}.recovery_tickets r
                ON r.projection=h.projection AND (r.active_version=@version OR r.candidate_version=@version)
            WHERE h.projection=@projection
            """, ("projection", identity.Name), ("version", identity.Version));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The projection publication head is absent.");
        }
        var activeVersion = reader.IsDBNull(0) ? (int?)null : reader.GetInt32(0);
        var recoveryId = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);
        var prior = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
        var candidate = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3);
        var completed = !reader.IsDBNull(5) && reader.GetBoolean(5);
        var role = activeVersion == identity.Version ? "active" : "candidate";
        if (recoveryId is not null)
        {
            if (candidate == identity.Version && reader.GetString(4) == lineage && !completed && activeVersion == prior)
            {
                role = "recovery_candidate";
            }
            else if (candidate == identity.Version && completed && activeVersion == identity.Version)
            {
                role = "active";
            }
            else
            {
                throw new InvalidOperationException("The projection recovery role cannot be certified for this marker.");
            }
        }
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Multiple recovery tickets cover one projection version.");
        }
        return new RetentionRole(role, activeVersion, recoveryId);
    }

    private static async ValueTask<RetentionTargetIdentity> ReadRetentionTargetIdentityAsync(
        DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = ProjectionSql.Command(connection, transaction, 30, """
            SELECT s.system_identifier::text,current_database(),d.oid,c.timeline_id::bigint,pg_is_in_recovery()
            FROM pg_catalog.pg_control_system() s CROSS JOIN pg_catalog.pg_control_checkpoint() c
            JOIN pg_catalog.pg_database d ON d.datname=current_database()
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetBoolean(4))
        {
            throw new InvalidOperationException("The protected projection target identity is unavailable or in recovery.");
        }
        return new RetentionTargetIdentity(reader.GetString(0), reader.GetString(1),
            reader.GetFieldValue<uint>(2), reader.GetInt64(3));
    }

    private sealed record RetentionTargetIdentity(string SystemIdentifier, string DatabaseName,
        uint DatabaseOid, long Timeline);
    private sealed record RetentionRole(string Name, int? ActiveVersion, Guid? RecoveryId);
}
