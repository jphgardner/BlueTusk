using System.Buffers;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BlueTusk.Events;

/// <summary>Schema of a configured protected target. The coordinator queries the database itself.</summary>
public enum EventPublishedRetentionRemoteKind { Events, Projection }

/// <summary>A trusted, operator-configured remote target endpoint, not a caller-supplied ACK.</summary>
public sealed class EventPublishedRetentionRemoteTarget
{
    public EventPublishedRetentionRemoteTarget(string consumerGroup, Guid targetIncarnation,
        string endpointKey, DbDataSource dataSource, string schema, string transportSchema,
        EventPublishedRetentionRemoteKind kind, string? projection = null, int? projectionVersion = null)
    {
        ConsumerGroup = consumerGroup;
        TargetIncarnation = targetIncarnation;
        EndpointKey = endpointKey;
        DataSource = dataSource;
        Schema = schema;
        TransportSchema = transportSchema;
        Kind = kind;
        Projection = projection;
        ProjectionVersion = projectionVersion;
    }

    public string ConsumerGroup { get; }
    public Guid TargetIncarnation { get; }
    public string EndpointKey { get; }
    public DbDataSource DataSource { get; }
    public string Schema { get; }
    public string TransportSchema { get; }
    public EventPublishedRetentionRemoteKind Kind { get; }
    public string? Projection { get; }
    public int? ProjectionVersion { get; }
}

/// <summary>A historical proof observation. It never authorizes source retention or deletion.</summary>
public sealed class EventPublishedRetentionObservation
{
    internal EventPublishedRetentionObservation(Guid observationId, Guid retentionEpoch,
        long membershipRevision, ulong markerCommitEndPosition, int targetCount)
    {
        ObservationId = observationId;
        RetentionEpoch = retentionEpoch;
        MembershipRevision = membershipRevision;
        MarkerCommitEndPosition = markerCommitEndPosition;
        TargetCount = targetCount;
    }

    public Guid ObservationId { get; }
    public Guid RetentionEpoch { get; }
    public long MembershipRevision { get; }
    public ulong MarkerCommitEndPosition { get; }
    public int TargetCount { get; }
}

