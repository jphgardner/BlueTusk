using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;

namespace BlueTusk.Events;

public sealed partial class PostgreSqlEventStore
{
    private async ValueTask<RemoteProof> ReadRemoteProofAsync(EventPublishedRetentionRemoteTarget endpoint,
        EventPublishedConsumerRegistration member, ObservedIntent intent,
        CancellationToken cancellationToken)
    {
        await using var connection = await endpoint.DataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var snapshot = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,
            cancellationToken).ConfigureAwait(false);
        var identity = await ReadRemoteIdentityAsync(connection, snapshot, cancellationToken)
            .ConfigureAwait(false);
        var schema = QuoteRemoteSchema(endpoint.Schema);
        var registration = await ReadRemoteRegistrationAsync(connection, snapshot, schema, endpoint,
            member, identity, cancellationToken).ConfigureAwait(false);
        var proof = endpoint.Kind == EventPublishedRetentionRemoteKind.Events
            ? await ReadRemoteEventsAckAsync(connection, snapshot, schema, endpoint, member, intent,
                identity, cancellationToken).ConfigureAwait(false)
            : await ReadRemoteProjectionAckAsync(connection, snapshot, schema, endpoint, member, intent,
                identity, registration, cancellationToken).ConfigureAwait(false);
        var transport = await ReadRemoteTransportCheckpointAsync(connection, snapshot, endpoint,
            member, cancellationToken).ConfigureAwait(false);
        if (transport < proof.MarkerCommitEndPosition || transport > proof.DurableCheckpoint)
        {
            throw new InvalidOperationException("The separate durable Streams transport checkpoint does not align with target effects.");
        }
        await snapshot.CommitAsync(cancellationToken).ConfigureAwait(false);
        return proof with { TransportCheckpoint = transport };
    }

    private async ValueTask<ulong> ReadRemoteTransportCheckpointAsync(DbConnection connection,
        DbTransaction transaction, EventPublishedRetentionRemoteTarget endpoint,
        EventPublishedConsumerRegistration member, CancellationToken cancellationToken)
    {
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', member.Source.SystemIdentifier, member.Source.DatabaseName,
                member.Source.SlotName, member.Source.PublicationName))));
        await using var command = Command(connection, transaction, $"""
            SELECT checkpoint_format,system_identifier,database_name,slot_name,
                   publication_fingerprint,output_plugin,database_identity,mapping_fingerprint,
                   acknowledged_position,store_generation
            FROM {QuoteRemoteSchema(endpoint.TransportSchema)}.stream_state
            WHERE source_fingerprint=@source AND consumer_group=@consumer
            """, ("source", fingerprint), ("consumer", member.ConsumerGroup));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.IsDBNull(0) || reader.GetInt32(0) != 1 ||
            reader.IsDBNull(1) || reader.GetString(1) != member.Source.SystemIdentifier ||
            reader.IsDBNull(2) || reader.GetString(2) != member.Source.DatabaseName ||
            reader.IsDBNull(3) || reader.GetString(3) != member.Source.SlotName ||
            reader.IsDBNull(4) || reader.GetString(4) != member.Source.PublicationName ||
            reader.IsDBNull(5) || reader.GetString(5) != "pgoutput" ||
            reader.IsDBNull(6) || string.IsNullOrWhiteSpace(reader.GetString(6)) ||
            reader.IsDBNull(7) || string.IsNullOrWhiteSpace(reader.GetString(7)) ||
            reader.IsDBNull(8) || reader.IsDBNull(9) || reader.GetInt64(9) < 0)
        {
            throw new InvalidOperationException("The durable Streams transport checkpoint is absent or belongs to a different source.");
        }
        return checked((ulong)reader.GetDecimal(8));
    }

    private async ValueTask<RemoteIdentity> ReadRemoteIdentityAsync(DbConnection connection,
        DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT s.system_identifier::text,current_database(),d.oid,c.timeline_id::bigint,pg_is_in_recovery()
            FROM pg_catalog.pg_control_system() s CROSS JOIN pg_catalog.pg_control_checkpoint() c
            JOIN pg_catalog.pg_database d ON d.datname=current_database()
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetBoolean(4))
        {
            throw new InvalidOperationException("The remote target identity is unavailable or in recovery.");
        }
        return new RemoteIdentity(reader.GetString(0), reader.GetString(1),
            reader.GetFieldValue<uint>(2), reader.GetInt64(3));
    }

    private async ValueTask<RemoteRegistration> ReadRemoteRegistrationAsync(DbConnection connection,
        DbTransaction transaction, string schema, EventPublishedRetentionRemoteTarget endpoint,
        EventPublishedConsumerRegistration member, RemoteIdentity identity,
        CancellationToken cancellationToken)
    {
        var projection = endpoint.Kind == EventPublishedRetentionRemoteKind.Projection;
        var extraColumns = projection
            ? "source_fingerprint,definition_fingerprint,projection_source_lineage"
            : "NULL::text,NULL::text,NULL::text";
        var extraWhere = projection ? "AND projection=@projection AND version=@version" : string.Empty;
        var parameters = new List<(string Name, object Value)>
        {
            ("tenant", member.Stream.TenantId), ("stream", member.Stream.StreamId),
            ("consumer", member.ConsumerGroup), ("incarnation", member.TargetIncarnation)
        };
        if (projection)
        {
            parameters.Add(("projection", endpoint.Projection!));
            parameters.Add(("version", endpoint.ProjectionVersion!.Value));
        }
        await using var command = Command(connection, transaction, $"""
            SELECT membership_revision,source_system_identifier,source_database,source_database_oid,
                   source_timeline,source_slot,source_publication,source_publication_oid,
                   target_system_identifier,target_database,target_database_oid,target_timeline,
                   {extraColumns}
            FROM {schema}.published_retention_targets
            WHERE tenant_id=@tenant AND stream_id=@stream AND consumer_group=@consumer
              AND target_incarnation=@incarnation {extraWhere}
            """, [.. parameters]);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetInt64(0) != member.MembershipRevision ||
            reader.GetString(1) != member.Source.SystemIdentifier ||
            reader.GetString(2) != member.Source.DatabaseName ||
            reader.GetFieldValue<uint>(3) != member.SourceDatabaseOid ||
            reader.GetInt64(4) != member.Source.Timeline ||
            reader.GetString(5) != member.Source.SlotName ||
            reader.GetString(6) != member.Source.PublicationName ||
            reader.GetFieldValue<uint>(7) != member.SourcePublicationOid ||
            reader.GetString(8) != identity.SystemIdentifier ||
            reader.GetString(9) != identity.DatabaseName ||
            reader.GetFieldValue<uint>(10) != identity.DatabaseOid ||
            reader.GetInt64(11) != identity.Timeline)
        {
            throw new InvalidOperationException("A remote target registration is missing or no longer matches source membership and current target identity.");
        }
        var registration = new RemoteRegistration(reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14));
        if (projection && (string.IsNullOrWhiteSpace(registration.SourceFingerprint) ||
            string.IsNullOrWhiteSpace(registration.DefinitionFingerprint) ||
            string.IsNullOrWhiteSpace(registration.SourceLineage)))
        {
            throw new InvalidOperationException("The projection target registration has no bound source or definition lineage.");
        }
        return registration;
    }

    private async ValueTask<RemoteProof> ReadRemoteEventsAckAsync(DbConnection connection,
        DbTransaction transaction, string schema, EventPublishedRetentionRemoteTarget endpoint,
        EventPublishedConsumerRegistration member, ObservedIntent intent, RemoteIdentity identity,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"""
            SELECT a.commit_end_position,c.commit_end_position,c.source_transaction_id
            FROM {schema}.published_retention_acknowledgements a
            JOIN {schema}.published_retention_target_checkpoints c
              ON c.consumer_group=a.consumer_group AND c.target_incarnation=a.target_incarnation
             AND c.source_system_identifier=a.source_system_identifier
             AND c.source_database=a.source_database AND c.source_slot=a.source_slot
             AND c.source_publication=a.source_publication AND c.source_timeline=a.source_timeline
            {RemoteAckPredicate}
            """, RemoteAckParameters(member, intent, identity));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The Events target has no exact durable marker ACK and checkpoint.");
        }
        var marker = checked((ulong)reader.GetDecimal(0));
        var checkpoint = checked((ulong)reader.GetDecimal(1));
        if (marker == 0 || checkpoint < marker)
        {
            throw new InvalidOperationException("The Events target checkpoint does not cover its marker ACK.");
        }
        return new RemoteProof(member.ConsumerGroup, member.TargetIncarnation, endpoint.EndpointKey,
            endpoint.Kind, identity.SystemIdentifier, identity.DatabaseName, identity.DatabaseOid,
            identity.Timeline, marker, checkpoint, 0,
            checkpoint == marker ? reader.GetInt64(2) : null,
            null, null, null, null, null);
    }

    private async ValueTask<RemoteProof> ReadRemoteProjectionAckAsync(DbConnection connection,
        DbTransaction transaction, string schema, EventPublishedRetentionRemoteTarget endpoint,
        EventPublishedConsumerRegistration member, ObservedIntent intent, RemoteIdentity identity,
        RemoteRegistration registration, CancellationToken cancellationToken)
    {
        var parameters = new List<(string Name, object Value)>(RemoteAckParameters(member, intent, identity))
        {
            ("projection", endpoint.Projection!), ("version", endpoint.ProjectionVersion!.Value)
        };
        await using var command = Command(connection, transaction, $"""
            SELECT a.commit_end_position,a.source_transaction_id,a.checkpoint,a.role,
                   a.snapshot_epoch,a.active_version,a.recovery_id,a.definition_fingerprint,
                   a.source_fingerprint,a.projection_source_lineage,
                   s.checkpoint,s.snapshot_epoch,s.definition_fingerprint,s.source_fingerprint,
                   s.source_lineage,s.phase,h.active_version,
                   r.recovery_id,r.active_version,r.candidate_version,r.target_lineage,
                   r.completed_at
            FROM {schema}.published_retention_acknowledgements a
            JOIN {schema}.state s ON s.projection=a.projection AND s.version=a.version
            JOIN {schema}.heads h ON h.projection=a.projection
            LEFT JOIN {schema}.recovery_tickets r
              ON r.projection=a.projection AND r.recovery_id=a.recovery_id
            {RemoteAckPredicate} AND a.projection=@projection AND a.version=@version
            """, [.. parameters]);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The projection target has no exact durable marker ACK.");
        }
        var marker = checked((ulong)reader.GetDecimal(0));
        var ackCheckpoint = checked((ulong)reader.GetDecimal(2));
        var currentCheckpoint = checked((ulong)reader.GetDecimal(10));
        var role = reader.GetString(3);
        var snapshot = reader.GetGuid(4);
        var ackActive = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5);
        var recovery = reader.IsDBNull(6) ? (Guid?)null : reader.GetGuid(6);
        var currentActive = reader.IsDBNull(16) ? (int?)null : reader.GetInt32(16);
        if (marker == 0 || ackCheckpoint != marker || currentCheckpoint < marker ||
            reader.GetInt32(15) != 2 || reader.IsDBNull(11) || reader.GetGuid(11) != snapshot ||
            reader.GetString(7) != registration.DefinitionFingerprint ||
            reader.GetString(8) != registration.SourceFingerprint ||
            reader.GetString(9) != registration.SourceLineage ||
            reader.GetString(12) != registration.DefinitionFingerprint ||
            reader.GetString(13) != registration.SourceFingerprint ||
            reader.IsDBNull(14) || reader.GetString(14) != registration.SourceLineage)
        {
            throw new InvalidOperationException("The projection checkpoint, snapshot, definition or lineage no longer certifies the marker.");
        }
        var version = endpoint.ProjectionVersion!.Value;
        var roleCurrent = role switch
        {
            "active" => currentActive == version && ackActive == version,
            "candidate" => currentActive != version && ackActive == currentActive && recovery is null,
            "recovery_candidate" => currentActive != version && ackActive == currentActive &&
                recovery is not null && !reader.IsDBNull(17) && reader.GetGuid(17) == recovery &&
                reader.GetInt32(18) == currentActive && reader.GetInt32(19) == version &&
                reader.GetString(20) == registration.SourceLineage && reader.IsDBNull(21),
            _ => false
        };
        if (!roleCurrent)
        {
            throw new InvalidOperationException("The projection ACK's historical role no longer matches the current head or recovery ticket.");
        }
        return new RemoteProof(member.ConsumerGroup, member.TargetIncarnation, endpoint.EndpointKey,
            endpoint.Kind, identity.SystemIdentifier, identity.DatabaseName, identity.DatabaseOid,
            identity.Timeline, marker, currentCheckpoint, 0, reader.GetInt64(1), endpoint.Projection,
            version, role, snapshot, recovery);
    }

    private const string RemoteAckPredicate = """
        WHERE a.retention_epoch=@epoch AND a.tenant_id=@tenant AND a.stream_id=@stream
          AND a.consumer_group=@consumer AND a.target_incarnation=@incarnation
          AND a.first_sequence=@first AND a.through_sequence=@through
          AND a.archive_manifest_sha256=@digest AND a.membership_revision=@revision
          AND a.source_system_identifier=@source_system AND a.source_database=@source_database
          AND a.source_database_oid=@source_database_oid AND a.source_timeline=@source_timeline
          AND a.source_slot=@source_slot AND a.source_publication=@source_publication
          AND a.source_publication_oid=@source_publication_oid
          AND a.target_system_identifier=@target_system AND a.target_database=@target_database
          AND a.target_database_oid=@target_database_oid AND a.target_timeline=@target_timeline
        """;

    private static (string Name, object Value)[] RemoteAckParameters(
        EventPublishedConsumerRegistration member, ObservedIntent intent, RemoteIdentity identity) =>
    [
        ("epoch", intent.Epoch), ("tenant", intent.Stream.TenantId), ("stream", intent.Stream.StreamId),
        ("consumer", member.ConsumerGroup), ("incarnation", member.TargetIncarnation),
        ("first", intent.FirstSequence), ("through", intent.ThroughSequence),
        ("digest", intent.ArchiveManifestSha256), ("revision", intent.MembershipRevision),
        ("source_system", intent.Source.SystemIdentifier), ("source_database", intent.Source.DatabaseName),
        ("source_database_oid", intent.SourceDatabaseOid), ("source_timeline", intent.Source.Timeline),
        ("source_slot", intent.Source.SlotName), ("source_publication", intent.Source.PublicationName),
        ("source_publication_oid", intent.SourcePublicationOid),
        ("target_system", identity.SystemIdentifier), ("target_database", identity.DatabaseName),
        ("target_database_oid", identity.DatabaseOid), ("target_timeline", identity.Timeline)
    ];

    private sealed record RemoteIdentity(string SystemIdentifier, string DatabaseName,
        uint DatabaseOid, long Timeline);
    private sealed record RemoteRegistration(string? SourceFingerprint,
        string? DefinitionFingerprint, string? SourceLineage);
}