public sealed partial class PostgreSqlEventStore
{
    /// <summary>
    /// Read every registered target's durable ACK in its own repeatable-read snapshot, then append
    /// an observation only if source membership, lineage and slot transport still match under the
    /// stream row lock. The record is intentionally not a published retention floor.
    /// </summary>
    public async ValueTask<EventPublishedRetentionObservation> ObservePublishedRetentionIntentAsync(
        Guid retentionEpoch, IReadOnlyList<EventPublishedRetentionRemoteTarget> targets,
        int maximumTargets = 64, CancellationToken cancellationToken = default)
    {
        if (retentionEpoch == Guid.Empty) { throw new ArgumentException("A retention epoch is required.", nameof(retentionEpoch)); }
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumTargets);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumTargets, 64);
        if (targets.Count is 0 || targets.Count > maximumTargets)
        {
            throw new InvalidOperationException("The configured remote target set is empty or exceeds its bound.");
        }

        var endpoints = new Dictionary<(string Group, Guid Incarnation), EventPublishedRetentionRemoteTarget>();
        foreach (var target in targets)
        {
            ValidateRemoteTarget(target);
            if (!endpoints.TryAdd((target.ConsumerGroup, target.TargetIncarnation), target))
            {
                throw new InvalidOperationException("A protected target incarnation was configured more than once.");
            }
        }

        ObservedIntent intent;
        IReadOnlyList<EventPublishedConsumerRegistration> members;
        await using (var source = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var snapshot = await source.BeginTransactionAsync(IsolationLevel.RepeatableRead,
            cancellationToken).ConfigureAwait(false))
        {
            intent = await ReadObservedIntentAsync(source, snapshot, retentionEpoch, cancellationToken)
                .ConfigureAwait(false);
            members = await ReadPublishedMembersAsync(source, snapshot, intent, maximumTargets,
                cancellationToken).ConfigureAwait(false);
            var revision = await ReadPublishedMembershipRevisionAsync(source, snapshot, intent.Stream,
                cancellationToken).ConfigureAwait(false);
            if (revision != intent.MembershipRevision)
            {
                throw new InvalidOperationException("A newer protected member requires a newer ordered marker.");
            }
        }
        if (members.Count != targets.Count || members.Any(member =>
                !endpoints.ContainsKey((member.ConsumerGroup, member.TargetIncarnation))))
        {
            throw new InvalidOperationException("Every immutable protected source member needs exactly one configured target endpoint.");
        }

        var proofs = new List<RemoteProof>(members.Count);
        foreach (var member in members)
        {
            proofs.Add(await ReadRemoteProofAsync(endpoints[(member.ConsumerGroup, member.TargetIncarnation)],
                member, intent, cancellationToken).ConfigureAwait(false));
        }
        var markerPosition = proofs[0].MarkerCommitEndPosition;
        if (markerPosition == 0 || proofs.Any(proof => proof.MarkerCommitEndPosition != markerPosition ||
                proof.DurableCheckpoint < markerPosition || proof.TransportCheckpoint < markerPosition ||
                proof.TransportCheckpoint > proof.DurableCheckpoint))
        {
            throw new InvalidOperationException("Protected targets disagree on the ordered marker position or lack a durable target checkpoint.");
        }
        if (proofs.Where(proof => proof.SourceTransactionId is not null)
            .Select(proof => proof.SourceTransactionId).Distinct().Skip(1).Any())
        {
            throw new InvalidOperationException("Protected targets disagree on the source marker transaction ID.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var status = await LockRetentionStatusAsync(connection, transaction, intent.Stream, cancellationToken)
            .ConfigureAwait(false);
        if (status.LocalRetentionEnabled || status.RetainedThrough != 0 ||
            status.ArchivedThrough < intent.ThroughSequence)
        {
            throw new InvalidOperationException("The source archived prefix or retention mode changed.");
        }
        var currentIntent = await ReadObservedIntentAsync(connection, transaction, retentionEpoch,
            cancellationToken).ConfigureAwait(false);
        var currentMembers = await ReadPublishedMembersAsync(connection, transaction, currentIntent,
            maximumTargets, cancellationToken).ConfigureAwait(false);
        var currentRevision = await ReadPublishedMembershipRevisionAsync(connection, transaction,
            intent.Stream, cancellationToken).ConfigureAwait(false);
        if (!IntentEquals(intent, currentIntent) || currentRevision != intent.MembershipRevision ||
            !members.SequenceEqual(currentMembers))
        {
            throw new InvalidOperationException("Source membership or intent changed while remote proof was collected.");
        }

        await ExecuteAsync(connection, transaction,
            $"LOCK TABLE {_schema}.outbox, {_schema}.published_retention_intents IN SHARE UPDATE EXCLUSIVE MODE",
            cancellationToken).ConfigureAwait(false);
        if (!await HasOutboxIdentityFenceAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The outbox identity fence changed.");
        }
        await CheckControlPublicationCoverageAsync(connection, transaction,
            intent.Source.PublicationName, cancellationToken).ConfigureAwait(false);
        var (databaseOid, publicationOid) = await CheckPublishedSourceAsync(connection, transaction,
            intent.Source, cancellationToken).ConfigureAwait(false);
        if (databaseOid != intent.SourceDatabaseOid || publicationOid != intent.SourcePublicationOid)
        {
            throw new InvalidOperationException("The source database or publication OID changed.");
        }
        var confirmedFlush = await ReadConfirmedFlushAsync(connection, transaction, intent.Source.SlotName,
            cancellationToken).ConfigureAwait(false);
        if (confirmedFlush < markerPosition ||
            proofs.Any(proof => proof.TransportCheckpoint < confirmedFlush))
        {
            throw new InvalidOperationException("The logical slot's confirmed flush is behind the marker or ahead of a target checkpoint.");
        }

        var observationId = Guid.NewGuid();
        var evidence = SerializeRemoteProofs(proofs);
        var evidenceHash = SHA256.HashData(Encoding.UTF8.GetBytes(evidence));
        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO {_schema}.published_retention_observations
                (observation_id,retention_epoch,tenant_id,stream_id,membership_revision,
                 marker_commit_end_position,slot_confirmed_flush_position,target_count,
                 evidence_json,evidence_sha256)
            VALUES(@observation,@epoch,@tenant,@stream,@revision,@marker,@slot,@count,
                   CAST(@evidence AS jsonb),@hash)
            """, cancellationToken, ("observation", observationId), ("epoch", retentionEpoch),
            ("tenant", intent.Stream.TenantId), ("stream", intent.Stream.StreamId),
            ("revision", intent.MembershipRevision), ("marker", (decimal)markerPosition),
            ("slot", (decimal)confirmedFlush), ("count", proofs.Count),
            ("evidence", evidence), ("hash", evidenceHash)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new EventPublishedRetentionObservation(observationId, retentionEpoch,
            intent.MembershipRevision, markerPosition, proofs.Count);
    }

    private async ValueTask<ObservedIntent> ReadObservedIntentAsync(DbConnection connection,
        DbTransaction transaction, Guid epoch, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"""
            SELECT tenant_id,stream_id,first_sequence,through_sequence,archive_manifest_sha256,
                   membership_revision,source_system_identifier,source_database,source_database_oid,
                   source_timeline,source_slot,source_publication,source_publication_oid
            FROM {_schema}.published_retention_intents WHERE retention_epoch=@epoch
            """, ("epoch", epoch));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The published retention intent is absent.");
        }
        return new ObservedIntent(epoch, new EventStreamKey(reader.GetString(0), reader.GetString(1)),
            reader.GetInt64(2), reader.GetInt64(3), (byte[])reader.GetValue(4), reader.GetInt64(5),
            new EventPublishedSourceIdentity(reader.GetString(6), reader.GetString(7), reader.GetInt64(9),
                reader.GetString(10), reader.GetString(11)), reader.GetFieldValue<uint>(8),
            reader.GetFieldValue<uint>(12));
    }

    private async ValueTask<IReadOnlyList<EventPublishedConsumerRegistration>> ReadPublishedMembersAsync(
        DbConnection connection, DbTransaction transaction, ObservedIntent intent, int maximumTargets,
        CancellationToken cancellationToken)
    {
        var members = new List<EventPublishedConsumerRegistration>();
        await using var command = Command(connection, transaction, $"""
            SELECT consumer_group,target_incarnation,membership_revision,source_system_identifier,
                   source_database,source_database_oid,source_timeline,source_slot,
                   source_publication,source_publication_oid
            FROM {_schema}.published_retention_members
            WHERE tenant_id=@tenant AND stream_id=@stream
            ORDER BY consumer_group COLLATE "C",target_incarnation LIMIT 65
            """, ("tenant", intent.Stream.TenantId), ("stream", intent.Stream.StreamId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (members.Count == maximumTargets)
            {
                throw new InvalidOperationException("The protected membership exceeds the observation bound.");
            }
            var source = new EventPublishedSourceIdentity(reader.GetString(3), reader.GetString(4),
                reader.GetInt64(6), reader.GetString(7), reader.GetString(8));
            var member = new EventPublishedConsumerRegistration(intent.Stream, reader.GetString(0),
                reader.GetGuid(1), reader.GetInt64(2), source, reader.GetFieldValue<uint>(5),
                reader.GetFieldValue<uint>(9));
            if (member.Source != intent.Source || member.SourceDatabaseOid != intent.SourceDatabaseOid ||
                member.SourcePublicationOid != intent.SourcePublicationOid ||
                member.MembershipRevision > intent.MembershipRevision)
            {
                throw new InvalidOperationException("A protected source member has a different lineage or postdates the marker.");
            }
            members.Add(member);
        }
        if (members.Count == 0) { throw new InvalidOperationException("No protected source members were registered."); }
        return members;
    }

    private async ValueTask<long> ReadPublishedMembershipRevisionAsync(DbConnection connection,
        DbTransaction transaction, EventStreamKey stream, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"""
            SELECT published_membership_revision FROM {_schema}.streams
            WHERE tenant_id=@tenant AND stream_id=@stream
            """, ("tenant", stream.TenantId), ("stream", stream.StreamId));
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull
            ? throw new InvalidOperationException("The published source stream is absent.")
            : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private async ValueTask<ulong> ReadConfirmedFlushAsync(DbConnection connection,
        DbTransaction transaction, string slotName, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT pg_catalog.pg_wal_lsn_diff(r.confirmed_flush_lsn,'0/0'::pg_lsn),
                   pg_catalog.pg_wal_lsn_diff(r.restart_lsn,'0/0'::pg_lsn),r.wal_status
            FROM pg_catalog.pg_replication_slots r WHERE r.slot_name=@slot
            """, ("slot", slotName));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0) ||
            reader.IsDBNull(1) || reader.IsDBNull(2) || reader.GetString(2) is "lost" or "unreserved")
        {
            throw new InvalidOperationException("The source slot has no durable, retained confirmed-flush position.");
        }
        return checked((ulong)reader.GetDecimal(0));
    }

    private static bool IntentEquals(ObservedIntent left, ObservedIntent right) =>
        left.Epoch == right.Epoch && left.Stream == right.Stream &&
        left.FirstSequence == right.FirstSequence && left.ThroughSequence == right.ThroughSequence &&
        left.ArchiveManifestSha256.AsSpan().SequenceEqual(right.ArchiveManifestSha256) &&
        left.MembershipRevision == right.MembershipRevision && left.Source == right.Source &&
        left.SourceDatabaseOid == right.SourceDatabaseOid &&
        left.SourcePublicationOid == right.SourcePublicationOid;

    private static string SerializeRemoteProofs(IEnumerable<RemoteProof> proofs)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var proof in proofs.OrderBy(value => value.ConsumerGroup, StringComparer.Ordinal)
                .ThenBy(value => value.TargetIncarnation))
            {
                writer.WriteStartObject();
                writer.WriteString("consumerGroup", proof.ConsumerGroup);
                writer.WriteString("targetIncarnation", proof.TargetIncarnation);
                writer.WriteString("endpointKey", proof.EndpointKey);
                writer.WriteString("kind", proof.Kind.ToString());
                writer.WriteString("targetSystemIdentifier", proof.TargetSystemIdentifier);
                writer.WriteString("targetDatabase", proof.TargetDatabase);
                writer.WriteNumber("targetDatabaseOid", proof.TargetDatabaseOid);
                writer.WriteNumber("targetTimeline", proof.TargetTimeline);
                writer.WriteNumber("markerCommitEndPosition", proof.MarkerCommitEndPosition);
                writer.WriteNumber("durableCheckpoint", proof.DurableCheckpoint);
                writer.WriteNumber("transportCheckpoint", proof.TransportCheckpoint);
                if (proof.SourceTransactionId is null) { writer.WriteNull("sourceTransactionId"); }
                else { writer.WriteNumber("sourceTransactionId", proof.SourceTransactionId.Value); }
                if (proof.Projection is null) { writer.WriteNull("projection"); }
                else { writer.WriteString("projection", proof.Projection); }
                if (proof.ProjectionVersion is null) { writer.WriteNull("projectionVersion"); }
                else { writer.WriteNumber("projectionVersion", proof.ProjectionVersion.Value); }
                if (proof.ProjectionRole is null) { writer.WriteNull("projectionRole"); }
                else { writer.WriteString("projectionRole", proof.ProjectionRole); }
                if (proof.SnapshotEpoch is null) { writer.WriteNull("snapshotEpoch"); }
                else { writer.WriteString("snapshotEpoch", proof.SnapshotEpoch.Value); }
                if (proof.RecoveryId is null) { writer.WriteNull("recoveryId"); }
                else { writer.WriteString("recoveryId", proof.RecoveryId.Value); }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void ValidateRemoteTarget(EventPublishedRetentionRemoteTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        EventValidation.Key(target.ConsumerGroup, nameof(target.ConsumerGroup));
        ArgumentException.ThrowIfNullOrWhiteSpace(target.EndpointKey);
        ArgumentNullException.ThrowIfNull(target.DataSource);
        if (target.TargetIncarnation == Guid.Empty || target.EndpointKey.Length > 200 ||
            string.IsNullOrWhiteSpace(target.Schema) || Encoding.UTF8.GetByteCount(target.Schema) > 63 ||
            target.Schema.Contains('\0') || string.IsNullOrWhiteSpace(target.TransportSchema) ||
            Encoding.UTF8.GetByteCount(target.TransportSchema) > 63 ||
            target.TransportSchema.Contains('\0') || !Enum.IsDefined(target.Kind) ||
            (target.Kind == EventPublishedRetentionRemoteKind.Projection &&
             string.IsNullOrWhiteSpace(target.Projection)) ||
            (target.Kind == EventPublishedRetentionRemoteKind.Projection) !=
                (target.Projection is not null && target.ProjectionVersion is > 0) ||
            (target.Kind == EventPublishedRetentionRemoteKind.Events &&
             (target.Projection is not null || target.ProjectionVersion is not null)))
        {
            throw new ArgumentException("A protected remote target has an invalid endpoint, schema, kind or projection identity.", nameof(target));
        }
    }

    private static string QuoteRemoteSchema(string schema) =>
        '"' + schema.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    private sealed record ObservedIntent(Guid Epoch, EventStreamKey Stream, long FirstSequence,
        long ThroughSequence, byte[] ArchiveManifestSha256, long MembershipRevision,
        EventPublishedSourceIdentity Source, uint SourceDatabaseOid, uint SourcePublicationOid);

    private sealed record RemoteProof(string ConsumerGroup, Guid TargetIncarnation, string EndpointKey,
        EventPublishedRetentionRemoteKind Kind, string TargetSystemIdentifier, string TargetDatabase,
        uint TargetDatabaseOid, long TargetTimeline, ulong MarkerCommitEndPosition,
        ulong DurableCheckpoint, ulong TransportCheckpoint, long? SourceTransactionId,
        string? Projection, int? ProjectionVersion,
        string? ProjectionRole, Guid? SnapshotEpoch, Guid? RecoveryId);
}
